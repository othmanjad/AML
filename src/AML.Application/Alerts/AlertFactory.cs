using AML.Application.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Alerts;

public sealed class AlertDraft
{
    public long TenantId { get; set; }
    public string Source { get; set; } = "Rule";
    public string AlertType { get; set; } = "";
    public long? RuleId { get; set; }
    public long? RuleVersionId { get; set; }
    public int? RuleVersion { get; set; }
    public string? RuleCode { get; set; }
    public long? ExecutionId { get; set; }
    public long? PrimaryCustomerId { get; set; }
    public string SubjectKey { get; set; } = "";
    public string Severity { get; set; } = "Medium";
    public decimal? Score { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string DeduplicationKey { get; set; } = "";
    public string EvidenceJson { get; set; } = "{}";
    public string MatchKey { get; set; } = "";
    public string MatchedConditionsJson { get; set; } = "[]";
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public IReadOnlyCollection<long> TransactionIds { get; set; } = Array.Empty<long>();
    public IReadOnlyCollection<long> CustomerIds { get; set; } = Array.Empty<long>();
    public bool IsBacktest { get; set; }
    public bool IsTestData { get; set; }
    /// <summary>Earlier triggers merged into the same alert in this run (recorded as lightweight match records).</summary>
    public IReadOnlyList<AlertMatchDraft> AdditionalMatches { get; set; } = Array.Empty<AlertMatchDraft>();
}

public sealed record AlertMatchDraft(string MatchKey, string MatchedConditionsJson, string EvidenceJson, DateTime WindowStart, DateTime WindowEnd);

public sealed record AlertUpsertResult(AmlAlert Alert, bool Created, bool NewMatch);

/// <summary>Idempotent alert creation keyed by a configurable deduplication key (unique per tenant).</summary>
public sealed class AlertFactory
{
    private readonly IAmlDbContext _db;
    private readonly IClock _clock;
    private readonly TenantSettingsService _settings;

    public AlertFactory(IAmlDbContext db, IClock clock, TenantSettingsService settings)
    {
        _db = db; _clock = clock; _settings = settings;
    }

    public async Task<AlertUpsertResult> UpsertAsync(AlertDraft d, CancellationToken ct)
    {
        var existing = await _db.Set<AmlAlert>().IgnoreQueryFilters().FirstOrDefaultAsync(a => a.TenantId == d.TenantId && a.DeduplicationKey == d.DeduplicationKey, ct);
        if (existing is not null) return new AlertUpsertResult(existing, false, await MergeAsync(existing, d, ct));

        var now = _clock.UtcNow;
        var calendar = await _settings.CalendarAsync(d.TenantId, ct);
        var sla = await _db.Set<AlertSlaPolicy>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == d.TenantId && s.Severity == d.Severity, ct);
        var alert = new AmlAlert
        {
            TenantId = d.TenantId, AlertNumber = "PENDING-" + Guid.NewGuid().ToString("N"), Source = d.Source, AlertType = d.AlertType, RuleId = d.RuleId,
            RuleVersionId = d.RuleVersionId, RuleVersion = d.RuleVersion, RuleCode = d.RuleCode, ExecutionId = d.ExecutionId, PrimaryCustomerId = d.PrimaryCustomerId,
            SubjectKey = d.SubjectKey, Severity = d.Severity, Score = d.Score, Title = d.Title, Description = d.Description, CreatedAt = now,
            DueAt = calendar.AddWorkingDays(now, sla?.WorkingDays ?? 5), DeduplicationKey = d.DeduplicationKey, EvidenceJson = d.EvidenceJson,
            IsBacktest = d.IsBacktest, IsTestData = d.IsTestData
        };
        var ownTx = await _db.Database.BeginIfNoneAsync(ct);
        try
        {
            _db.Set<AmlAlert>().Add(alert);
            await _db.SaveChangesAsync(ct);
            alert.AlertNumber = $"ALR-{now:yyyyMM}-{alert.Id:D6}";
            await MergeAsync(alert, d, ct);
            _db.Set<AlertStatusHistory>().Add(new AlertStatusHistory { TenantId = d.TenantId, AlertId = alert.Id, FromStatus = "", ToStatus = AlertStatuses.New, Actor = "system", At = now, Reason = d.Source });
            await _db.SaveChangesAsync(ct);
            await ownTx.CommitIfOwnedAsync(ct);
            return new AlertUpsertResult(alert, true, true);
        }
        catch (DbUpdateException ex) when (Customers.CustomerService.IsUniqueViolation(ex))
        {
            // A concurrent run created the same logical alert: merge into it instead of duplicating.
            await ownTx.RollbackIfOwnedAsync(ct);
            _db.Entry(alert).State = EntityState.Detached;
            foreach (var e in _db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached;
            var winner = await _db.Set<AmlAlert>().IgnoreQueryFilters().FirstAsync(a => a.TenantId == d.TenantId && a.DeduplicationKey == d.DeduplicationKey, ct);
            return new AlertUpsertResult(winner, false, await MergeAsync(winner, d, ct));
        }
        finally
        {
            if (ownTx is not null) await ownTx.DisposeAsync();
        }
    }

    private async Task<bool> MergeAsync(AmlAlert alert, AlertDraft d, CancellationToken ct)
    {
        var newMatch = false;
        if (!string.IsNullOrEmpty(d.MatchKey) &&
            !await _db.Set<AmlAlertRuleMatch>().IgnoreQueryFilters().AnyAsync(m => m.AlertId == alert.Id && m.MatchKey == d.MatchKey, ct) &&
            !_db.ChangeTracker.Entries<AmlAlertRuleMatch>().Any(e => e.Entity.AlertId == alert.Id && e.Entity.MatchKey == d.MatchKey))
        {
            _db.Set<AmlAlertRuleMatch>().Add(new AmlAlertRuleMatch
            {
                TenantId = alert.TenantId, AlertId = alert.Id, ExecutionId = d.ExecutionId, RuleVersionId = d.RuleVersionId, RuleVersion = d.RuleVersion,
                MatchKey = d.MatchKey, MatchedConditionsJson = d.MatchedConditionsJson, EvidenceJson = d.EvidenceJson, WindowStart = d.WindowStart,
                WindowEnd = d.WindowEnd, MatchedAt = _clock.UtcNow
            });
            newMatch = true;
        }
        if (d.AdditionalMatches.Count > 0)
        {
            var keys = d.AdditionalMatches.Select(m => m.MatchKey).ToList();
            var known = (await _db.Set<AmlAlertRuleMatch>().IgnoreQueryFilters().Where(m => m.AlertId == alert.Id && keys.Contains(m.MatchKey)).Select(m => m.MatchKey).ToListAsync(ct)).ToHashSet();
            foreach (var m in d.AdditionalMatches.Where(m => !known.Contains(m.MatchKey) && m.MatchKey != d.MatchKey))
                _db.Set<AmlAlertRuleMatch>().Add(new AmlAlertRuleMatch
                {
                    TenantId = alert.TenantId, AlertId = alert.Id, ExecutionId = d.ExecutionId, RuleVersionId = d.RuleVersionId, RuleVersion = d.RuleVersion,
                    MatchKey = m.MatchKey, MatchedConditionsJson = m.MatchedConditionsJson, EvidenceJson = m.EvidenceJson, WindowStart = m.WindowStart,
                    WindowEnd = m.WindowEnd, MatchedAt = _clock.UtcNow
                });
        }
        var existingTx = await _db.Set<AmlAlertTransaction>().IgnoreQueryFilters().Where(x => x.AlertId == alert.Id).Select(x => x.TransactionId).ToListAsync(ct);
        foreach (var t in d.TransactionIds.Except(existingTx).Distinct())
            _db.Set<AmlAlertTransaction>().Add(new AmlAlertTransaction { TenantId = alert.TenantId, AlertId = alert.Id, TransactionId = t });
        var existingCust = await _db.Set<AmlAlertCustomer>().IgnoreQueryFilters().Where(x => x.AlertId == alert.Id).Select(x => x.CustomerId).ToListAsync(ct);
        var customers = d.CustomerIds.Concat(d.PrimaryCustomerId is { } p ? new[] { p } : Array.Empty<long>()).Distinct();
        foreach (var c in customers.Except(existingCust))
            _db.Set<AmlAlertCustomer>().Add(new AmlAlertCustomer { TenantId = alert.TenantId, AlertId = alert.Id, CustomerId = c, Role = c == d.PrimaryCustomerId ? "Primary" : "Related" });
        await _db.SaveChangesAsync(ct);
        return newMatch;
    }
}
