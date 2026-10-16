using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AML.Application.Common;
using AML.Domain.Services;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Reporting;

/// <summary>
/// Currency (cash) transaction reports. For each customer and local business date, eligible cash transactions
/// (Reporting:CtrTransactionTypes, completed, net of reversals, in the base currency) are aggregated; a total at or above the
/// CTR_THRESHOLD jurisdiction value creates a draft report. Drafts are approved (MLRO), exported and recorded as submitted
/// manually: nothing is ever sent to the FIU automatically. Threshold, types and deadline are to be confirmed by compliance.
/// </summary>
public sealed class CtrService
{
    public const decimal DefaultThreshold = 10000m;
    public const int DefaultDueDays = 15;

    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly AccessPolicy _access;
    private readonly TenantSettingsService _settings;

    public CtrService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, AccessPolicy access, TenantSettingsService settings)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _access = access; _settings = settings;
    }

    public sealed record GenerateResult(DateOnly BusinessDate, decimal Threshold, string BaseCurrency, IReadOnlyList<string> TransactionTypes, int CustomersEvaluated,
        int Created, int Updated, int UnchangedFiled, int ExcludedMissingFx);

    /// <summary>Generates or refreshes draft reports for one business date (idempotent; filed reports are never changed).</summary>
    public async Task<GenerateResult> GenerateAsync(DateOnly businessDate, CancellationToken ct)
    {
        if (!_user.IsSystem) Guard.Require(_user, Perm.StrPrepare);
        var tenantId = _user.TenantId;
        var tenant = await _settings.TenantAsync(tenantId, ct);
        var at = businessDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var threshold = await _settings.JurisdictionDecimalAsync("CTR_THRESHOLD",
            await _settings.DecimalSettingAsync("Reporting:CtrThreshold", DefaultThreshold, tenantId, ct), at, tenantId, ct);
        var dueDays = (int)await _settings.JurisdictionDecimalAsync("CTR_FILING_DEADLINE", DefaultDueDays, at, tenantId, ct);
        var types = RiskScoringEngine.Split(await _settings.SettingAsync("Reporting:CtrTransactionTypes", "CashIn,CashOut", tenantId, ct)).ToList();

        var txns = await _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.BusinessDate == businessDate && types.Contains(t.TransactionType) && !t.IsReversal && t.EconomicLegOfId == null &&
                        (t.Status == TxnStatuses.Completed || t.Status == TxnStatuses.PartiallyReversed))
            .Select(t => new { t.Id, t.SenderCustomerId, t.ReceiverCustomerId, t.AgentCustomerId, t.Amount, t.ReversedAmount, t.BaseAmount, t.IsTestData })
            .ToListAsync(ct);
        var excluded = txns.Count(t => t.BaseAmount is null);
        var rows = new List<(long Customer, long Txn, decimal Base, bool In, bool Test)>();
        foreach (var t in txns.Where(t => t.BaseAmount is not null))
        {
            var net = t.Amount == 0 ? 0 : Math.Round(t.BaseAmount!.Value * (t.Amount - t.ReversedAmount) / t.Amount, 4);
            if (net <= 0) continue;
            // The agent handling the cash is not the reporting subject; the customer depositing or withdrawing is.
            if (t.ReceiverCustomerId is { } r && r != t.AgentCustomerId) rows.Add((r, t.Id, net, true, t.IsTestData));
            if (t.SenderCustomerId is { } s && s != t.AgentCustomerId && s != t.ReceiverCustomerId) rows.Add((s, t.Id, net, false, t.IsTestData));
        }
        // Agents and other excluded customer types (Reporting:CtrExcludedCustomerTypes) handle cash for others and are not subjects.
        var excludedTypes = RiskScoringEngine.Split(await _settings.SettingAsync("Reporting:CtrExcludedCustomerTypes", "Agent", tenantId, ct)).ToList();
        var subjectIds = rows.Select(x => x.Customer).Distinct().ToList();
        var excludedCustomers = new HashSet<long>();
        foreach (var chunk in subjectIds.Chunk(1000))
        {
            var part = chunk.ToList();
            excludedCustomers.UnionWith(await _db.Set<Customer>().IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId && part.Contains(c.Id) && excludedTypes.Contains(c.CustomerType)).Select(c => c.Id).ToListAsync(ct));
        }
        int created = 0, updated = 0, filed = 0;
        var groups = rows.Where(x => !excludedCustomers.Contains(x.Customer)).GroupBy(x => x.Customer).ToList();
        foreach (var g in groups)
        {
            var total = g.Sum(x => x.Base);
            if (total < threshold) continue;
            var existing = await _db.Set<CurrencyTransactionReport>().FirstOrDefaultAsync(x => x.CustomerId == g.Key && x.BusinessDate == businessDate, ct);
            var ids = g.Select(x => x.Txn).Distinct().OrderBy(x => x).ToList();
            if (existing is not null && existing.Status != CtrStatuses.Draft)
            {
                filed++;
                continue;
            }
            var isTest = g.Any(x => x.Test) || await _db.Set<Customer>().IgnoreQueryFilters().AnyAsync(c => c.Id == g.Key && c.IsTestData, ct);
            var r = existing ?? new CurrencyTransactionReport
            {
                TenantId = tenantId, ReportNumber = "PENDING-" + Guid.NewGuid().ToString("N"), CustomerId = g.Key, BusinessDate = businessDate, Status = CtrStatuses.Draft,
                CreatedAt = _clock.UtcNow, CreatedBy = _user.Username
            };
            r.TransactionCount = ids.Count;
            r.TotalBaseAmount = total;
            r.CashInBaseAmount = g.Where(x => x.In).Sum(x => x.Base);
            r.CashOutBaseAmount = g.Where(x => !x.In).Sum(x => x.Base);
            r.BaseCurrency = tenant.BaseCurrency;
            r.Threshold = threshold;
            r.TransactionIdsJson = JsonSerializer.Serialize(ids);
            r.DueDate = businessDate.AddDays(dueDays);
            r.IsTestData = isTest;
            if (existing is null)
            {
                _db.Set<CurrencyTransactionReport>().Add(r);
                await _db.SaveChangesAsync(ct);
                r.ReportNumber = $"CTR-{businessDate:yyyyMMdd}-{r.Id:D6}";
                created++;
            }
            else updated++;
            _audit.Record(existing is null ? "CTR_GENERATE" : "CTR_REFRESH", "CurrencyTransactionReport", r.ReportNumber, null,
                new { r.CustomerId, businessDate, r.TotalBaseAmount, r.TransactionCount, threshold });
            await _db.SaveChangesAsync(ct);
        }
        return new GenerateResult(businessDate, threshold, tenant.BaseCurrency, types, groups.Count, created, updated, filed, excluded);
    }

    /// <summary>Scheduled generation for the last few local business dates (late-arriving cash transactions refresh drafts).</summary>
    public async Task<int> GenerateRecentAsync(long tenantId, CancellationToken ct)
    {
        var tz = await _settings.TimeZoneAsync(tenantId, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.UtcNow, tz));
        var days = await _settings.IntSettingAsync("Reporting:CtrLookbackDays", 3, tenantId, ct);
        var n = 0;
        for (var d = 1; d <= days; d++) n += (await GenerateAsync(today.AddDays(-d), ct)).Created;
        return n;
    }

    private IQueryable<CurrencyTransactionReport> Visible() =>
        _db.Set<CurrencyTransactionReport>().Where(r => _access.Customers().Any(c => c.Id == r.CustomerId));

    public async Task<List<object>> ListAsync(string? status, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var q = Visible().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        if (from is { } f) q = q.Where(r => r.BusinessDate >= f);
        if (to is { } t) q = q.Where(r => r.BusinessDate <= t);
        var list = await q.OrderByDescending(r => r.BusinessDate).ThenByDescending(r => r.Id).Take(500).ToListAsync(ct);
        var names = await NamesAsync(list.Select(r => r.CustomerId), ct);
        var today = DateOnly.FromDateTime(_clock.UtcNow);
        return list.Select(r => ToDto(r, names.GetValueOrDefault(r.CustomerId), today)).ToList();
    }

    public async Task<object> GetAsync(long id, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct, tracking: false);
        var names = await NamesAsync(new[] { r.CustomerId }, ct);
        var ids = JsonSerializer.Deserialize<List<long>>(r.TransactionIdsJson) ?? new();
        var txns = await _db.Set<Transaction>().AsNoTracking().Where(t => ids.Contains(t.Id)).OrderBy(t => t.TimestampUtc)
            .Select(t => new { t.Id, t.SourceTransactionId, t.TimestampUtc, t.TransactionType, t.Amount, t.Currency, t.BaseAmount, t.ReversedAmount, t.Channel, t.SenderWalletExternalId, t.ReceiverWalletExternalId })
            .ToListAsync(ct);
        return new { report = ToDto(r, names.GetValueOrDefault(r.CustomerId), DateOnly.FromDateTime(_clock.UtcNow)), transactions = txns };
    }

    public async Task<object> ApproveAsync(long id, string? comment, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrApprove);
        var r = await LoadAsync(id, ct);
        if (r.Status != CtrStatuses.Draft) throw new ConflictException("Only draft reports can be approved.");
        if (string.Equals(r.CreatedBy, _user.Username, StringComparison.OrdinalIgnoreCase)) throw new ForbiddenException("Separation of duties: the preparer cannot approve the report.");
        r.Status = CtrStatuses.Approved;
        r.ApprovedBy = _user.Username;
        r.ApprovedAt = _clock.UtcNow;
        if (!string.IsNullOrWhiteSpace(comment)) r.Notes = comment;
        _audit.Record("CTR_APPROVE", "CurrencyTransactionReport", r.ReportNumber, null, new { comment });
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<object> RejectAsync(long id, string reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrApprove);
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A reason is required.");
        var r = await LoadAsync(id, ct);
        if (r.Status != CtrStatuses.Draft) throw new ConflictException("Only draft reports can be rejected.");
        r.Status = CtrStatuses.Rejected;
        r.Notes = reason;
        _audit.Record("CTR_REJECT", "CurrencyTransactionReport", r.ReportNumber, null, new { reason });
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Export file (XML or CSV). The layout is a placeholder until the FIU's CTR schema is confirmed.</summary>
    public async Task<(string Content, string ContentType, string FileName)> ExportAsync(long id, string format, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrSubmit);
        var r = await LoadAsync(id, ct);
        if (r.Status is not (CtrStatuses.Approved or CtrStatuses.Exported or CtrStatuses.Submitted)) throw new ConflictException("Only approved reports can be exported.");
        var tenant = await _settings.TenantAsync(r.TenantId, ct);
        var c = await _db.Set<Customer>().AsNoTracking().FirstAsync(x => x.Id == r.CustomerId, ct);
        var ids = JsonSerializer.Deserialize<List<long>>(r.TransactionIdsJson) ?? new();
        var txns = await _db.Set<Transaction>().AsNoTracking().Where(t => ids.Contains(t.Id)).OrderBy(t => t.TimestampUtc).ToListAsync(ct);
        string content, type, file;
        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder("report_number,business_date,customer_external_id,transaction_reference,timestamp_utc,type,amount,currency,base_amount,base_currency\n");
            foreach (var t in txns)
                sb.Append(Csv(r.ReportNumber)).Append(',').Append(r.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',').Append(Csv(c.ExternalId)).Append(',')
                  .Append(Csv(t.SourceTransactionId)).Append(',').Append(t.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',').Append(Csv(t.TransactionType)).Append(',')
                  .Append(t.Amount.ToString(CultureInfo.InvariantCulture)).Append(',').Append(t.Currency).Append(',').Append((t.BaseAmount ?? 0).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.BaseCurrency).Append('\n');
            (content, type, file) = (sb.ToString(), "text/csv", r.ReportNumber + ".csv");
        }
        else
        {
            var x = new XDocument(new XElement("ctr_report",
                new XAttribute("schema", "placeholder-to-be-confirmed-with-FIU"),
                new XElement("reporting_entity_id", tenant.ReportingEntityId ?? ""),
                new XElement("report_number", r.ReportNumber),
                new XElement("business_date", r.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new XElement("threshold", r.Threshold.ToString(CultureInfo.InvariantCulture), new XAttribute("currency", r.BaseCurrency)),
                new XElement("subject",
                    new XElement("external_id", c.ExternalId), new XElement("name", c.NameLatin ?? c.NameArabic ?? ""), new XElement("type", c.CustomerType),
                    new XElement("nationality", c.NationalityCountry ?? "")),
                new XElement("totals",
                    new XElement("count", r.TransactionCount), new XElement("total_base", r.TotalBaseAmount.ToString(CultureInfo.InvariantCulture)),
                    new XElement("cash_in_base", r.CashInBaseAmount.ToString(CultureInfo.InvariantCulture)), new XElement("cash_out_base", r.CashOutBaseAmount.ToString(CultureInfo.InvariantCulture))),
                new XElement("transactions", txns.Select(t => new XElement("transaction",
                    new XElement("reference", t.SourceTransactionId), new XElement("timestamp_utc", t.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                    new XElement("type", t.TransactionType), new XElement("amount", t.Amount.ToString(CultureInfo.InvariantCulture), new XAttribute("currency", t.Currency)),
                    new XElement("base_amount", (t.BaseAmount ?? 0).ToString(CultureInfo.InvariantCulture)))))));
            (content, type, file) = (x.ToString(), "application/xml", r.ReportNumber + ".xml");
        }
        if (r.Status == CtrStatuses.Approved) r.Status = CtrStatuses.Exported;
        r.ExportedAt ??= _clock.UtcNow;
        _audit.Record("CTR_EXPORT", "CurrencyTransactionReport", r.ReportNumber, null, new { format, hash = Transactions.IngestionService.Sha256(content) });
        await _db.SaveChangesAsync(ct);
        return (content, type, file);
    }

    public async Task<object> RecordSubmissionAsync(long id, string reference, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrSubmit);
        if (string.IsNullOrWhiteSpace(reference)) throw new ValidationException("reference", "The FIU submission reference is required.");
        var r = await LoadAsync(id, ct);
        if (r.Status != CtrStatuses.Exported) throw new ConflictException("Export the report before recording its manual submission.");
        if (r.IsTestData) throw new ConflictException("Demo/test reports can never be submitted.");
        r.Status = CtrStatuses.Submitted;
        r.SubmissionReference = reference.Trim();
        r.SubmittedAt = _clock.UtcNow;
        r.SubmittedBy = _user.Username;
        _audit.Record("CTR_MANUAL_SUBMISSION", "CurrencyTransactionReport", r.ReportNumber, null, new { reference });
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task<CurrencyTransactionReport> LoadAsync(long id, CancellationToken ct, bool tracking = true)
    {
        var q = Visible();
        if (!tracking) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("CTR", id);
    }

    private async Task<Dictionary<long, string?>> NamesAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await _db.Set<Customer>().AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.NameLatin ?? c.NameArabic, ct);
    }

    private static object ToDto(CurrencyTransactionReport r, string? customerName, DateOnly today) => new
    {
        r.Id, r.ReportNumber, r.CustomerId, customerName, r.BusinessDate, r.TransactionCount, r.TotalBaseAmount, r.CashInBaseAmount, r.CashOutBaseAmount, r.BaseCurrency,
        r.Threshold, r.Status, r.DueDate, isOverdue = r.Status is CtrStatuses.Draft or CtrStatuses.Approved or CtrStatuses.Exported && r.DueDate < today,
        r.CreatedAt, r.CreatedBy, r.ApprovedBy, r.ApprovedAt, r.ExportedAt, r.SubmissionReference, r.SubmittedAt, r.SubmittedBy, r.Notes, r.IsTestData,
        rowVersion = Convert.ToBase64String(r.RowVersion)
    };

    /// <summary>CSV cell: quoted when needed; a leading formula character is neutralised (spreadsheet injection).</summary>
    private static string Csv(string? v)
    {
        if (v is null) return "";
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
