using System.Text.Json.Nodes;
using AML.Domain.Entities;
using AML.Domain.Rules;
using AML.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.TransactionMonitoring;

/// <summary>Own database for the transaction-monitoring scenario workbook (AML_Transaction_Monitoring_Test_Cases.xlsx).</summary>
public sealed class TmFactory : AmlFactory { }

[CollectionDefinition("tm")]
public sealed class TmCollection : ICollectionFixture<TmFactory> { }

/// <summary>
/// Base for scenario tests. Each scenario is configured as a rule through the public API, data is ingested through the
/// integration API, the rule is executed and alerts are inspected. Every workbook test case is verified individually and
/// reported as a "TMRESULT|id|status|actual" line; the test method fails if any case fails.
/// </summary>
[Collection("tm")]
public abstract class TmBase : AmlTestBase
{
    private readonly List<string> _failures = new();
    protected TmBase(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    /// <summary>Local (Asia/Amman, UTC+3) wall-clock time on a scenario-specific base date, returned as UTC.</summary>
    protected static DateTime L(int scenarioDay, string hhmmss, int addDays = 0)
    {
        var t = TimeSpan.Parse(hhmmss);
        return DateTime.SpecifyKind(new DateTime(2025, 1, 1).AddDays(scenarioDay * 3 + addDays).Add(t).AddHours(-3), DateTimeKind.Utc);
    }

    /// <summary>Local Asia/Amman wall-clock date-time (UTC+3) returned as UTC.</summary>
    protected static DateTime At(int y, int m, int d, string hhmmss) =>
        DateTime.SpecifyKind(new DateTime(y, m, d).Add(TimeSpan.Parse(hhmmss)).AddHours(-3), DateTimeKind.Utc);

    protected void Result(string tc, bool ok, string actual)
    {
        Out.WriteLine($"TMRESULT|{tc}|{(ok ? "Pass" : "Fail")}|{actual}");
        if (!ok) _failures.Add($"{tc}: {actual}");
    }

    /// <summary>Records a case that cannot be executed because the scenario cannot be configured (capability gap).</summary>
    protected void Blocked(string tc, string reason) => Out.WriteLine($"TMRESULT|{tc}|Blocked|{reason}");

    protected void Done() => _failures.Should().BeEmpty();

    protected async Task<(long Id, string W)> Cust(string type = "Individual", string region = "CENTRAL", object? attributes = null, object? financialProfile = null,
        bool isPep = false, string? name = null, string? registrationDate = null, long? agentId = null, string? externalId = null)
    {
        var api = await As("manager");
        attributes ??= type == "Individual" ? new Dictionary<string, object> { ["OCCUPATION"] = "EMPLOYEE", ["SOURCE_OF_INCOME"] = "SALARY" } : new Dictionary<string, object>();
        var c = await api.PostOk("/api/customers", new
        {
            sourceSystem = "CORE", externalId = externalId ?? U("TM"), customerType = type, nameLatin = name ?? "Tm Subject " + Guid.NewGuid().ToString("N")[..6],
            nationalityCountry = type == "Individual" ? "JO" : null, residenceCountry = type == "Individual" ? "JO" : null, incorporationCountry = type == "Individual" ? null : "JO",
            region, attributes, financialProfile, isPep, registrationDate, onboardingAgentId = agentId
        });
        return (Id(c), await CreateWallet(Id(c)));
    }

    /// <summary>Test-data setup: records the customer's profile and dynamic field values as captured on 2024-01-01 so historical (point-in-time) evaluation sees them.</summary>
    protected async Task Backdate(params long[] customers)
    {
        var ids = string.Join(",", customers);
        await F.ExecSql($"UPDATE CustomerFieldValues SET EffectiveFrom = '2024-01-01' WHERE CustomerId IN ({ids}); UPDATE CustomerFinancialProfiles SET EffectiveFrom = '2024-01-01' WHERE CustomerId IN ({ids});");
    }

    protected Task<string> Wallet(long customer) => CreateWallet(customer);

    private int _seq;
    protected object T(string from, string to, decimal amount, DateTime at, string type = "P2P", string status = "SUCC", string channel = "Mobile",
        string currency = "JOD", string? src = "JO", string? dst = "JO", string? original = null, bool reversal = false, bool refund = false,
        string? remarks = null, string? device = null, string? agent = null, string? id = null, object? originator = null, object? beneficiary = null, string? reference = null) =>
        Txn(id ?? $"TM-{Guid.NewGuid():N}"[..20] + (++_seq), from, to, amount, at, type, status, currency, channel, reference, original, reversal, refund, src, dst,
            originator, beneficiary, remarks, device, agent);

    protected async Task Send(IEnumerable<object> txns)
    {
        foreach (var chunk in txns.Chunk(2000)) await Ingest(chunk);
    }

    protected async Task<long> Rule(object definition, string severity = "Medium") => await PublishRule(definition, severity: severity);

    protected async Task<JsonNode> Run(long rule, DateTime from, DateTime to) => await RunRule(rule, from, to);

    protected Task<List<AmlAlert>> Alerts(long rule, long customer) =>
        F.WithDb(db => db.Set<AmlAlert>().Where(a => a.RuleId == rule && a.PrimaryCustomerId == customer && !a.IsBacktest).OrderBy(a => a.Id).ToListAsync());

    protected Task<List<AmlAlert>> AlertsBySubject(long rule, string subjectKey) =>
        F.WithDb(db => db.Set<AmlAlert>().Where(a => a.RuleId == rule && a.SubjectKey == subjectKey && !a.IsBacktest).OrderBy(a => a.Id).ToListAsync());

    protected Task<List<AmlAlertRuleMatch>> Matches(long alertId) =>
        F.WithDb(db => db.Set<AmlAlertRuleMatch>().Where(m => m.AlertId == alertId).OrderBy(m => m.Id).ToListAsync());

    protected Task<int> TxnCount(long alertId) => F.WithDb(db => db.Set<AmlAlertTransaction>().CountAsync(x => x.AlertId == alertId));

    /// <summary>Validates a rule definition through the API and returns the validator's errors (used to evidence capability gaps).</summary>
    protected static List<string> Validate(object def)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(def);
        return RuleValidator.ValidateJson(json, new Dictionary<string, string>()).Errors.Select(e => $"{e.Path}: {e.Message}").ToList();
    }


    /// <summary>Creates, approves and publishes a new version of an existing rule.</summary>
    protected async Task NewVersion(long rule, object definition)
    {
        var officer = await As("officer");
        var v = await officer.PostOk($"/api/admin/aml-rules/{rule}/versions");
        await officer.PutOk($"/api/admin/aml-rules/{rule}/versions/{Id(v)}", new { definition, rowVersion = v["rowVersion"]!.GetValue<string>() });
        await officer.PostOk($"/api/admin/aml-rules/{rule}/simulate", new { from = DateTime.UtcNow.AddDays(-1), to = DateTime.UtcNow });
        await officer.PostOk($"/api/admin/aml-rules/{rule}/submit", new { rationale = "tm", testEvidence = "sim", expectedImpact = "low" });
        await (await As("senior")).PostOk($"/api/admin/aml-rules/{rule}/approve", new { comment = "ok" });
        await officer.PostOk($"/api/admin/aml-rules/{rule}/publish");
    }

    /// <summary>Publishes a real-time-capable rule (inline evaluation in the decision API).</summary>
    protected async Task<long> RealtimeRule(string code, object definition, string severity = "High")
    {
        var officer = await As("officer");
        var rt = await officer.PostOk("/api/admin/aml-rules", new { code, name = "TM realtime " + code, category = "TM", ruleType = "Realtime", severity, definition });
        await officer.PostOk($"/api/admin/aml-rules/{Id(rt)}/simulate", new { from = DateTime.UtcNow.AddDays(-1), to = DateTime.UtcNow });
        await officer.PostOk($"/api/admin/aml-rules/{Id(rt)}/submit", new { rationale = "rt", testEvidence = "sim", expectedImpact = "low" });
        await (await As("senior")).PostOk($"/api/admin/aml-rules/{Id(rt)}/approve", new { comment = "ok" });
        await officer.PostOk($"/api/admin/aml-rules/{Id(rt)}/publish");
        return Id(rt);
    }

    protected async Task<JsonNode> Decide(object body) => await (await F.ClientAsync()).PostOk("/api/realtime/transactions/decide", body);

    /// <summary>Creates and publishes a dynamic customer field.</summary>
    protected async Task Field(string code, string type, string customerTypes = "Individual")
    {
        var api = await As("manager");
        var f = await api.PostOk("/api/admin/customer-fields", new { code, labelEn = code, labelAr = "حقل " + code, groupCode = "CORE_DETAILS", dataType = type, customerTypes });
        await api.PostOk($"/api/admin/customer-fields/{f["id"]}/publish", new { });
    }

    protected async Task AddListEntry(string list, string name, string entryId)
    {
        await (await As("manager")).PostOk($"/api/watchlists/{list}/versions", new
        {
            versionLabel = "V-" + Guid.NewGuid().ToString("N")[..10], publishedAt = DateTime.UtcNow.AddHours(-1),
            additions = new[] { new { entryId, primaryName = name, dateOfBirth = (string?)null, nationality = (string?)null, category = (string?)null } }
        });
        AML.Application.Realtime.RealtimeService.InvalidateCache(1);
    }

    protected async Task RemoveListEntry(string list, string entryId)
    {
        await (await As("manager")).PostOk($"/api/watchlists/{list}/versions", new
        {
            versionLabel = "V-" + Guid.NewGuid().ToString("N")[..10], publishedAt = DateTime.UtcNow.AddHours(-1), additions = Array.Empty<object>(), removals = new[] { entryId }
        });
        AML.Application.Realtime.RealtimeService.InvalidateCache(1);
    }

    protected static object Rt(string? sender, decimal amount, string type = "P2P", string? beneficiary = null, string? remarks = null, string? dst = null, string? src = "JO",
        string currency = "JOD", string channel = "Mobile") => new
    {
        sourceTransactionId = "TMRT-" + Guid.NewGuid().ToString("N")[..12], transactionType = type, channel, amount, currency, senderWallet = sender, receiverWallet = "W-RT-" + Guid.NewGuid().ToString("N")[..6],
        beneficiaryName = beneficiary, remarks, sourceCountry = src, destinationCountry = dst
    };

    protected static object Cnt(object? filter = null, string op = "gte", object? value = null, string? direction = null, string? label = null) =>
        new { aggregate = "count", filter, @operator = op, value, direction, label };

    protected static object Sum(object? filter = null, string op = "gte", object? value = null, string? direction = null, string? label = null) =>
        new { aggregate = "sum", filter, @operator = op, value, direction, label };

    protected static object F_(string field, string op, object value) => new { field, @operator = op, value };

    protected static object And(params object[] c) => new { op = "and", conditions = c };
    protected static object Or(params object[] c) => new { op = "or", conditions = c };
}
