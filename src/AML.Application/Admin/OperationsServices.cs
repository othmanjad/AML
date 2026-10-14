using AML.Application.Actions;
using AML.Application.Common;
using AML.Application.Reporting;
using AML.Application.Screening;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Admin;

public sealed class OperationsService
{
    private readonly IAmlDbContext _db;
    private readonly IClock _clock;
    private readonly ICurrentUser _user;
    private readonly ICoreSystemAdapter _core;
    private readonly IFiuSubmissionChannel _fiu;
    private readonly IScreeningProvider _screening;

    public OperationsService(IAmlDbContext db, IClock clock, ICurrentUser user, ICoreSystemAdapter core, IFiuSubmissionChannel fiu, IScreeningProvider screening)
    {
        _db = db; _clock = clock; _user = user; _core = core; _fiu = fiu; _screening = screening;
    }

    public async Task<object> IngestionRunsAsync(int take, CancellationToken ct) =>
        await _db.Set<IngestionRun>().AsNoTracking().OrderByDescending(r => r.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);

    public async Task<PagedResult<IngestionRecord>> RecordsAsync(string? status, long? runId, PageRequest page, CancellationToken ct)
    {
        var q = _db.Set<IngestionRecord>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        if (runId is not null) q = q.Where(r => r.RunId == runId);
        return await q.OrderByDescending(r => r.Id).ToPagedAsync(page, ct);
    }

    public async Task<PagedResult<DataQualityIssue>> DataQualityAsync(string? type, string? status, PageRequest page, CancellationToken ct)
    {
        var q = _db.Set<DataQualityIssue>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(d => d.IssueType == type);
        q = q.Where(d => d.Status == (status ?? "Open"));
        return await q.OrderByDescending(d => d.Id).ToPagedAsync(page, ct);
    }

    public async Task<object> ResolveIssueAsync(long id, string note, IAuditService audit, CancellationToken ct)
    {
        var d = await _db.Set<DataQualityIssue>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Issue", id);
        if (string.IsNullOrWhiteSpace(note)) throw new ValidationException("note", "A resolution note is required.");
        d.Status = "Resolved"; d.ResolvedAt = _clock.UtcNow; d.ResolvedBy = _user.Username;
        audit.Record("DQ_RESOLVE", "DataQualityIssue", id.ToString(), null, null, note);
        await _db.SaveChangesAsync(ct);
        return d;
    }

    public async Task<object> HealthAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var jobs = await _db.Set<JobHeartbeat>().AsNoTracking().OrderBy(j => j.JobName).ToListAsync(ct);
        var freshness = await _db.Set<Transaction>().AsNoTracking().GroupBy(t => t.SourceSystem).Select(g => new { source = g.Key, lastIngestedAt = g.Max(t => t.IngestedAt), lastTransactionAt = g.Max(t => t.TimestampUtc) }).ToListAsync(ct);
        var providers = await _db.Set<ScreeningProviderStatus>().AsNoTracking().ToListAsync(ct);
        var retry = await _db.Set<RetryQueueItem>().AsNoTracking().GroupBy(r => new { r.Kind, r.Status }).Select(g => new { g.Key.Kind, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
        var outbox = new { pending = await _db.Set<OutboxMessage>().CountAsync(o => o.ProcessedAt == null, ct), published = await _db.Set<PublishedEvent>().CountAsync(ct) };
        var ingestion = await _db.Set<IngestionRecord>().AsNoTracking().GroupBy(r => r.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync(ct);
        var ruleExec = await _db.Set<AmlRuleExecution>().AsNoTracking().OrderByDescending(e => e.Id).Take(20).Select(e => new { e.Id, e.RuleCode, e.RuleVersion, e.Mode, e.Status, e.Matches, e.ErrorCount, e.StartedAt, e.CorrelationId }).ToListAsync(ct);
        var opsAlerts = await _db.Set<OperationalAlert>().AsNoTracking().Where(o => o.Status == "Open").OrderByDescending(o => o.Id).Take(50).ToListAsync(ct);
        return new
        {
            generatedAt = now,
            jobs = jobs.Select(j => new { j.JobName, j.LastRunAt, j.LastStatus, j.LastError, j.ConsecutiveFailures, j.LastDurationMs, stale = j.LastRunAt < now.AddMinutes(-15) }),
            dataFreshness = freshness, screeningProviders = providers.Select(p => new { p.ProviderCode, p.IsAvailable, p.SimulationMode, p.LastSuccessAt, p.LastFailureAt, p.LastError }),
            retryQueue = retry, outbox, ingestion, ruleExecutions = ruleExec, operationalAlerts = opsAlerts,
            integrations = new { coreAdapter = _core.Mode, fiuChannel = _fiu.Name, screeningProvider = _screening.Code }
        };
    }

    public async Task<object> DuplicatesAsync(CancellationToken ct) =>
        await _db.Set<IngestionRecord>().AsNoTracking().Where(r => r.Status == "Duplicate" || r.Status == "Conflict").OrderByDescending(r => r.Id).Take(500)
            .Select(r => new { r.Id, r.SourceSystem, r.SourceTransactionId, r.Status, r.PayloadHash, r.ReceivedAt, r.TransactionId, r.CorrelationId }).ToListAsync(ct);

    public async Task<object> ScreeningQueueAsync(CancellationToken ct) =>
        await _db.Set<ScreeningRequest>().AsNoTracking().Where(r => r.Status == "Unavailable" || r.Status == "Failed" || r.Status == "Pending").OrderBy(r => r.NextAttemptAt)
            .Select(r => new { r.Id, r.SubjectType, r.SubjectId, r.ProviderCode, r.Status, r.Attempts, r.NextAttemptAt, r.LastError, r.FailureHistory, r.CreatedAt }).Take(500).ToListAsync(ct);
}

public sealed class DashboardService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly AccessPolicy _access;
    private readonly IClock _clock;

    public DashboardService(IAmlDbContext db, ICurrentUser user, AccessPolicy access, IClock clock)
    {
        _db = db; _user = user; _access = access; _clock = clock;
    }

    /// <summary>Counts are computed only over records the user may see (no restricted-item inference).</summary>
    public async Task<object> GetAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var customers = _access.Customers();
        var custIds = customers.Select(c => c.Id);
        var closed = AlertStatuses.Closed;
        var alerts = _access.Alerts().Where(a => !a.IsBacktest);
        var cases = _access.Cases();
        var risk = await _db.Set<CurrentCustomerRiskProfile>().Where(p => custIds.Contains(p.CustomerId)).GroupBy(p => p.Level).Select(g => new { level = g.Key, count = g.Count() }).ToListAsync(ct);
        return new
        {
            customers = new { total = await customers.CountAsync(ct), byType = await customers.GroupBy(c => c.CustomerType).Select(g => new { type = g.Key, count = g.Count() }).ToListAsync(ct) },
            riskDistribution = risk,
            alerts = _user.Has(Perm.AlertsRead) ? new
            {
                open = await alerts.CountAsync(a => !closed.Contains(a.Status) && a.Status != AlertStatuses.ConvertedToCase, ct),
                overdue = await alerts.CountAsync(a => !closed.Contains(a.Status) && a.Status != AlertStatuses.ConvertedToCase && a.DueAt < now, ct),
                bySeverity = await alerts.Where(a => !closed.Contains(a.Status)).GroupBy(a => a.Severity).Select(g => new { severity = g.Key, count = g.Count() }).ToListAsync(ct)
            } : null,
            cases = _user.Has(Perm.CasesRead) ? new { open = await cases.CountAsync(c => c.Status != CaseStatuses.Closed, ct), overdue = await cases.CountAsync(c => c.Status != CaseStatuses.Closed && c.DueDate < now, ct) } : null,
            reviews = new { overdue = await _db.Set<RiskReviewTask>().CountAsync(t => custIds.Contains(t.CustomerId) && t.Status != "Completed" && t.DueDate < today, ct) },
            screening = await customers.GroupBy(c => c.ScreeningStatus).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync(ct),
            transactions = new { last24h = await _db.Set<Transaction>().CountAsync(t => t.TimestampUtc >= now.AddDays(-1), ct), dataQualityOpen = await _db.Set<DataQualityIssue>().CountAsync(d => d.Status == "Open", ct) },
            holds = _user.Has(Perm.HoldsWork) || _user.Has(Perm.ActionsRead) ? new { held = await _db.Set<HoldQueueItem>().CountAsync(h => h.Status == "Held", ct) } : null
        };
    }
}
