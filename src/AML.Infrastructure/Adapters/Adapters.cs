using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using AML.Application.Actions;
using AML.Application.Common;
using AML.Application.Realtime;
using AML.Application.Reporting;
using AML.Application.Reports;
using AML.Application.Screening;
using AML.Domain.Entities;
using AML.Domain.Services;
using AML.Infrastructure.Persistence;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AML.Infrastructure.Adapters;

/// <summary>
/// Internal watchlist screening adapter (local/test lists, internal lists). In production, an existing screening service is
/// integrated through <see cref="IScreeningProvider"/>; this adapter's simulation controls are ignored in Production.
/// </summary>
public sealed class InternalWatchlistScreeningProvider : IScreeningProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly INameMatcher _matcher;
    private readonly IEnvironmentInfo _env;

    public InternalWatchlistScreeningProvider(IServiceScopeFactory scopes, INameMatcher matcher, IEnvironmentInfo env)
    {
        _scopes = scopes; _matcher = matcher; _env = env;
    }

    public string Code => "INTERNAL-LISTS";

    public async Task<ProviderResponse> ScreenAsync(long tenantId, ScreeningSubject subject, IReadOnlyCollection<long>? onlyEntryIds, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmlDbContext>();
        if (!_env.IsProduction)
        {
            var status = await db.Set<ScreeningProviderStatus>().IgnoreQueryFilters().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProviderCode == Code, ct);
            if (status is not null && (status.SimulationMode != "Normal" || status.SimulatedFailuresRemaining > 0))
            {
                var mode = status.SimulationMode;
                if (status.SimulatedFailuresRemaining > 0)
                {
                    // Count-based simulation: fail N times, then the provider recovers automatically.
                    status.SimulatedFailuresRemaining--;
                    if (mode == "Normal") mode = "Unavailable";
                    if (status.SimulatedFailuresRemaining == 0) { status.SimulationMode = "Normal"; status.IsAvailable = true; }
                    await db.SaveChangesAsync(ct);
                }
                else if (mode is "FailTimes") mode = "Normal";
                switch (mode)
                {
                    case "Timeout": await Task.Delay(Timeout.Infinite, ct); break;
                    case "Unavailable": throw new ScreeningProviderException("Unavailable", "Provider returned HTTP 503 (simulated)");
                    case "Malformed": throw new ScreeningProviderException("Malformed", "Provider response could not be parsed (simulated malformed payload)");
                }
            }
        }
        var sources = await db.Set<WatchlistSource>().IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive).ToListAsync(ct);
        var ids = sources.Select(s => s.Id).ToList();
        var q = db.Set<WatchlistEntry>().IgnoreQueryFilters().AsNoTracking().Where(e => ids.Contains(e.SourceId) && e.Status == "Active");
        if (onlyEntryIds is not null) q = q.Where(e => onlyEntryIds.Contains(e.Id));
        var entries = await q.ToListAsync(ct);
        var matches = new List<ProviderMatch>();
        foreach (var e in entries)
        {
            var names = new List<string> { e.PrimaryName };
            if (e.AliasesJson is not null) names.AddRange(JsonSerializer.Deserialize<List<string>>(e.AliasesJson) ?? new());
            NameMatchResult? best = null;
            string? matchedName = null;
            foreach (var subjectName in new[] { subject.Name, subject.NameArabic }.Where(n => !string.IsNullOrWhiteSpace(n)))
                foreach (var n in names)
                {
                    var r = _matcher.Match(subjectName!, n);
                    if (best is null || r.Score > best.Score) { best = r; matchedName = n; }
                }
            if (best is null || best.Score < 0.70m) continue;
            var attrs = new Dictionary<string, object?> { ["name"] = new { subject = subject.Name, list = matchedName, score = best.Score } };
            var score = best.Score;
            if (subject.DateOfBirth is { } dob && e.DateOfBirth is { } edob)
            {
                attrs["dateOfBirth"] = new { match = dob == edob };
                if (dob != edob) score = Math.Round(score * 0.85m, 4);
            }
            if (subject.Nationality is { } nat && e.Nationality is { } enat) attrs["nationality"] = new { match = string.Equals(nat, enat, StringComparison.OrdinalIgnoreCase) };
            var source = sources.First(s => s.Id == e.SourceId);
            matches.Add(new ProviderMatch(e.SourceCode, e.EntryId, source.CurrentVersion ?? e.VersionAdded, e.EntryHash, e.PrimaryName, score, best.Method,
                best.Explanation + (score != best.Score ? "; date of birth differs (score reduced)" : ""), e.Category, JsonSerializer.Serialize(attrs)));
        }
        var raw = JsonSerializer.Serialize(new { provider = Code, screenedAt = DateTime.UtcNow, subject = new { subject.SubjectType, subject.SubjectId }, matches = matches.Select(m => new { m.SourceCode, m.ListEntryId, m.Score, m.Method }) });
        return new ProviderResponse(matches, raw);
    }
}

/// <summary>
/// Development/test core-system adapter backed by local tables. Idempotency keys guarantee a command is processed once.
/// Failure simulation via setting MockCore:FailureMode (None, Fail, Unknown, FailTimes:N). Real core integration is a
/// separate adapter that is only allowed in Production.
/// </summary>
public sealed class MockCoreSystemAdapter : ICoreSystemAdapter
{
    private readonly IServiceScopeFactory _scopes;
    public MockCoreSystemAdapter(IServiceScopeFactory scopes) => _scopes = scopes;
    public string Mode => "Mock (development sandbox)";

    public async Task<CoreResult> SendAsync(CoreCommand c, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmlDbContext>();
        var settings = await db.Set<SystemSetting>().IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == c.TenantId && s.Key == "MockCore:FailureMode", ct);
        var mode = settings?.Value ?? "None";
        if (mode.StartsWith("FailTimes:"))
        {
            var n = int.Parse(mode[10..], CultureInfo.InvariantCulture);
            if (n > 0) { settings!.Value = $"FailTimes:{n - 1}"; await db.SaveChangesAsync(ct); return new CoreResult("Failed", null, null, null, "Core unavailable (simulated)"); }
        }
        if (mode == "Fail") return new CoreResult("Failed", null, null, null, "Core unavailable (simulated)");
        var existing = await db.Set<MockCoreCommand>().FirstOrDefaultAsync(x => x.TenantId == c.TenantId && x.IdempotencyKey == c.IdempotencyKey, ct);
        if (existing is not null)
        {
            existing.TimesReceived++;
            await db.SaveChangesAsync(ct);
            var st = await db.Set<MockCoreTargetState>().FirstOrDefaultAsync(x => x.TenantId == c.TenantId && x.Target == c.Target, ct);
            return new CoreResult("Ack", existing.CoreReference, st?.Balance, st?.Currency, null);
        }
        var state = await db.Set<MockCoreTargetState>().FirstOrDefaultAsync(x => x.TenantId == c.TenantId && x.Target == c.Target, ct);
        if (state is null) db.Set<MockCoreTargetState>().Add(state = new MockCoreTargetState { TenantId = c.TenantId, Target = c.Target, Balance = 0, Currency = "JOD" });
        state.State = c.Command switch
        {
            ActionTypes.FreezeWallet or ActionTypes.BlockIncoming or ActionTypes.BlockOutgoing => "Frozen",
            ActionTypes.Release => "Normal", ActionTypes.SuspendAgent => "Suspended", ActionTypes.HoldTransaction => "Held",
            ActionTypes.ReleaseTransaction => "Released", ActionTypes.RejectTransaction => "Rejected", _ => state.State
        };
        state.UpdatedAt = DateTime.UtcNow;
        var cmd = new MockCoreCommand
        {
            TenantId = c.TenantId, IdempotencyKey = c.IdempotencyKey, Command = c.Command, Target = c.Target, CoreReference = "CORE-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            PayloadJson = JsonSerializer.Serialize(new { command = c.Command, target = c.Target, code = c.NeutralCode }), ReceivedAt = DateTime.UtcNow
        };
        db.Set<MockCoreCommand>().Add(cmd);
        await db.SaveChangesAsync(ct);
        if (mode == "Unknown") return new CoreResult("Unknown", null, null, null, "No acknowledgement received before timeout (simulated)");
        return new CoreResult("Ack", cmd.CoreReference, state.Balance, state.Currency, null);
    }

    public async Task<CoreResult?> LookupAsync(long tenantId, string idempotencyKey, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmlDbContext>();
        var cmd = await db.Set<MockCoreCommand>().AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, ct);
        if (cmd is null) return null;
        var st = await db.Set<MockCoreTargetState>().AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Target == cmd.Target, ct);
        return new CoreResult("Ack", cmd.CoreReference, st?.Balance, st?.Currency, null);
    }

    public async Task<string?> TargetStateAsync(long tenantId, string target, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmlDbContext>();
        return await db.Set<MockCoreTargetState>().AsNoTracking().Where(x => x.TenantId == tenantId && x.Target == target).Select(x => x.State).FirstOrDefaultAsync(ct);
    }
}

/// <summary>
/// goAML-style XML adapter. The element set and embedded XSD are a structural baseline that MUST be replaced by the exact
/// schema/version published by the national FIU before production use.
/// </summary>
public sealed class GoAmlXmlAdapter : IFiuReportAdapter
{
    public string Format => "goAML-XML (baseline; confirm schema with FIU)";

    private const string Xsd = """
<?xml version="1.0" encoding="utf-8"?>
<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" elementFormDefault="qualified">
  <xs:element name="report">
    <xs:complexType>
      <xs:sequence>
        <xs:element name="rentity_id" type="nonEmpty"/>
        <xs:element name="submission_code" type="nonEmpty"/>
        <xs:element name="report_code" type="nonEmpty"/>
        <xs:element name="entity_reference" type="nonEmpty"/>
        <xs:element name="previous_report_reference" type="xs:string" minOccurs="0"/>
        <xs:element name="submission_date" type="xs:dateTime"/>
        <xs:element name="currency_code_local" type="nonEmpty"/>
        <xs:element name="reason" type="nonEmpty"/>
        <xs:element name="report_indicators" minOccurs="1">
          <xs:complexType><xs:sequence><xs:element name="indicator" type="nonEmpty" maxOccurs="unbounded"/></xs:sequence></xs:complexType>
        </xs:element>
        <xs:element name="subjects">
          <xs:complexType><xs:sequence>
            <xs:element name="t_person" maxOccurs="unbounded">
              <xs:complexType><xs:sequence>
                <xs:element name="full_name" type="nonEmpty"/>
                <xs:element name="full_name_local" type="xs:string" minOccurs="0"/>
                <xs:element name="subject_type" type="nonEmpty"/>
                <xs:element name="birthdate" type="xs:date" minOccurs="0"/>
                <xs:element name="nationality1" type="xs:string" minOccurs="0"/>
                <xs:element name="identification" minOccurs="0">
                  <xs:complexType><xs:sequence><xs:element name="type" type="nonEmpty"/><xs:element name="number" type="nonEmpty"/></xs:sequence></xs:complexType>
                </xs:element>
                <xs:element name="account" type="nonEmpty" minOccurs="0" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType>
            </xs:element>
          </xs:sequence></xs:complexType>
        </xs:element>
        <xs:element name="transactions" minOccurs="0">
          <xs:complexType><xs:sequence>
            <xs:element name="transaction" minOccurs="0" maxOccurs="unbounded">
              <xs:complexType><xs:sequence>
                <xs:element name="transactionnumber" type="nonEmpty"/>
                <xs:element name="date_transaction" type="xs:dateTime"/>
                <xs:element name="amount_local" type="xs:decimal"/>
                <xs:element name="currency" type="nonEmpty"/>
                <xs:element name="transmode_code" type="nonEmpty"/>
                <xs:element name="from_account" type="xs:string" minOccurs="0"/>
                <xs:element name="to_account" type="xs:string" minOccurs="0"/>
              </xs:sequence></xs:complexType>
            </xs:element>
          </xs:sequence></xs:complexType>
        </xs:element>
      </xs:sequence>
    </xs:complexType>
  </xs:element>
  <xs:simpleType name="nonEmpty"><xs:restriction base="xs:string"><xs:minLength value="1"/></xs:restriction></xs:simpleType>
</xs:schema>
""";

    private static readonly Lazy<XmlSchemaSet> Schemas = new(() =>
    {
        var set = new XmlSchemaSet();
        using var r = XmlReader.Create(new StringReader(Xsd));
        set.Add(null, r);
        set.Compile();
        return set;
    });

    public string BuildXml(FiuPayload p)
    {
        XElement? Opt(string name, object? v) => v is null || v is string s && string.IsNullOrWhiteSpace(s) ? null : new XElement(name, v);
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("report",
                new XElement("rentity_id", p.ReportingEntityId),
                new XElement("submission_code", "E"),
                new XElement("report_code", p.ReportType),
                new XElement("entity_reference", p.ReportNumber),
                Opt("previous_report_reference", p.PreviousReportNumber),
                new XElement("submission_date", p.GeneratedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                new XElement("currency_code_local", "JOD"),
                p.Narrative is null ? null : new XElement("reason", p.Narrative),
                p.Indicators.Count == 0 ? null : new XElement("report_indicators", p.Indicators.Select(i => new XElement("indicator", i))),
                new XElement("subjects", p.Subjects.Select(s => new XElement("t_person",
                    new XElement("full_name", s.Name),
                    Opt("full_name_local", s.NameArabic),
                    new XElement("subject_type", s.CustomerType),
                    s.DateOfBirth is null ? null : new XElement("birthdate", s.DateOfBirth.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    Opt("nationality1", s.Nationality),
                    s.IdNumber is null ? null : new XElement("identification", new XElement("type", s.IdType ?? "ID"), new XElement("number", s.IdNumber)),
                    s.Accounts.Select(a => new XElement("account", a))))),
                p.Transactions.Count == 0 ? null : new XElement("transactions", p.Transactions.Select(t => new XElement("transaction",
                    new XElement("transactionnumber", t.Reference),
                    new XElement("date_transaction", t.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                    new XElement("amount_local", t.Amount.ToString(CultureInfo.InvariantCulture)),
                    new XElement("currency", t.Currency),
                    new XElement("transmode_code", t.Type),
                    Opt("from_account", t.From),
                    Opt("to_account", t.To))))));
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new Utf8StringWriter(sb), new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 })) doc.Save(w);
        return sb.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder sb) : base(sb, CultureInfo.InvariantCulture) { }
        public override Encoding Encoding => Encoding.UTF8;
    }

    public IReadOnlyList<string> Validate(string xml)
    {
        var errors = new List<string>();
        try
        {
            var doc = XDocument.Parse(xml);
            doc.Validate(Schemas.Value, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        }
        catch (XmlException ex)
        {
            errors.Add("Malformed XML: " + ex.Message);
        }
        return errors;
    }
}

/// <summary>Sandbox FIU channel used in development/test: nothing leaves the platform.</summary>
public sealed class SandboxFiuChannel : IFiuSubmissionChannel
{
    private readonly ILogger<SandboxFiuChannel> _log;
    public SandboxFiuChannel(ILogger<SandboxFiuChannel> log) => _log = log;
    public string Name => "Sandbox (non-production)";

    public Task<(bool Accepted, string? Reference, string? Message)> SubmitAsync(string reportNumber, string xml, CancellationToken ct)
    {
        _log.LogInformation("Sandbox FIU submission recorded for {ReportNumber} ({Bytes} bytes); no external call made", reportNumber, xml.Length);
        return Task.FromResult<(bool, string?, string?)>((true, null, "Recorded in sandbox; record the FIU reference when acknowledged."));
    }
}

/// <summary>Production placeholder: electronic FIU submission is not configured, manual export/upload must be used.</summary>
public sealed class ManualOnlyFiuChannel : IFiuSubmissionChannel
{
    public string Name => "ManualOnly";
    public Task<(bool Accepted, string? Reference, string? Message)> SubmitAsync(string reportNumber, string xml, CancellationToken ct) =>
        Task.FromResult<(bool, string?, string?)>((false, null, "Electronic FIU channel not configured; export the XML and record the manual submission."));
}

public sealed class ExportWriter : IExportWriter
{
    public byte[] Write(ReportTable t, string format) => format == "xlsx" ? Xlsx(t) : Csv(t);

    /// <summary>Prevents spreadsheet formula injection: values beginning with =, +, -, @, tab or CR are prefixed with a quote.</summary>
    public static string Safe(object? v)
    {
        var s = v switch
        {
            null => "",
            DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };
        if (s.Length > 0 && v is string && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s;
    }

    private static byte[] Csv(ReportTable t)
    {
        var sb = new StringBuilder();
        static string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        sb.AppendLine(string.Join(",", t.Columns.Select(Q)));
        foreach (var r in t.Rows) sb.AppendLine(string.Join(",", t.Columns.Select(c => Q(Safe(r.GetValueOrDefault(c))))));
        var bom = Encoding.UTF8.GetPreamble();
        return bom.Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static byte[] Xlsx(ReportTable t)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(t.Code.Length > 31 ? t.Code[..31] : t.Code);
        for (var i = 0; i < t.Columns.Count; i++) ws.Cell(1, i + 1).Value = t.Columns[i];
        var row = 2;
        foreach (var r in t.Rows)
        {
            for (var i = 0; i < t.Columns.Count; i++)
            {
                var v = r.GetValueOrDefault(t.Columns[i]);
                var cell = ws.Cell(row, i + 1);
                switch (v)
                {
                    case int n: cell.Value = n; break;
                    case long n: cell.Value = n; break;
                    case decimal d: cell.Value = d; break;
                    case double d: cell.Value = d; break;
                    case bool b: cell.Value = b; break;
                    default: cell.SetValue(Safe(v)); break;
                }
            }
            row++;
        }
        ws.Row(1).Style.Font.Bold = true;
        if (t.Notes is not null) wb.AddWorksheet("Notes").Cell(1, 1).Value = t.Notes;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
