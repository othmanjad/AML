using System.Globalization;
using System.Text.Json;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Reports;

public sealed class ReportFilters
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? GroupBy { get; set; }
    public bool IncludeSensitive { get; set; }
    public bool IncludeRestricted { get; set; }
    public string? DataScopeRegion { get; set; }
}

public sealed record ReportTable(string Code, string Title, IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, string? Notes = null);

/// <summary>Writes tabular exports (CSV, Excel). CSV/XLSX writers neutralize spreadsheet formula injection.</summary>
public interface IExportWriter
{
    byte[] Write(ReportTable table, string format);
}

/// <summary>Report generation from operational data. Generation honours the requester's permissions captured at request time.</summary>
public sealed class ReportService
{
    public static readonly Dictionary<string, string> Catalogue = new()
    {
        ["customers"] = "Customer register", ["risk-distribution"] = "Customer risk distribution", ["high-risk-customers"] = "High/critical risk customers",
        ["overdue-reviews"] = "Due/overdue customer reviews", ["kyc-gaps"] = "KYC gaps and expiry", ["transaction-activity"] = "Transaction activity",
        ["rule-executions"] = "Rule execution and match summary", ["alerts"] = "Alerts register", ["alerts-summary"] = "Alerts by status/severity/rule/owner/age",
        ["case-aging"] = "Case aging and outcomes", ["screening-summary"] = "Screening status counts", ["data-quality"] = "Data quality and unresolved parties",
        ["integration-failures"] = "Integration failures and retries", ["audit"] = "Audit report", ["mi-pack"] = "Management information pack",
        ["institutional-risk"] = "Institutional risk-assessment extract", ["analyst-quality"] = "Analyst quality"
    };

    private readonly IAmlDbContext _db;
    private readonly IClock _clock;
    private readonly TenantSettingsService _settings;

    public ReportService(IAmlDbContext db, IClock clock, TenantSettingsService settings)
    {
        _db = db; _clock = clock; _settings = settings;
    }

    private static Dictionary<string, object?> Row(params (string, object?)[] cells) => cells.ToDictionary(c => c.Item1, c => c.Item2);

    public async Task<ReportTable> GenerateAsync(long tenantId, string code, ReportFilters f, IReadOnlySet<string> perms, CancellationToken ct)
    {
        if (!Catalogue.TryGetValue(code, out var title)) throw new NotFoundException("Report", code);
        var now = _clock.UtcNow;
        var from = f.From ?? now.AddDays(-30);
        var to = f.To ?? now.AddDays(1);
        var sensitive = f.IncludeSensitive && perms.Contains(Perm.ReportsExportSensitive);
        var restricted = f.IncludeRestricted && perms.Contains(Perm.ReportsExportRestricted);
        var tz = await _settings.TimeZoneAsync(tenantId, ct);
        var customers = _db.Set<Customer>().IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId);
        if (!string.IsNullOrEmpty(f.DataScopeRegion)) customers = customers.Where(c => c.Region == f.DataScopeRegion);
        var profiles = _db.Set<CurrentCustomerRiskProfile>().IgnoreQueryFilters().AsNoTracking().Where(p => p.TenantId == tenantId);
        var today = DateOnly.FromDateTime(now);

        switch (code)
        {
            case "customers":
            {
                var rows = await (from c in customers join p in profiles on c.Id equals p.CustomerId into pj from p in pj.DefaultIfEmpty()
                                  orderby c.Id select new { c.Id, c.SourceSystem, c.ExternalId, c.CustomerType, c.Status, c.NameLatin, c.NameArabic, c.NationalityCountry, c.ResidenceCountry, c.KycStatus, c.ScreeningStatus, Level = p == null ? null : p.Level, Score = p == null ? (decimal?)null : p.Score, c.CreatedAt }).ToListAsync(ct);
                Dictionary<long, List<string>> idNumbers = new();
                var cols = new List<string> { "Id", "SourceSystem", "ExternalId", "CustomerType", "Status", "NameLatin", "NameArabic", "Nationality", "Residence", "KycStatus", "ScreeningStatus", "RiskLevel", "RiskScore", "CreatedAt" };
                if (sensitive)
                {
                    cols.Add("IdentificationLast4");
                    idNumbers = (await _db.Set<CustomerIdentification>().IgnoreQueryFilters().AsNoTracking().Where(i => i.TenantId == tenantId).Select(i => new { i.CustomerId, i.Last4 }).ToListAsync(ct))
                        .GroupBy(i => i.CustomerId).ToDictionary(g => g.Key, g => g.Select(x => "******" + x.Last4).ToList());
                }
                return new ReportTable(code, title, cols, rows.Select(r =>
                {
                    var d = Row(("Id", r.Id), ("SourceSystem", r.SourceSystem), ("ExternalId", r.ExternalId), ("CustomerType", r.CustomerType), ("Status", r.Status), ("NameLatin", r.NameLatin),
                        ("NameArabic", r.NameArabic), ("Nationality", r.NationalityCountry), ("Residence", r.ResidenceCountry), ("KycStatus", r.KycStatus), ("ScreeningStatus", r.ScreeningStatus),
                        ("RiskLevel", r.Level), ("RiskScore", r.Score), ("CreatedAt", r.CreatedAt));
                    if (sensitive) d["IdentificationLast4"] = string.Join(";", idNumbers.GetValueOrDefault(r.Id) ?? new());
                    return d;
                }).ToList(), sensitive ? "Sensitive columns included by permission (masked identifiers)." : "Sensitive fields excluded by default.");
            }
            case "risk-distribution":
            {
                var ids = customers.Select(c => c.Id);
                var rows = await profiles.Where(p => ids.Contains(p.CustomerId)).GroupBy(p => p.Level).Select(g => new { Level = g.Key, Count = g.Count(), AvgScore = g.Average(p => p.Score) }).ToListAsync(ct);
                var unassessed = await customers.CountAsync(c => !profiles.Any(p => p.CustomerId == c.Id), ct);
                var list = rows.OrderBy(r => r.Level).Select(r => Row(("RiskLevel", r.Level), ("Customers", r.Count), ("AverageScore", Math.Round(r.AvgScore, 2)))).ToList();
                list.Add(Row(("RiskLevel", "NotAssessed"), ("Customers", unassessed), ("AverageScore", null)));
                return new ReportTable(code, title, new[] { "RiskLevel", "Customers", "AverageScore" }, list);
            }
            case "high-risk-customers":
            {
                var rows = await (from c in customers join p in profiles on c.Id equals p.CustomerId where p.Level == RiskLevels.High || p.Level == RiskLevels.Critical
                                  select new { c.Id, c.ExternalId, c.NameLatin, c.CustomerType, p.Level, p.Score, p.NextReviewDate, p.OverrideApplied }).ToListAsync(ct);
                return new ReportTable(code, title, new[] { "Id", "ExternalId", "Name", "Type", "RiskLevel", "Score", "NextReview", "Override" },
                    rows.Select(r => Row(("Id", r.Id), ("ExternalId", r.ExternalId), ("Name", r.NameLatin), ("Type", r.CustomerType), ("RiskLevel", r.Level), ("Score", r.Score), ("NextReview", r.NextReviewDate), ("Override", r.OverrideApplied))).ToList());
            }
            case "overdue-reviews":
            {
                var ids = customers.Select(c => c.Id);
                var tasks = await _db.Set<RiskReviewTask>().IgnoreQueryFilters().AsNoTracking().Where(t => t.TenantId == tenantId && ids.Contains(t.CustomerId) && t.Status != "Completed").OrderBy(t => t.DueDate).ToListAsync(ct);
                return new ReportTable(code, title, new[] { "TaskId", "CustomerId", "DueDate", "Status", "Owner", "Overdue", "EscalatedTo" },
                    tasks.Select(t => Row(("TaskId", t.Id), ("CustomerId", t.CustomerId), ("DueDate", t.DueDate), ("Status", t.Status), ("Owner", t.Owner), ("Overdue", t.DueDate < today), ("EscalatedTo", t.EscalatedTo))).ToList());
            }
            case "kyc-gaps":
            {
                var ids = await customers.Select(c => new { c.Id, c.CustomerType, c.DueDiligenceLevel }).ToListAsync(ct);
                var expired = await _db.Set<CustomerIdentification>().IgnoreQueryFilters().AsNoTracking().Where(i => i.TenantId == tenantId && i.ExpiryDate != null && i.ExpiryDate < today).Select(i => new { i.CustomerId, i.IdType, i.ExpiryDate }).ToListAsync(ct);
                var docs = await _db.Set<CustomerDocument>().IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == tenantId && d.ExpiryDate != null && d.ExpiryDate < today).Select(d => new { d.CustomerId, d.DocumentType, d.ExpiryDate }).ToListAsync(ct);
                var owners = (await _db.Set<OwnershipLink>().IgnoreQueryFilters().AsNoTracking().Where(l => l.TenantId == tenantId).Select(l => l.EntityCustomerId).ToListAsync(ct)).ToHashSet();
                var levels = await _db.Set<DueDiligenceLevelDefinition>().IgnoreQueryFilters().AsNoTracking().Where(l => l.TenantId == tenantId).ToListAsync(ct);
                var values = (await _db.Set<CustomerFieldValue>().IgnoreQueryFilters().AsNoTracking().Where(v => v.TenantId == tenantId && v.EffectiveTo == null).Select(v => new { v.CustomerId, v.FieldCode }).ToListAsync(ct))
                    .GroupBy(v => v.CustomerId).ToDictionary(g => g.Key, g => g.Select(x => x.FieldCode).ToHashSet());
                var rows = new List<Dictionary<string, object?>>();
                foreach (var e in expired) rows.Add(Row(("CustomerId", e.CustomerId), ("Gap", $"Expired identification {e.IdType}"), ("Date", e.ExpiryDate)));
                foreach (var d in docs) rows.Add(Row(("CustomerId", d.CustomerId), ("Gap", $"Expired document {d.DocumentType}"), ("Date", d.ExpiryDate)));
                foreach (var c in ids)
                {
                    if (c.CustomerType != CustomerTypes.Individual && !owners.Contains(c.Id)) rows.Add(Row(("CustomerId", c.Id), ("Gap", "Missing beneficial ownership information"), ("Date", null)));
                    var lvl = levels.FirstOrDefault(l => l.Code == (c.DueDiligenceLevel ?? "CDD"));
                    if (lvl is null) continue;
                    var have = values.GetValueOrDefault(c.Id) ?? new HashSet<string>();
                    foreach (var fcode in lvl.RequiredFieldCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !have.Contains(x)))
                        rows.Add(Row(("CustomerId", c.Id), ("Gap", $"Missing {lvl.Code} field {fcode}"), ("Date", null)));
                }
                return new ReportTable(code, title, new[] { "CustomerId", "Gap", "Date" }, rows);
            }
            case "transaction-activity":
            {
                var txns = await _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking().Where(t => t.TenantId == tenantId && t.TimestampUtc >= from && t.TimestampUtc < to && !t.IsReversal)
                    .Select(t => new { t.TimestampUtc, t.Channel, t.TransactionType, t.DestinationCountry, t.Currency, t.Amount, t.BaseAmount, t.Status }).ToListAsync(ct);
                var by = f.GroupBy ?? "day";
                string Key(DateTime ts, string? channel, string type, string? country, string currency) => by switch
                {
                    "channel" => channel ?? "(none)", "type" => type, "country" => country ?? "(none)", "currency" => currency,
                    "month" => TimeZoneInfo.ConvertTimeFromUtc(ts, tz).ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    _ => TimeZoneInfo.ConvertTimeFromUtc(ts, tz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                };
                var rows = txns.GroupBy(t => Key(t.TimestampUtc, t.Channel, t.TransactionType, t.DestinationCountry, t.Currency)).OrderBy(g => g.Key).Select(g => Row(("Group", g.Key), ("Count", g.Count()), ("Completed", g.Count(x => x.Status == TxnStatuses.Completed)),
                    ("BaseAmount", g.Sum(x => x.BaseAmount ?? 0)))).ToList();
                return new ReportTable(code, title, new[] { "Group", "Count", "Completed", "BaseAmount" }, rows, $"Grouped by {by}; day/month boundaries in {tz.Id}; range [{from:O}, {to:O}).");
            }
            case "rule-executions":
            {
                var ex = await _db.Set<AmlRuleExecution>().IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenantId && e.StartedAt >= from && e.StartedAt < to).OrderByDescending(e => e.Id).Take(5000).ToListAsync(ct);
                return new ReportTable(code, title, new[] { "ExecutionId", "Rule", "Version", "Mode", "WindowStart", "WindowEnd", "Evaluated", "Matches", "AlertsCreated", "Deduplicated", "Errors", "Status", "CorrelationId" },
                    ex.Select(e => Row(("ExecutionId", e.Id), ("Rule", e.RuleCode), ("Version", e.RuleVersion), ("Mode", e.Mode), ("WindowStart", e.WindowStart), ("WindowEnd", e.WindowEnd), ("Evaluated", e.RecordsEvaluated),
                        ("Matches", e.Matches), ("AlertsCreated", e.AlertsCreated), ("Deduplicated", e.AlertsDeduplicated), ("Errors", e.ErrorCount), ("Status", e.Status), ("CorrelationId", e.CorrelationId))).ToList());
            }
            case "alerts":
            case "alerts-summary":
            {
                var q = _db.Set<AmlAlert>().IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantId == tenantId && !a.IsBacktest && a.CreatedAt >= from && a.CreatedAt < to);
                if (!restricted) q = q.Where(a => a.Confidentiality == Confidentiality.Standard);
                var list = await q.Select(a => new { a.Id, a.AlertNumber, a.Title, a.Severity, a.Status, a.RuleCode, a.AssignedTo, a.CreatedAt, a.ClosedAt, a.PrimaryCustomerId }).ToListAsync(ct);
                var names = await customers.Where(c => list.Select(a => a.PrimaryCustomerId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.NameArabic ?? c.NameLatin, ct);
                if (code == "alerts")
                    return new ReportTable(code, title, new[] { "AlertNumber", "Title", "Severity", "Status", "Rule", "AssignedTo", "Customer", "CreatedAt", "AgeDays" },
                        list.Select(a => Row(("AlertNumber", a.AlertNumber), ("Title", a.Title), ("Severity", a.Severity), ("Status", a.Status), ("Rule", a.RuleCode), ("AssignedTo", a.AssignedTo),
                            ("Customer", a.PrimaryCustomerId is { } c ? names.GetValueOrDefault(c) : null), ("CreatedAt", a.CreatedAt), ("AgeDays", Math.Round(((a.ClosedAt ?? now) - a.CreatedAt).TotalDays, 1)))).ToList(),
                        restricted ? "Restricted rows included by explicit permission." : "Restricted rows excluded.");
                var rows = list.GroupBy(a => new { a.Status, a.Severity, a.RuleCode, a.AssignedTo }).Select(g => Row(("Status", g.Key.Status), ("Severity", g.Key.Severity), ("Rule", g.Key.RuleCode),
                    ("Owner", g.Key.AssignedTo), ("Count", g.Count()), ("AvgAgeDays", Math.Round(g.Average(a => ((a.ClosedAt ?? now) - a.CreatedAt).TotalDays), 1)))).ToList();
                return new ReportTable(code, title, new[] { "Status", "Severity", "Rule", "Owner", "Count", "AvgAgeDays" }, rows);
            }
            case "case-aging":
            {
                var q = _db.Set<AmlCase>().IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId);
                if (!restricted) q = q.Where(c => c.Confidentiality == Confidentiality.Standard);
                var list = await q.Select(c => new { c.CaseNumber, c.Status, c.Priority, c.CreatedAt, c.ClosedAt, c.FinalDecision, c.DueDate }).ToListAsync(ct);
                return new ReportTable(code, title, new[] { "CaseNumber", "Status", "Priority", "AgeDays", "Overdue", "Outcome" },
                    list.Select(c => Row(("CaseNumber", c.CaseNumber), ("Status", c.Status), ("Priority", c.Priority), ("AgeDays", Math.Round(((c.ClosedAt ?? now) - c.CreatedAt).TotalDays, 1)),
                        ("Overdue", c.Status != CaseStatuses.Closed && c.DueDate < now), ("Outcome", c.FinalDecision))).ToList());
            }
            case "screening-summary":
            {
                var results = await _db.Set<ScreeningResult>().IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == tenantId).GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
                var reqs = await _db.Set<ScreeningRequest>().IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == tenantId && r.Status != "Completed").GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
                var rows = reqs.Select(r => Row(("Category", "Request"), ("Status", r.Key), ("Count", r.Count))).Concat(results.Select(r => Row(("Category", "Result"), ("Status", r.Key), ("Count", r.Count)))).ToList();
                return new ReportTable(code, title, new[] { "Category", "Status", "Count" }, rows);
            }
            case "data-quality":
            {
                var list = await _db.Set<DataQualityIssue>().IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == tenantId && d.Status == "Open").OrderBy(d => d.CreatedAt).Take(10000).ToListAsync(ct);
                return new ReportTable(code, title, new[] { "Id", "Type", "Entity", "EntityId", "Description", "Severity", "CreatedAt", "AgeDays" },
                    list.Select(d => Row(("Id", d.Id), ("Type", d.IssueType), ("Entity", d.EntityType), ("EntityId", d.EntityId), ("Description", d.Description), ("Severity", d.Severity),
                        ("CreatedAt", d.CreatedAt), ("AgeDays", Math.Round((now - d.CreatedAt).TotalDays, 1)))).ToList());
            }
            case "integration-failures":
            {
                var recs = await _db.Set<IngestionRecord>().IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == tenantId && (r.Status == "DeadLetter" || r.Status == "Retrying" || r.Status == "Conflict" || r.AttemptCount > 1))
                    .OrderByDescending(r => r.Id).Take(5000).ToListAsync(ct);
                var retries = await _db.Set<RetryQueueItem>().IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == tenantId).OrderByDescending(r => r.Id).Take(5000).ToListAsync(ct);
                var rows = recs.Select(r => Row(("Kind", "Ingestion"), ("Reference", r.SourceTransactionId ?? r.Id.ToString(CultureInfo.InvariantCulture)), ("Status", r.Status), ("Attempts", r.AttemptCount), ("LastError", r.LastError), ("CorrelationId", r.CorrelationId), ("At", r.ReceivedAt)))
                    .Concat(retries.Select(r => Row(("Kind", r.Kind), ("Reference", r.ReferenceId), ("Status", r.Status), ("Attempts", r.Attempts), ("LastError", r.LastError), ("CorrelationId", null), ("At", r.CreatedAt)))).ToList();
                return new ReportTable(code, title, new[] { "Kind", "Reference", "Status", "Attempts", "LastError", "CorrelationId", "At" }, rows);
            }
            case "audit":
            {
                var q = _db.Set<AuditLog>().AsNoTracking().Where(a => a.TenantId == tenantId && a.TimestampUtc >= from && a.TimestampUtc < to);
                if (!restricted) q = q.Where(a => !a.IsRestricted);
                var list = await q.OrderByDescending(a => a.Id).Take(20000).ToListAsync(ct);
                return new ReportTable(code, title, new[] { "Id", "TimestampUtc", "Actor", "Action", "EntityType", "EntityId", "Reason", "Outcome", "CorrelationId" },
                    list.Select(a => Row(("Id", a.Id), ("TimestampUtc", a.TimestampUtc), ("Actor", a.Actor), ("Action", a.Action), ("EntityType", a.EntityType), ("EntityId", a.EntityId), ("Reason", a.Reason), ("Outcome", a.Outcome), ("CorrelationId", a.CorrelationId))).ToList());
            }
            case "mi-pack":
            {
                var closed = AlertStatuses.Closed;
                var openAlerts = await _db.Set<AmlAlert>().IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId && !a.IsBacktest && !closed.Contains(a.Status) && a.Status != AlertStatuses.ConvertedToCase, ct);
                var overdueAlerts = await _db.Set<AmlAlert>().IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId && !a.IsBacktest && !closed.Contains(a.Status) && a.Status != AlertStatuses.ConvertedToCase && a.DueAt < now, ct);
                var overdueReviews = await _db.Set<RiskReviewTask>().IgnoreQueryFilters().CountAsync(t => t.TenantId == tenantId && t.Status != "Completed" && t.DueDate < today, ct);
                var holdBreaches = await _db.Set<HoldQueueItem>().IgnoreQueryFilters().CountAsync(h => h.TenantId == tenantId && h.IsEscalated, ct);
                var screenTimes = await _db.Set<ScreeningResult>().IgnoreQueryFilters().Where(r => r.TenantId == tenantId && r.ReviewedAt != null).Select(r => new { r.ScreenedAt, r.ReviewedAt }).ToListAsync(ct);
                var filed = await _db.Set<RegulatoryReport>().IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId && r.SubmittedAt != null && r.SubmittedAt >= from && r.SubmittedAt < to, ct);
                var frozen = await _db.Set<ComplianceAction>().IgnoreQueryFilters().Where(a => a.TenantId == tenantId && a.Status == "Applied" && a.FrozenAmount != null).SumAsync(a => a.FrozenAmount ?? 0, ct);
                var dq = await _db.Set<DataQualityIssue>().IgnoreQueryFilters().CountAsync(d => d.TenantId == tenantId && d.Status == "Open", ct);
                var highRisk = await profiles.CountAsync(p => p.Level == RiskLevels.High || p.Level == RiskLevels.Critical, ct);
                var rows = new List<Dictionary<string, object?>>
                {
                    Row(("Indicator", "Open alerts (backlog)"), ("Type", "KPI"), ("Value", openAlerts)),
                    Row(("Indicator", "Alerts past SLA"), ("Type", "KRI"), ("Value", overdueAlerts)),
                    Row(("Indicator", "Overdue customer reviews"), ("Type", "KRI"), ("Value", overdueReviews)),
                    Row(("Indicator", "Hold-queue SLA breaches"), ("Type", "KRI"), ("Value", holdBreaches)),
                    Row(("Indicator", "Average screening time-to-clear (hours)"), ("Type", "KPI"), ("Value", screenTimes.Count == 0 ? 0 : Math.Round(screenTimes.Average(s => (s.ReviewedAt!.Value - s.ScreenedAt).TotalHours), 2))),
                    Row(("Indicator", "Regulatory reports filed in period"), ("Type", "KPI"), ("Value", filed)),
                    Row(("Indicator", "Frozen assets (base currency)"), ("Type", "KRI"), ("Value", frozen)),
                    Row(("Indicator", "Open data-quality issues"), ("Type", "KRI"), ("Value", dq)),
                    Row(("Indicator", "High/critical risk customers"), ("Type", "KRI"), ("Value", highRisk)),
                };
                return new ReportTable(code, title, new[] { "Indicator", "Type", "Value" }, rows);
            }
            case "institutional-risk":
            {
                var list = await _db.Set<InstitutionalRiskAssessment>().IgnoreQueryFilters().AsNoTracking().Where(i => i.TenantId == tenantId).ToListAsync(ct);
                var rows = list.Select(i => Row(("Period", i.Period), ("Dimension", i.Dimension), ("Item", i.Item), ("InherentRisk", i.InherentRisk), ("Controls", i.ControlEffectiveness), ("ResidualRisk", i.ResidualRisk))).ToList();
                var byChannel = await _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking().Where(t => t.TenantId == tenantId && t.TimestampUtc >= from && t.TimestampUtc < to)
                    .GroupBy(t => t.Channel).Select(g => new { g.Key, Count = g.Count(), Amount = g.Sum(x => x.BaseAmount ?? 0) }).ToListAsync(ct);
                rows.AddRange(byChannel.Select(c => Row(("Period", "activity"), ("Dimension", "Channel volume"), ("Item", c.Key ?? "(none)"), ("InherentRisk", c.Count), ("Controls", null), ("ResidualRisk", c.Amount))));
                return new ReportTable(code, title, new[] { "Period", "Dimension", "Item", "InherentRisk", "Controls", "ResidualRisk" }, rows);
            }
            case "analyst-quality":
            {
                var samples = await _db.Set<QaSample>().IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == tenantId && s.Status != "Pending").ToListAsync(ct);
                var rows = samples.GroupBy(s => s.Analyst).Select(g => Row(("Analyst", g.Key), ("Reviewed", g.Count()), ("Errors", g.Count(s => s.Status == "ErrorFound")),
                    ("ErrorRatePct", Math.Round(100m * g.Count(s => s.Status == "ErrorFound") / g.Count(), 2)))).ToList();
                return new ReportTable(code, title, new[] { "Analyst", "Reviewed", "Errors", "ErrorRatePct" }, rows);
            }
        }
        throw new NotFoundException("Report", code);
    }
}

public sealed class ExportService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly ReportService _reports;
    private readonly IExportWriter _writer;
    private readonly IFileStore _files;

    public ExportService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, ReportService reports, IExportWriter writer, IFileStore files)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _reports = reports; _writer = writer; _files = files;
    }

    public async Task<ExportJob> RequestAsync(string code, string format, ReportFilters f, CancellationToken ct)
    {
        Guard.Require(_user, Perm.ReportsExport);
        if (!ReportService.Catalogue.ContainsKey(code)) throw new NotFoundException("Report", code);
        if (format is not ("csv" or "xlsx")) throw new ValidationException("format", "Supported formats: csv, xlsx.");
        if (f.IncludeSensitive && !_user.Has(Perm.ReportsExportSensitive)) throw new ForbiddenException("Exporting sensitive fields requires reports.export.sensitive.");
        if (f.IncludeRestricted && !_user.Has(Perm.ReportsExportRestricted)) throw new ForbiddenException("Exporting restricted data requires reports.export.restricted.");
        f.DataScopeRegion = _user.DataScopeRegion;
        var job = new ExportJob
        {
            TenantId = _user.TenantId, JobId = Guid.NewGuid(), ReportCode = code, Format = format, FiltersJson = JsonSerializer.Serialize(f), IncludeSensitive = f.IncludeSensitive,
            IncludeRestricted = f.IncludeRestricted, RequestedBy = _user.Username, RequesterPermissions = string.Join(",", _user.Permissions), CreatedAt = _clock.UtcNow
        };
        _db.Set<ExportJob>().Add(job);
        _audit.Record("EXPORT_REQUEST", "ExportJob", job.JobId.ToString(), null, new { code, format, f.From, f.To, f.GroupBy, f.IncludeSensitive, f.IncludeRestricted });
        await _db.SaveChangesAsync(ct);
        return job;
    }

    /// <summary>Background processing of queued export jobs.</summary>
    public async Task<int> ProcessQueuedAsync(CancellationToken ct)
    {
        var jobs = await _db.Set<ExportJob>().IgnoreQueryFilters().Where(j => j.Status == "Queued").OrderBy(j => j.Id).Take(10).ToListAsync(ct);
        foreach (var j in jobs)
        {
            j.Status = "Running";
            await _db.SaveChangesAsync(ct);
            try
            {
                var f = JsonSerializer.Deserialize<ReportFilters>(j.FiltersJson ?? "{}") ?? new ReportFilters();
                var table = await _reports.GenerateAsync(j.TenantId, j.ReportCode, f, j.RequesterPermissions.Split(',').ToHashSet(), ct);
                var bytes = _writer.Write(table, j.Format);
                using var ms = new MemoryStream(bytes);
                j.StorageKey = await _files.SaveAsync("exports", ms, ct);
                j.FileName = $"{j.ReportCode}-{_clock.UtcNow:yyyyMMddHHmmss}.{j.Format}";
                j.RowCount = table.Rows.Count;
                j.Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                j.Status = "Completed";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                j.Status = "Failed";
                j.Error = ex.Message;
            }
            j.CompletedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return jobs.Count;
    }

    public async Task<ExportJob> GetAsync(Guid jobId, CancellationToken ct)
    {
        var j = await _db.Set<ExportJob>().AsNoTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct) ?? throw new NotFoundException("Export", jobId);
        if (j.RequestedBy != _user.Username && !_user.Has(Perm.AuditRead)) throw new NotFoundException("Export", jobId);
        return j;
    }

    public async Task<List<ExportJob>> MineAsync(CancellationToken ct) =>
        await _db.Set<ExportJob>().AsNoTracking().Where(j => j.RequestedBy == _user.Username).OrderByDescending(j => j.Id).Take(100).ToListAsync(ct);

    public async Task<(Stream Content, string FileName, string ContentType)> DownloadAsync(Guid jobId, CancellationToken ct)
    {
        Guard.Require(_user, Perm.ReportsExport);
        var j = await _db.Set<ExportJob>().FirstOrDefaultAsync(x => x.JobId == jobId, ct) ?? throw new NotFoundException("Export", jobId);
        if (j.RequestedBy != _user.Username) throw new NotFoundException("Export", jobId);
        if (j.Status != "Completed" || j.StorageKey is null) throw new ConflictException($"Export is {j.Status}.");
        var s = await _files.OpenAsync(j.StorageKey, ct) ?? throw new NotFoundException("Export file", jobId);
        j.DownloadCount++;
        _audit.Record("EXPORT_DOWNLOAD", "ExportJob", jobId.ToString(), null, new { j.ReportCode, j.RowCount, j.Sha256 });
        await _db.SaveChangesAsync(ct);
        return (s, j.FileName!, j.Format == "csv" ? "text/csv; charset=utf-8" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }
}
