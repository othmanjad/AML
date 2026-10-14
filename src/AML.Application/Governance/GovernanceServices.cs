using System.Globalization;
using System.Text.Json;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Governance;

public sealed record RuleMetricsDto(long RuleId, string RuleCode, string RuleName, bool IsActive, int AlertsGenerated, int AlertsConvertedOrLinkedToCase, decimal AlertToCaseRate,
    int CasesWithReport, decimal CaseToReportRate, int ClosedAlerts, int FalsePositives, decimal FalsePositiveRate, decimal? AvgHandlingHours, decimal? AvgOpenAlertAgeDays,
    string Definitions);

public sealed record FeedRow(string Source, DateOnly BusinessDate, int Count, decimal BaseAmount, string Status);

public sealed class EffectivenessService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;

    public EffectivenessService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit)
    {
        _db = db; _user = user; _clock = clock; _audit = audit;
    }

    public const string MetricDefinitions =
        "Alert-to-case rate = alerts linked to or converted into a case / operational alerts generated in period. " +
        "Case-to-report rate = distinct cases (from this rule's alerts) with an STR in any status beyond Draft / distinct such cases. " +
        "False-positive rate = alerts closed as Closed-FalsePositive / all closed alerts. Handling time = ClosedAt - CreatedAt for closed alerts. Backtest alerts excluded.";

    public async Task<List<RuleMetricsDto>> RuleMetricsAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var f = from ?? DateTime.MinValue;
        var t = to ?? DateTime.MaxValue;
        var now = _clock.UtcNow;
        var rules = await _db.Set<AmlRule>().AsNoTracking().ToListAsync(ct);
        var alerts = await _db.Set<AmlAlert>().AsNoTracking().Where(a => a.RuleId != null && !a.IsBacktest && a.CreatedAt >= f && a.CreatedAt < t)
            .Select(a => new { a.Id, a.RuleId, a.Status, a.CreatedAt, a.ClosedAt, a.CaseId }).ToListAsync(ct);
        var caseLinks = await _db.Set<AmlCaseAlert>().AsNoTracking().Select(x => new { x.AlertId, x.CaseId }).ToListAsync(ct);
        var reportCases = (await _db.Set<RegulatoryReport>().AsNoTracking().Where(r => r.Status != ReportStatuses.Draft && r.Status != ReportStatuses.Withdrawn).Select(r => r.CaseId).ToListAsync(ct)).ToHashSet();
        return rules.Select(r =>
        {
            var mine = alerts.Where(a => a.RuleId == r.Id).ToList();
            var cases = mine.SelectMany(a => caseLinks.Where(l => l.AlertId == a.Id).Select(l => l.CaseId).Concat(a.CaseId is { } c ? new[] { c } : Array.Empty<long>())).Distinct().ToList();
            var toCase = mine.Count(a => a.CaseId != null || caseLinks.Any(l => l.AlertId == a.Id));
            var closed = mine.Where(a => AlertStatuses.IsClosed(a.Status)).ToList();
            var fp = closed.Count(a => a.Status == AlertStatuses.ClosedFalsePositive);
            var open = mine.Where(a => !AlertStatuses.IsClosed(a.Status) && a.Status != AlertStatuses.ConvertedToCase).ToList();
            return new RuleMetricsDto(r.Id, r.Code, r.Name, r.ActiveVersionId != null, mine.Count, toCase, Rate(toCase, mine.Count), cases.Count(reportCases.Contains), Rate(cases.Count(reportCases.Contains), cases.Count),
                closed.Count, fp, Rate(fp, closed.Count), closed.Count(a => a.ClosedAt != null) == 0 ? null : Math.Round((decimal)closed.Where(a => a.ClosedAt != null).Average(a => (a.ClosedAt!.Value - a.CreatedAt).TotalHours), 2),
                open.Count == 0 ? null : Math.Round((decimal)open.Average(a => (now - a.CreatedAt).TotalDays), 2), MetricDefinitions);
        }).ToList();
    }

    private static decimal Rate(int n, int d) => d == 0 ? 0 : Math.Round(100m * n / d, 2);

    public async Task<object> CoverageAsync(CancellationToken ct)
    {
        var typologies = await _db.Set<Typology>().AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
        var controls = await _db.Set<TypologyControl>().AsNoTracking().ToListAsync(ct);
        var rules = await _db.Set<AmlRule>().AsNoTracking().ToListAsync(ct);
        var rows = typologies.Select(t =>
        {
            var mapped = controls.Where(c => c.TypologyCode == t.Code).ToList();
            var ruleCodes = mapped.Where(m => m.ControlType == "Rule").Select(m => m.ControlReference)
                .Concat(rules.Where(r => (r.TypologyCodes ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(t.Code)).Select(r => r.Code)).Distinct().ToList();
            var activeRules = ruleCodes.Where(code => rules.Any(r => r.Code == code && r.ActiveVersionId != null)).ToList();
            var otherControls = mapped.Where(m => m.ControlType != "Rule").Select(m => m.ControlReference).ToList();
            return new { t.Code, t.Name, t.RiskRating, rules = ruleCodes, activeRules, otherControls, covered = activeRules.Count > 0 || otherControls.Count > 0 };
        }).ToList();
        return new { typologies = rows, uncovered = rows.Where(r => !r.covered).Select(r => r.Code).ToList() };
    }

    public async Task<TypologyControl> MapControlAsync(string typologyCode, string controlType, string reference, CancellationToken ct)
    {
        if (!await _db.Set<Typology>().AnyAsync(t => t.Code == typologyCode, ct)) throw new NotFoundException("Typology", typologyCode);
        var c = new TypologyControl { TenantId = _user.TenantId, TypologyCode = typologyCode, ControlType = controlType, ControlReference = reference };
        _db.Set<TypologyControl>().Add(c);
        _audit.Record("TYPOLOGY_CONTROL_MAP", "Typology", typologyCode, null, new { controlType, reference });
        await _db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>Data-feed completeness: imported counts/amounts per in-scope source and business day; days with no data are gaps.</summary>
    public async Task<object> FeedCompletenessAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 92) throw new ValidationException("to", "Range must be 0-92 days.");
        var sources = await _db.Set<SourceSystem>().AsNoTracking().Where(s => s.IsActive && s.ExpectedFeed == "Daily").ToListAsync(ct);
        var stats = await _db.Set<Transaction>().AsNoTracking().Where(t => t.BusinessDate >= from && t.BusinessDate <= to && !t.IsTestData)
            .GroupBy(t => new { t.SourceSystem, t.BusinessDate }).Select(g => new { g.Key.SourceSystem, g.Key.BusinessDate, Count = g.Count(), Amount = g.Sum(x => x.BaseAmount ?? 0) }).ToListAsync(ct);
        var rows = new List<FeedRow>();
        foreach (var s in sources)
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var st = stats.FirstOrDefault(x => x.SourceSystem == s.Code && x.BusinessDate == d);
                rows.Add(new FeedRow(s.Code, d, st?.Count ?? 0, st?.Amount ?? 0, st is null ? "Gap" : "Received"));
            }
        return new { from, to, rows, gaps = rows.Count(r => r.Status == "Gap") };
    }

    public async Task<object> ValidationPackAsync(CancellationToken ct)
    {
        var methodologies = await _db.Set<RiskMethodology>().AsNoTracking().Include(m => m.Factors).ThenInclude(f => f.Mappings).Include(m => m.Thresholds).Include(m => m.Overrides).Include(m => m.Floors).AsSplitQuery().ToListAsync(ct);
        var rules = await _db.Set<AmlRule>().AsNoTracking().ToListAsync(ct);
        var versions = await _db.Set<AmlRuleVersion>().AsNoTracking().ToListAsync(ct);
        var executions = await _db.Set<AmlRuleExecution>().AsNoTracking().Where(e => e.Mode == "Backtest" || e.Mode == "Simulation" || e.Mode == "Tuning").OrderByDescending(e => e.Id).Take(200).ToListAsync(ct);
        var tuning = await _db.Set<TuningRun>().AsNoTracking().OrderByDescending(t => t.Id).Take(100).ToListAsync(ct);
        var pack = new
        {
            generatedAt = _clock.UtcNow, generatedBy = _user.Username,
            methodologies = methodologies.Select(m => JsonDocument.Parse(Risk.RiskAssessmentService.Snapshot(m)).RootElement),
            rules = rules.Select(r => new
            {
                r.Code, r.Name, r.Category, r.TypologyCodes, r.IsSample,
                versions = versions.Where(v => v.RuleId == r.Id).OrderBy(v => v.Version).Select(v => new { v.Version, v.Status, definition = JsonDocument.Parse(v.DefinitionJson).RootElement, v.Rationale, v.TestEvidence, v.ExpectedImpact, v.ApprovedBy, v.ApprovedAt, v.EffectiveFrom, v.EffectiveTo })
            }),
            testRuns = executions.Select(e => new { e.RuleCode, e.RuleVersion, e.Mode, e.WindowStart, e.WindowEnd, e.RecordsEvaluated, e.Matches, e.Status, e.StartedAt }),
            tuningRuns = tuning.Select(t => new { t.RuleId, t.RunType, t.CurrentMatches, t.CandidateMatches, t.CreatedBy, t.CreatedAt, t.ApprovedBy }),
            metrics = await RuleMetricsAsync(null, null, ct), coverage = await CoverageAsync(ct), metricDefinitions = MetricDefinitions
        };
        _audit.Record("VALIDATION_PACK_EXPORT", "Governance", null, null, new { methodologies = methodologies.Count, rules = rules.Count });
        await _db.SaveChangesAsync(ct);
        return pack;
    }
}

public sealed class QaService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly Alerts.AlertService _alerts;

    public QaService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, Alerts.AlertService alerts)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _alerts = alerts;
    }

    /// <summary>Reproducible stratified sampling (seeded) of closed alerts per configuration.</summary>
    public async Task<object> RunSamplingAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        Guard.Require(_user, Perm.QaReview);
        var configs = await _db.Set<QaSamplingConfig>().AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        var already = (await _db.Set<QaSample>().Select(s => s.AlertId).ToListAsync(ct)).ToHashSet();
        var closed = AlertStatuses.Closed;
        var candidates = await _db.Set<AmlAlert>().AsNoTracking().Where(a => closed.Contains(a.Status) && a.ClosedAt >= from && a.ClosedAt < to && !a.IsBacktest)
            .Select(a => new { a.Id, a.Status, a.RuleCode, a.AssignedTo, a.PrimaryCustomerId }).ToListAsync(ct);
        var levels = await _db.Set<CurrentCustomerRiskProfile>().AsNoTracking().Select(p => new { p.CustomerId, p.Level }).ToDictionaryAsync(p => p.CustomerId, p => p.Level, ct);
        var results = new List<object>();
        foreach (var cfg in configs)
        {
            var pool = candidates.Where(a => (cfg.Outcome == "*" || a.Status == cfg.Outcome) && (cfg.RuleCode == null || a.RuleCode == cfg.RuleCode) &&
                                             (cfg.Analyst == null || a.AssignedTo == cfg.Analyst) &&
                                             (cfg.RiskLevel == null || (a.PrimaryCustomerId is { } c && levels.GetValueOrDefault(c) == cfg.RiskLevel))).OrderBy(a => a.Id).ToList();
            var size = (int)Math.Ceiling(pool.Count * cfg.Percentage / 100m);
            var rnd = new Random(cfg.Seed);
            var sample = pool.OrderBy(_ => rnd.Next()).Take(size).ToList();
            foreach (var a in sample.Where(a => already.Add(a.Id)))
                _db.Set<QaSample>().Add(new QaSample { TenantId = _user.TenantId, AlertId = a.Id, ConfigCode = cfg.Code, Stratum = $"{cfg.Outcome}|{cfg.RuleCode ?? "*"}|{cfg.Analyst ?? "*"}|{cfg.RiskLevel ?? "*"}", Analyst = a.AssignedTo ?? "", SampledAt = _clock.UtcNow });
            results.Add(new { cfg.Code, cfg.Percentage, population = pool.Count, sampleSize = size, sampled = sample.Select(s => s.Id) });
        }
        _audit.Record("QA_SAMPLING_RUN", "QaSample", null, null, new { from, to, configs = configs.Count });
        await _db.SaveChangesAsync(ct);
        return results;
    }

    public async Task<List<QaSample>> SamplesAsync(string? status, CancellationToken ct) =>
        await _db.Set<QaSample>().AsNoTracking().Where(s => status == null || s.Status == status).OrderByDescending(s => s.Id).Take(500).ToListAsync(ct);

    public async Task<QaSample> ReviewAsync(long sampleId, bool passed, string? errorCategory, string? findings, bool reopen, CancellationToken ct)
    {
        Guard.Require(_user, Perm.QaReview);
        var s = await _db.Set<QaSample>().FirstOrDefaultAsync(x => x.Id == sampleId, ct) ?? throw new NotFoundException("QA sample", sampleId);
        if (s.Status != "Pending") throw new ConflictException("Sample already reviewed.");
        if (!passed && (string.IsNullOrWhiteSpace(errorCategory) || string.IsNullOrWhiteSpace(findings))) throw new ValidationException("findings", "Error category and findings are required.");
        Guard.FourEyes(s.Analyst, _user.Username, "QA review");
        s.Status = passed ? "Passed" : "ErrorFound";
        s.Reviewer = _user.Username;
        s.ErrorCategory = errorCategory;
        s.Findings = findings;
        s.ReviewedAt = _clock.UtcNow;
        _audit.Record("QA_REVIEW", "AmlAlert", s.AlertId.ToString(CultureInfo.InvariantCulture), null, new { s.Status, errorCategory, reopen }, findings);
        await _db.SaveChangesAsync(ct);
        if (!passed && reopen)
        {
            await _alerts.ReopenAsync(s.AlertId, $"QA finding ({errorCategory}): {findings}", ct);
            s.Reopened = true;
            await _db.SaveChangesAsync(ct);
        }
        return s;
    }

    public async Task<object> AnalystQualityAsync(CancellationToken ct)
    {
        var samples = await _db.Set<QaSample>().AsNoTracking().Where(s => s.Status != "Pending").ToListAsync(ct);
        var closures = (await _db.Set<AmlAlert>().AsNoTracking().Where(a => a.ClosedAt != null && !a.IsBacktest).Select(a => new { a.AssignedTo, a.CreatedAt, a.ClosedAt }).ToListAsync(ct))
            .GroupBy(a => a.AssignedTo).Select(g => new { Analyst = g.Key, Closed = g.Count(), AvgHours = (double?)Math.Round(g.Average(a => (a.ClosedAt!.Value - a.CreatedAt).TotalHours), 2) }).ToList();
        return samples.GroupBy(s => s.Analyst).Select(g => new
        {
            analyst = g.Key, reviewed = g.Count(), errors = g.Count(s => s.Status == "ErrorFound"), errorRate = Math.Round(100m * g.Count(s => s.Status == "ErrorFound") / g.Count(), 2),
            closedAlerts = closures.FirstOrDefault(c => c.Analyst == g.Key)?.Closed ?? 0, avgHandlingHours = closures.FirstOrDefault(c => c.Analyst == g.Key)?.AvgHours
        }).OrderBy(x => x.analyst).ToList();
    }
}

public sealed class RetentionService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;

    public RetentionService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit)
    {
        _db = db; _user = user; _clock = clock; _audit = audit;
    }

    public async Task<List<RetentionPolicy>> PoliciesAsync(CancellationToken ct) => await _db.Set<RetentionPolicy>().AsNoTracking().ToListAsync(ct);

    public async Task<LegalHold> AddHoldAsync(string entityType, long entityId, string reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RetentionManage);
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "Reason is required.");
        var h = new LegalHold { TenantId = _user.TenantId, EntityType = entityType, EntityId = entityId, Reason = reason, CreatedBy = _user.Username, CreatedAt = _clock.UtcNow };
        _db.Set<LegalHold>().Add(h);
        _audit.Record("LEGAL_HOLD_ADD", entityType, entityId.ToString(CultureInfo.InvariantCulture), null, new { reason });
        await _db.SaveChangesAsync(ct);
        return h;
    }

    /// <summary>Dry run: identifies customers whose retention period (from relationship end) has expired, excluding any record under legal hold. Nothing is deleted.</summary>
    public async Task<RetentionRun> DryRunAsync(DateTime? asOf, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RetentionManage);
        var at = asOf ?? _clock.UtcNow;
        var policy = await _db.Set<RetentionPolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.RecordClass == "Customer" && p.IsActive, ct)
                     ?? throw new ConflictException("No active retention policy for record class Customer.");
        var exited = await _db.Set<Customer>().AsNoTracking().Where(c => c.ExitedAt != null && !c.IsAnonymised).Select(c => new { c.Id, c.ExitedAt, c.ExternalId }).ToListAsync(ct);
        var candidates = new List<object>();
        var skipped = new List<object>();
        foreach (var c in exited)
        {
            var eligibleAt = c.ExitedAt!.Value.AddMonths(policy.RetentionMonths);
            if (eligibleAt > at) continue;
            var holds = await HoldReasonsAsync(c.Id, ct);
            if (holds.Count > 0) skipped.Add(new { customerId = c.Id, c.ExternalId, reason = "Legal hold", holds });
            else candidates.Add(new { customerId = c.Id, c.ExternalId, relationshipEnded = c.ExitedAt, eligibleAt, action = policy.Disposal });
        }
        var run = new RetentionRun
        {
            TenantId = _user.TenantId, DryRun = true, Status = "DryRunCompleted", AsOf = at, CandidatesJson = JsonSerializer.Serialize(candidates), SkippedJson = JsonSerializer.Serialize(skipped),
            CandidateCount = candidates.Count, SkippedCount = skipped.Count, RequestedBy = _user.Username, RequestedAt = _clock.UtcNow
        };
        _db.Set<RetentionRun>().Add(run);
        _audit.Record("RETENTION_DRY_RUN", "RetentionRun", null, null, new { candidates = candidates.Count, skipped = skipped.Count, policy.RetentionMonths, policy.ClockEvent });
        await _db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<List<string>> HoldReasonsAsync(long customerId, CancellationToken ct)
    {
        var reasons = new List<string>();
        if (await _db.Set<LegalHold>().AnyAsync(h => h.IsActive && h.EntityType == "Customer" && h.EntityId == customerId, ct)) reasons.Add("Explicit legal hold");
        var caseIds = _db.Set<AmlCaseCustomer>().Where(x => x.CustomerId == customerId).Select(x => x.CaseId);
        if (await _db.Set<AmlCase>().AnyAsync(c => (c.PrimaryCustomerId == customerId || caseIds.Contains(c.Id)) && c.Status != CaseStatuses.Closed, ct)) reasons.Add("Open case");
        var allCases = _db.Set<AmlCase>().Where(c => c.PrimaryCustomerId == customerId || caseIds.Contains(c.Id)).Select(c => c.Id);
        if (await _db.Set<RegulatoryReport>().AnyAsync(r => allCases.Contains(r.CaseId) && r.Status != ReportStatuses.Draft && r.Status != ReportStatuses.Withdrawn, ct)) reasons.Add("Filed regulatory report");
        if (await _db.Set<AuthorityRequest>().AnyAsync(r => r.CustomerId == customerId && r.Status != "Closed", ct)) reasons.Add("Authority request");
        return reasons;
    }

    public async Task<RetentionRun> SubmitAsync(long runId, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RetentionManage);
        var r = await _db.Set<RetentionRun>().FirstOrDefaultAsync(x => x.Id == runId, ct) ?? throw new NotFoundException("Retention run", runId);
        if (r.Status != "DryRunCompleted") throw new ConflictException("Only completed dry runs can be submitted.");
        r.Status = "PendingApproval";
        await _db.SaveChangesAsync(ct);
        return r;
    }

    public async Task<RetentionRun> ApproveAsync(long runId, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RetentionApprove);
        var r = await _db.Set<RetentionRun>().FirstOrDefaultAsync(x => x.Id == runId, ct) ?? throw new NotFoundException("Retention run", runId);
        if (r.Status != "PendingApproval") throw new ConflictException("Run is not pending approval.");
        Guard.FourEyes(r.RequestedBy, _user.Username, "Retention deletion");
        r.Status = "Approved";
        r.ApprovedBy = _user.Username;
        r.ApprovedAt = _clock.UtcNow;
        _audit.Record("RETENTION_APPROVE", "RetentionRun", runId.ToString(CultureInfo.InvariantCulture), null, new { r.CandidateCount });
        await _db.SaveChangesAsync(ct);
        return r;
    }

    /// <summary>Executes an approved run: anonymises customers (re-checking legal holds at execution time) with full audit.</summary>
    public async Task<RetentionRun> ExecuteAsync(long runId, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RetentionManage);
        var r = await _db.Set<RetentionRun>().FirstOrDefaultAsync(x => x.Id == runId, ct) ?? throw new NotFoundException("Retention run", runId);
        if (r.Status != "Approved") throw new ConflictException("Deletion requires an approved run.");
        using var doc = JsonDocument.Parse(r.CandidatesJson);
        var processed = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = e.GetProperty("customerId").GetInt64();
            if ((await HoldReasonsAsync(id, ct)).Count > 0) continue;
            var c = await _db.Set<Customer>().FirstAsync(x => x.Id == id, ct);
            c.NameLatin = "ANONYMISED"; c.NameArabic = null; c.NameSearchKey = null; c.DateOfBirth = null; c.IsAnonymised = true; c.UpdatedAt = _clock.UtcNow;
            _db.Set<CustomerIdentification>().RemoveRange(_db.Set<CustomerIdentification>().Where(i => i.CustomerId == id));
            _db.Set<CustomerContact>().RemoveRange(_db.Set<CustomerContact>().Where(i => i.CustomerId == id));
            _db.Set<CustomerFieldValue>().RemoveRange(_db.Set<CustomerFieldValue>().Where(i => i.CustomerId == id));
            _audit.Record("RETENTION_ANONYMISE", "Customer", id.ToString(CultureInfo.InvariantCulture), null, new { runId });
            processed++;
        }
        r.Status = "Executed";
        r.ExecutedAt = _clock.UtcNow;
        r.ProcessedCount = processed;
        await _db.SaveChangesAsync(ct);
        return r;
    }

    public async Task<List<RetentionRun>> RunsAsync(CancellationToken ct) => await _db.Set<RetentionRun>().AsNoTracking().OrderByDescending(r => r.Id).Take(100).ToListAsync(ct);
}

public sealed class JurisdictionService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;

    public JurisdictionService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit)
    {
        _db = db; _user = user; _clock = clock; _audit = audit;
    }

    public async Task<List<JurisdictionSetting>> ListAsync(CancellationToken ct) =>
        await _db.Set<JurisdictionSetting>().AsNoTracking().OrderBy(j => j.Key).ThenByDescending(j => j.EffectiveFrom).ToListAsync(ct);

    public async Task<JurisdictionSetting> ProposeAsync(string key, string value, string? unit, string? legalSource, string? article, DateTime? effectiveFrom, string? owner, CancellationToken ct)
    {
        Guard.Require(_user, Perm.JurisdictionManage);
        var errors = new Dictionary<string, List<string>>();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) errors["key"] = new() { "Key and value are required." };
        if (string.IsNullOrWhiteSpace(legalSource)) errors["legalSource"] = new() { "A legal source reference is required for jurisdiction values." };
        if (string.IsNullOrWhiteSpace(article)) errors["articleReference"] = new() { "An article/clause reference is required." };
        ValidationException.ThrowIf(errors);
        var j = new JurisdictionSetting
        {
            TenantId = _user.TenantId, Key = key, Value = value, Unit = unit, LegalSource = legalSource, ArticleReference = article, EffectiveFrom = effectiveFrom ?? _clock.UtcNow,
            Status = "PendingApproval", ComplianceOwner = owner, CreatedBy = _user.Username, CreatedAt = _clock.UtcNow
        };
        _db.Set<JurisdictionSetting>().Add(j);
        _audit.Record("JURISDICTION_PROPOSE", "JurisdictionSetting", key, null, new { value, unit, legalSource, article, j.EffectiveFrom });
        await _db.SaveChangesAsync(ct);
        return j;
    }

    public async Task<JurisdictionSetting> ApproveAsync(long id, CancellationToken ct)
    {
        Guard.Require(_user, Perm.JurisdictionApprove);
        var j = await _db.Set<JurisdictionSetting>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Jurisdiction setting", id);
        if (j.Status is not ("PendingApproval" or "ToBeConfirmed")) throw new ConflictException($"Setting is {j.Status}.");
        if (string.IsNullOrWhiteSpace(j.LegalSource) || string.IsNullOrWhiteSpace(j.ArticleReference))
            throw new ValidationException("legalSource", "Unreferenced jurisdiction values cannot be approved; add the legal source and article reference.");
        Guard.FourEyes(j.CreatedBy ?? "", _user.Username, "Jurisdiction setting approval");
        var overlapping = await _db.Set<JurisdictionSetting>().Where(x => x.Key == j.Key && x.Id != j.Id && x.Status == "Approved" && (x.EffectiveTo == null || x.EffectiveTo > j.EffectiveFrom)).ToListAsync(ct);
        foreach (var o in overlapping)
        {
            if (o.EffectiveFrom >= j.EffectiveFrom) o.Status = "Superseded";
            o.EffectiveTo = j.EffectiveFrom;
        }
        j.Status = "Approved";
        j.ApprovedBy = _user.Username;
        j.ApprovedAt = _clock.UtcNow;
        _audit.Record("JURISDICTION_APPROVE", "JurisdictionSetting", j.Key, null, new { j.Value, j.EffectiveFrom, j.LegalSource });
        await _db.SaveChangesAsync(ct);
        return j;
    }
}
