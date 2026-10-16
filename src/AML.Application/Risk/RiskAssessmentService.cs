using System.Text.Json;
using AML.Application.Common;
using AML.Application.Customers;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AML.Application.Risk;

public sealed record AssessmentFactorDto(string FactorCode, string FactorName, string CategoryCode, int FactorVersion, string DataSource, string? DataReference,
    string? ObservedValue, string DataStatus, decimal? NormalizedScore, decimal Weight, decimal Contribution, bool Included, string? MissingHandling, string Explanation);

public sealed record AssessmentDto(long Id, long CustomerId, string MethodologyCode, int MethodologyVersion, string AggregationMethod, string Trigger, string? TriggerEventId,
    string? TriggerReason, decimal CalculatedScore, string CalculatedLevel, decimal? PreviousScore, string? PreviousLevel, string? MandatoryOverrideLevel,
    string FinalLevel, IReadOnlyList<string> AppliedOverrides, IReadOnlyList<string> AppliedFloors, bool SanctionsWorkflow, bool HasMissingData, string Explanation,
    string Status, bool RequiresApproval, string? ApprovedBy, DateTime? ApprovedAt, DateTime AsOf, DateTime CalculatedAt, string CreatedBy, string? CorrelationId,
    IReadOnlyList<AssessmentFactorDto> Factors, string? ConfigSnapshotJson);

public sealed record SimulationResultDto(string MethodologyCode, int MethodologyVersion, string AggregationMethod, string CustomerType, decimal CalculatedScore,
    string CalculatedLevel, string FinalLevel, string? MandatoryOverrideLevel, IReadOnlyList<string> AppliedOverrides, IReadOnlyList<string> AppliedFloors,
    bool SanctionsWorkflow, bool RequiresApproval, bool HasMissingData, IReadOnlyList<string> Warnings, IReadOnlyDictionary<string, decimal> CategoryScores,
    IReadOnlyList<AssessmentFactorDto> Factors, IReadOnlyList<object> Thresholds, string Explanation, bool Persisted);

public sealed record OverrideRequestDto(long Id, long CustomerId, long AssessmentId, decimal CalculatedScore, string CalculatedLevel, string RequestedLevel, string Reason,
    string? Evidence, string RequestedBy, DateTime RequestedAt, string Status, string? DecidedBy, DateTime? DecidedAt, string? DecisionReason, string? FinalLevel);

public sealed class RiskAssessmentService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly RiskDataSourceProvider _sources;
    private readonly CustomerService _customers;
    private readonly ICorrelationContext _corr;
    private readonly TenantSettingsService _settings;
    private readonly IFaultInjector _faults;
    private readonly ILogger<RiskAssessmentService> _log;
    private readonly RiskScoringEngine _engine = new();

    private readonly Lookups.LookupScoreProvider _lookupScores;

    public RiskAssessmentService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, RiskDataSourceProvider sources, CustomerService customers,
        ICorrelationContext corr, TenantSettingsService settings, IFaultInjector faults, ILogger<RiskAssessmentService> log, Lookups.LookupScoreProvider lookupScores)
    {
        _lookupScores = lookupScores;
        _db = db; _user = user; _clock = clock; _audit = audit; _sources = sources; _customers = customers; _corr = corr; _settings = settings; _faults = faults; _log = log;
    }

    public static AssessmentDto ToDto(CustomerRiskAssessment a, IEnumerable<CustomerRiskAssessmentFactor> factors, bool includeSnapshot = false) =>
        new(a.Id, a.CustomerId, a.MethodologyCode, a.MethodologyVersion, a.AggregationMethod, a.Trigger, a.TriggerEventId, a.TriggerReason, a.CalculatedScore,
            a.CalculatedLevel, a.PreviousScore, a.PreviousLevel, a.MandatoryOverrideLevel, a.FinalLevel, Lines(a.AppliedOverrides), Lines(a.AppliedFloors),
            a.SanctionsWorkflow, a.HasMissingData, a.Explanation, a.Status, a.RequiresApproval, a.ApprovedBy, a.ApprovedAt, a.AsOf, a.CalculatedAt, a.CreatedBy,
            a.CorrelationId, factors.Select(ToDto).ToList(), includeSnapshot ? a.ConfigSnapshotJson : null);

    private static IReadOnlyList<string> Lines(string? s) => string.IsNullOrEmpty(s) ? Array.Empty<string>() : s.Split('\n');

    public static AssessmentFactorDto ToDto(CustomerRiskAssessmentFactor f) => new(f.FactorCode, f.FactorName, f.CategoryCode, f.FactorVersion, f.DataSource,
        f.DataReference, f.ObservedValue, f.DataStatus, f.NormalizedScore, f.Weight, f.Contribution, f.Included, f.MissingHandling, f.Explanation);

    /// <summary>Loads the methodology version graph used by the engine.</summary>
    public async Task<RiskMethodology> LoadGraphAsync(long methodologyId, CancellationToken ct)
    {
        var m = await _db.Set<RiskMethodology>().IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Factors).ThenInclude(f => f.Mappings)
            .Include(x => x.Thresholds).Include(x => x.Overrides).Include(x => x.Floors)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == methodologyId, ct) ?? throw new NotFoundException("Methodology", methodologyId);
        return m;
    }

    /// <summary>Selects the approved methodology effective at the assessment timestamp for the customer type.</summary>
    public async Task<RiskMethodology?> SelectMethodologyAsync(long tenantId, string customerType, DateTime asOf, CancellationToken ct)
    {
        var candidates = await _db.Set<RiskMethodology>().IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.Status == ConfigStatus.Approved && m.EffectiveFrom <= asOf && (m.EffectiveTo == null || m.EffectiveTo > asOf))
            .ToListAsync(ct);
        var chosen = candidates.Where(m => !string.IsNullOrWhiteSpace(m.AppliesToCustomerTypes) && RiskScoringEngine.AppliesTo(m.AppliesToCustomerTypes, customerType))
                         .OrderByDescending(m => m.EffectiveFrom).ThenByDescending(m => m.Version).FirstOrDefault()
                     ?? candidates.Where(m => string.IsNullOrWhiteSpace(m.AppliesToCustomerTypes)).OrderByDescending(m => m.EffectiveFrom).ThenByDescending(m => m.Version).FirstOrDefault();
        return chosen is null ? null : await LoadGraphAsync(chosen.Id, ct);
    }

    public async Task<AssessmentDto> AssessAsync(long customerId, string trigger, string? triggerEventId, string? reason, DateTime? asOf, CancellationToken ct)
    {
        var validTriggers = new[] { "Onboarding", "FieldChange", "PeriodicReview", "ScreeningResult", "BehaviourChange", "AlertEscalation", "Manual", "AuthorityRequest", "Scheduled" };
        if (!validTriggers.Contains(trigger)) throw new ValidationException("trigger", $"Unknown trigger '{trigger}'.");
        if (trigger == "Manual" && string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A reason is required for manual reassessment.");
        var c = await _customers.LoadVisibleAsync(customerId, ct);

        if (!string.IsNullOrWhiteSpace(triggerEventId))
        {
            var existing = await _db.Set<CustomerRiskAssessment>().AsNoTracking().FirstOrDefaultAsync(a => a.CustomerId == customerId && a.TriggerEventId == triggerEventId, ct);
            if (existing is not null)
            {
                var ef = await _db.Set<CustomerRiskAssessmentFactor>().AsNoTracking().Where(f => f.AssessmentId == existing.Id).ToListAsync(ct);
                return ToDto(existing, ef);
            }
        }

        var at = asOf ?? _clock.UtcNow;
        var m = await SelectMethodologyAsync(c.TenantId, c.CustomerType, at, ct)
                ?? throw new ConflictException($"No approved risk methodology is effective for customer type {c.CustomerType} at {at:O}.");
        await _lookupScores.InjectAsync(m, at, ct);
        var inputs = await _sources.GatherAsync(c, m.Factors.Select(f => f.DataSource).Concat(m.Overrides.Select(o => o.ConditionDataSource)), at, ct);
        _faults.Check("risk.calculate");
        var r = _engine.Evaluate(m, c.CustomerType, inputs, at);

        var profile = await _db.Set<CurrentCustomerRiskProfile>().FirstOrDefaultAsync(p => p.CustomerId == customerId, ct);
        var a = new CustomerRiskAssessment
        {
            TenantId = c.TenantId, CustomerId = customerId, MethodologyId = m.Id, MethodologyCode = m.Code, MethodologyVersion = m.Version,
            AggregationMethod = m.AggregationMethod, Trigger = trigger, TriggerEventId = string.IsNullOrWhiteSpace(triggerEventId) ? null : triggerEventId,
            TriggerReason = reason, CorrelationId = _corr.CorrelationId, CalculatedScore = r.CalculatedScore, CalculatedLevel = r.CalculatedLevel,
            PreviousScore = profile?.CalculatedScore, PreviousLevel = profile?.Level, MandatoryOverrideLevel = r.MandatoryOverrideLevel, FinalLevel = r.FinalLevel,
            AppliedOverrides = r.AppliedOverrides.Count == 0 ? null : string.Join('\n', r.AppliedOverrides),
            AppliedFloors = r.AppliedFloors.Count == 0 ? null : string.Join('\n', r.AppliedFloors),
            SanctionsWorkflow = r.SanctionsWorkflow, HasMissingData = r.HasMissingData, Explanation = r.Explanation,
            RequiresApproval = r.RequiresApproval, Status = r.RequiresApproval ? "PendingApproval" : "Completed", AsOf = at, CalculatedAt = _clock.UtcNow,
            CreatedBy = _user.Username, ConfigSnapshotJson = Snapshot(m)
        };
        foreach (var f in r.Factors) { f.TenantId = c.TenantId; a.Factors.Add(f); }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            _db.Set<CustomerRiskAssessment>().Add(a);
            await _db.SaveChangesAsync(ct);
            _faults.Check("risk.persist");
            if (!r.RequiresApproval) await ApplyToProfileAsync(c, a, ct);
            _audit.Record("RISK_ASSESSMENT", "Customer", customerId.ToString(), profile is null ? null : new { profile.Level, profile.Score },
                new { assessmentId = a.Id, a.MethodologyCode, a.MethodologyVersion, a.CalculatedScore, a.CalculatedLevel, a.FinalLevel, a.Status, trigger }, reason);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (CustomerService.IsUniqueViolation(ex) && a.TriggerEventId is not null)
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            var existing = await _db.Set<CustomerRiskAssessment>().AsNoTracking().FirstAsync(x => x.CustomerId == customerId && x.TriggerEventId == a.TriggerEventId, ct);
            return ToDto(existing, await _db.Set<CustomerRiskAssessmentFactor>().AsNoTracking().Where(f => f.AssessmentId == existing.Id).ToListAsync(ct));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            _log.LogError(ex, "Risk assessment failed for customer {CustomerId} (correlation {CorrelationId})", customerId, _corr.CorrelationId);
            throw;
        }
        return ToDto(a, a.Factors);
    }

    public static string Snapshot(RiskMethodology m) => JsonSerializer.Serialize(new
    {
        m.Id, m.Code, m.Name, m.Version, m.Status, m.AggregationMethod, m.Cap, m.ScorePrecision, m.AppliesToCustomerTypes, m.ApprovalRequiredLevels, m.CategoryMatrixJson,
        m.EffectiveFrom, m.EffectiveTo, m.ApprovedBy, m.ApprovedAt,
        Factors = m.Factors.Select(f => new
        {
            f.Code, f.Name, f.CategoryCode, f.DataSource, f.Weight, f.Version, f.ApplicableCustomerTypes, f.MissingDataHandling, f.MissingDataScore, f.FreshnessDays, f.IsActive, f.ScoringMode, f.LookupCategory,
            Mappings = f.Mappings.Select(mp => new { mp.MatchType, mp.Value, mp.RangeMin, mp.RangeMax, mp.Score, mp.Priority, mp.Label })
        }),
        Thresholds = m.Thresholds.Select(t => new { t.Level, t.MinScore, t.MaxScore, t.ReviewMonths }),
        Overrides = m.Overrides.Select(o => new { o.Code, o.ConditionDataSource, o.ConditionOperator, o.ConditionValue, o.MinimumLevel, o.IsSanctionsWorkflow, o.Reason }),
        Floors = m.Floors.Select(f => new { f.CategoryCode, f.TriggerLevel, f.MinimumLevel })
    });

    private async Task ApplyToProfileAsync(Customer c, CustomerRiskAssessment a, CancellationToken ct)
    {
        var p = await _db.Set<CurrentCustomerRiskProfile>().FirstOrDefaultAsync(x => x.CustomerId == c.Id, ct);
        if (p is null) _db.Set<CurrentCustomerRiskProfile>().Add(p = new CurrentCustomerRiskProfile { TenantId = c.TenantId, CustomerId = c.Id });
        var thresholds = await _db.Set<RiskThreshold>().IgnoreQueryFilters().AsNoTracking().Where(t => t.MethodologyId == a.MethodologyId).ToListAsync(ct);
        var months = thresholds.FirstOrDefault(t => t.Level == a.FinalLevel)?.ReviewMonths ?? 12;
        if (c.DueDiligenceLevel is { } dd)
        {
            var ddDef = await _db.Set<DueDiligenceLevelDefinition>().AsNoTracking().FirstOrDefaultAsync(d => d.Code == dd, ct);
            if (ddDef is not null) months = Math.Min(months, ddDef.ReviewFrequencyMonths);
        }
        p.Score = a.CalculatedScore;
        p.CalculatedScore = a.CalculatedScore;
        p.CalculatedLevel = a.CalculatedLevel;
        p.Level = a.FinalLevel;
        p.MonitoringLevel = a.FinalLevel is RiskLevels.High or RiskLevels.Critical ? "Enhanced" : "Standard";
        p.LastAssessmentId = a.Id;
        p.LastAssessedAt = a.CalculatedAt;
        p.NextReviewDate = DateOnly.FromDateTime(a.AsOf.AddMonths(months));
        p.OverrideApplied = a.MandatoryOverrideLevel is not null || a.AppliedFloors is not null;
        p.OverrideType = a.MandatoryOverrideLevel is not null ? "Mandatory" : a.AppliedFloors is not null ? "CategoryFloor" : null;
        p.OverrideReason = a.AppliedOverrides ?? a.AppliedFloors;
        p.SanctionsWorkflow = a.SanctionsWorkflow;
        p.MethodologyCode = a.MethodologyCode;
        p.MethodologyVersion = a.MethodologyVersion;
        p.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<AssessmentDto> DecideApprovalAsync(long assessmentId, bool approve, string? note, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RiskApprove);
        var a = await _db.Set<CustomerRiskAssessment>().FirstOrDefaultAsync(x => x.Id == assessmentId, ct) ?? throw new NotFoundException("Assessment", assessmentId);
        var c = await _customers.LoadVisibleAsync(a.CustomerId, ct);
        if (a.Status != "PendingApproval") throw new ConflictException("Assessment is not pending approval.");
        Guard.FourEyes(a.CreatedBy, _user.Username, "Risk assessment approval");
        a.Status = approve ? "Approved" : "Rejected";
        a.ApprovedBy = _user.Username;
        a.ApprovedAt = _clock.UtcNow;
        a.ApprovalNote = note;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.SaveChangesAsync(ct);
        if (approve) await ApplyToProfileAsync(c, a, ct);
        _audit.Record(approve ? "RISK_ASSESSMENT_APPROVE" : "RISK_ASSESSMENT_REJECT", "Customer", a.CustomerId.ToString(), null, new { assessmentId, a.FinalLevel }, note);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(assessmentId, ct);
    }

    public async Task<AssessmentDto> GetAsync(long id, CancellationToken ct)
    {
        var a = await _db.Set<CustomerRiskAssessment>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Assessment", id);
        await _customers.LoadVisibleAsync(a.CustomerId, ct);
        var f = await _db.Set<CustomerRiskAssessmentFactor>().AsNoTracking().Where(x => x.AssessmentId == id).OrderBy(x => x.CategoryCode).ThenBy(x => x.FactorCode).ToListAsync(ct);
        return ToDto(a, f, includeSnapshot: true);
    }

    public async Task<List<AssessmentDto>> HistoryAsync(long customerId, CancellationToken ct)
    {
        await _customers.LoadVisibleAsync(customerId, ct);
        var list = await _db.Set<CustomerRiskAssessment>().AsNoTracking().Where(a => a.CustomerId == customerId).OrderByDescending(a => a.Id).Take(100).ToListAsync(ct);
        var ids = list.Select(a => a.Id).ToList();
        var factors = await _db.Set<CustomerRiskAssessmentFactor>().AsNoTracking().Where(f => ids.Contains(f.AssessmentId)).ToListAsync(ct);
        return list.Select(a => ToDto(a, factors.Where(f => f.AssessmentId == a.Id))).ToList();
    }

    public async Task<PagedResult<AssessmentDto>> ListAsync(string? status, string? level, PageRequest page, CancellationToken ct)
    {
        var visible = _customersVisibleIds();
        var q = _db.Set<CustomerRiskAssessment>().AsNoTracking().Where(a => visible.Contains(a.CustomerId));
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status);
        if (!string.IsNullOrWhiteSpace(level)) q = q.Where(a => a.FinalLevel == level);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.Id).Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<AssessmentDto>(items.Select(a => ToDto(a, Array.Empty<CustomerRiskAssessmentFactor>())).ToList(), page.SafePage, page.SafePageSize, total);
    }

    private IQueryable<long> _customersVisibleIds() => new AccessPolicy(_user, _db).Customers().Select(c => c.Id);

    // ---------------- manual overrides ----------------
    public async Task<OverrideRequestDto> RequestOverrideAsync(long customerId, string requestedLevel, string? reason, string? evidence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A reason is required for a manual risk override.");
        var c = await _customers.LoadVisibleAsync(customerId, ct);
        var profile = await _db.Set<CurrentCustomerRiskProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.CustomerId == customerId, ct)
                      ?? throw new ConflictException("Customer has no finalized risk assessment to override.");
        var a = await _db.Set<CustomerRiskAssessment>().AsNoTracking().FirstAsync(x => x.Id == profile.LastAssessmentId, ct);
        var thresholds = await _db.Set<RiskThreshold>().IgnoreQueryFilters().AsNoTracking().Where(t => t.MethodologyId == a.MethodologyId).ToListAsync(ct);
        if (RiskScoringEngine.Rank(thresholds, requestedLevel) < 0) throw new ValidationException("requestedLevel", $"Unknown risk level '{requestedLevel}'.");
        var lowering = RiskScoringEngine.Rank(thresholds, requestedLevel) < RiskScoringEngine.Rank(thresholds, profile.Level);
        if (lowering && (profile.SanctionsWorkflow || a.SanctionsWorkflow || c.ScreeningStatus == "ConfirmedMatch"))
            throw new ConflictException("Override blocked by policy: a confirmed sanctions match uses the dedicated sanctions workflow and cannot be lowered.");
        if (lowering && a.MandatoryOverrideLevel is { } min && RiskScoringEngine.Rank(thresholds, requestedLevel) < RiskScoringEngine.Rank(thresholds, min))
            throw new ConflictException($"Override blocked by policy: mandatory override requires at least {min}.");
        if (await _db.Set<RiskOverrideRequest>().AnyAsync(o => o.CustomerId == customerId && o.Status == "Pending", ct))
            throw new ConflictException("An override request is already pending for this customer.");
        var o = new RiskOverrideRequest
        {
            TenantId = c.TenantId, CustomerId = customerId, AssessmentId = a.Id, CalculatedScore = a.CalculatedScore, CalculatedLevel = profile.Level,
            RequestedLevel = requestedLevel, Reason = reason, Evidence = evidence, RequestedBy = _user.Username, RequestedAt = _clock.UtcNow
        };
        _db.Set<RiskOverrideRequest>().Add(o);
        _audit.Record("RISK_OVERRIDE_REQUEST", "Customer", customerId.ToString(), new { profile.Level }, new { requestedLevel, evidence }, reason);
        await _db.SaveChangesAsync(ct);
        return ToDto(o);
    }

    public static OverrideRequestDto ToDto(RiskOverrideRequest o) => new(o.Id, o.CustomerId, o.AssessmentId, o.CalculatedScore, o.CalculatedLevel, o.RequestedLevel, o.Reason,
        o.Evidence, o.RequestedBy, o.RequestedAt, o.Status, o.DecidedBy, o.DecidedAt, o.DecisionReason, o.FinalLevel);

    public async Task<OverrideRequestDto> DecideOverrideAsync(long requestId, bool approve, string? reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RiskOverrideApprove);
        var o = await _db.Set<RiskOverrideRequest>().FirstOrDefaultAsync(x => x.Id == requestId, ct) ?? throw new NotFoundException("Override request", requestId);
        await _customers.LoadVisibleAsync(o.CustomerId, ct);
        if (o.Status != "Pending") throw new ConflictException("Override request is not pending.");
        Guard.FourEyes(o.RequestedBy, _user.Username, "Risk override");
        if (!approve && string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A reason is required to reject an override.");
        var p = await _db.Set<CurrentCustomerRiskProfile>().FirstAsync(x => x.CustomerId == o.CustomerId, ct);
        o.Status = approve ? "Approved" : "Rejected";
        o.DecidedBy = _user.Username;
        o.DecidedAt = _clock.UtcNow;
        o.DecisionReason = reason;
        o.FinalLevel = approve ? o.RequestedLevel : p.Level;
        if (approve)
        {
            if (p.SanctionsWorkflow) throw new ConflictException("Override blocked by policy: sanctions workflow active.");
            var old = p.Level;
            p.Level = o.RequestedLevel;
            p.OverrideApplied = true;
            p.OverrideType = "Manual";
            p.OverrideReason = o.Reason;
            p.MonitoringLevel = p.Level is RiskLevels.High or RiskLevels.Critical ? "Enhanced" : "Standard";
            p.UpdatedAt = _clock.UtcNow;
            _audit.Record("RISK_OVERRIDE_APPROVE", "Customer", o.CustomerId.ToString(), new { Level = old, p.CalculatedScore }, new { p.Level, o.RequestedBy }, reason);
        }
        else _audit.Record("RISK_OVERRIDE_REJECT", "Customer", o.CustomerId.ToString(), null, new { o.RequestedLevel, finalLevel = p.Level }, reason);
        await _db.SaveChangesAsync(ct);
        return ToDto(o);
    }

    public async Task<List<OverrideRequestDto>> OverridesAsync(long? customerId, string? status, CancellationToken ct)
    {
        var visible = _customersVisibleIds();
        var q = _db.Set<RiskOverrideRequest>().AsNoTracking().Where(o => visible.Contains(o.CustomerId));
        if (customerId is not null) q = q.Where(o => o.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(o => o.Status == status);
        return (await q.OrderByDescending(o => o.Id).Take(500).ToListAsync(ct)).Select(ToDto).ToList();
    }

    // ---------------- review tasks ----------------
    public async Task<List<RiskReviewTask>> ReviewTasksAsync(long? customerId, string? status, bool? overdue, CancellationToken ct)
    {
        var visible = _customersVisibleIds();
        var q = _db.Set<RiskReviewTask>().AsNoTracking().Where(t => visible.Contains(t.CustomerId));
        if (customerId is not null) q = q.Where(t => t.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(t => t.Status == status);
        if (overdue == true)
        {
            var today = DateOnly.FromDateTime(_clock.UtcNow);
            q = q.Where(t => t.Status != "Completed" && t.DueDate < today);
        }
        return await q.OrderBy(t => t.DueDate).Take(1000).ToListAsync(ct);
    }

    /// <summary>Creates review tasks for profiles whose next review falls within the configured lead time.</summary>
    public async Task<int> ScheduleReviewTasksAsync(long tenantId, CancellationToken ct)
    {
        var lead = await _settings.IntSettingAsync("Risk:ReviewLeadDays", 30, tenantId, ct);
        var horizon = DateOnly.FromDateTime(_clock.UtcNow.AddDays(lead));
        var due = await _db.Set<CurrentCustomerRiskProfile>().IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.NextReviewDate != null && p.NextReviewDate <= horizon)
            .Where(p => !_db.Set<RiskReviewTask>().IgnoreQueryFilters().Any(t => t.CustomerId == p.CustomerId && t.Status != "Completed"))
            .ToListAsync(ct);
        var officers = await _db.Set<Customer>().IgnoreQueryFilters().Where(c => due.Select(d => d.CustomerId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.AssignedOfficer, ct);
        foreach (var p in due)
        {
            _db.Set<RiskReviewTask>().Add(new RiskReviewTask
            {
                TenantId = tenantId, CustomerId = p.CustomerId, DueDate = p.NextReviewDate!.Value, Owner = officers.GetValueOrDefault(p.CustomerId),
                Reason = $"Periodic review ({p.Level}, methodology {p.MethodologyCode} v{p.MethodologyVersion})", SourceAssessmentId = p.LastAssessmentId, CreatedAt = _clock.UtcNow
            });
        }
        if (due.Count > 0)
        {
            _audit.Record("RISK_REVIEW_TASKS_SCHEDULED", "RiskReviewTask", null, null, new { count = due.Count, horizon });
            await _db.SaveChangesAsync(ct);
        }
        return due.Count;
    }

    /// <summary>Escalates overdue review tasks once (status changes to Escalated).</summary>
    public async Task<int> EscalateOverdueReviewsAsync(long tenantId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow);
        var escalateTo = await _settings.SettingAsync("Risk:EscalationRole", RoleCodes.AmlManager, tenantId, ct);
        var tasks = await _db.Set<RiskReviewTask>().IgnoreQueryFilters().Where(t => t.TenantId == tenantId && t.Status == "Open" && t.DueDate < today).ToListAsync(ct);
        foreach (var t in tasks)
        {
            t.Status = "Escalated";
            t.EscalatedTo = escalateTo;
            t.EscalatedAt = _clock.UtcNow;
            _audit.Record("RISK_REVIEW_ESCALATED", "RiskReviewTask", t.Id.ToString(), new { Status = "Open" }, new { t.Status, t.EscalatedTo, t.DueDate });
        }
        await _db.SaveChangesAsync(ct);
        return tasks.Count;
    }

    public async Task<RiskReviewTask> CompleteReviewAsync(long taskId, string? evidence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evidence)) throw new ValidationException("evidence", "Completion evidence is required to complete a review task.");
        var t = await _db.Set<RiskReviewTask>().FirstOrDefaultAsync(x => x.Id == taskId, ct) ?? throw new NotFoundException("Review task", taskId);
        await _customers.LoadVisibleAsync(t.CustomerId, ct);
        if (t.Status == "Completed") throw new ConflictException("Review task already completed.");
        t.Status = "Completed";
        t.CompletionEvidence = evidence;
        t.CompletedBy = _user.Username;
        t.CompletedAt = _clock.UtcNow;
        _audit.Record("RISK_REVIEW_COMPLETE", "Customer", t.CustomerId.ToString(), null, new { taskId, evidence });
        await _db.SaveChangesAsync(ct);
        if (await _settings.BoolSettingAsync("Risk:ReassessOnReviewCompletion", true, t.TenantId, ct))
        {
            var a = await AssessAsync(t.CustomerId, "PeriodicReview", $"review:{t.Id}", "Periodic review completed", null, ct);
            t.ResultAssessmentId = a.Id;
            await _db.SaveChangesAsync(ct);
        }
        return t;
    }

    /// <summary>Event-triggered reassessment after dynamic field changes when the field is used by an active factor.</summary>
    public async Task<List<AssessmentDto>> OnFieldChangesAsync(long customerId, IEnumerable<(string Code, long HistoryId)> changes, CancellationToken ct)
    {
        var results = new List<AssessmentDto>();
        if (!await _settings.BoolSettingAsync("Risk:ReassessOnFieldChange", true, ct: ct)) return results;
        var activeSources = await (from f in _db.Set<RiskFactorDefinition>()
                                   join m in _db.Set<RiskMethodology>() on f.MethodologyId equals m.Id
                                   where m.Status == ConfigStatus.Approved && f.IsActive && f.DataSource.StartsWith("field:")
                                   select f.DataSource).Distinct().ToListAsync(ct);
        var relevant = changes.Where(ch => activeSources.Contains("field:" + ch.Code)).ToList();
        if (relevant.Count == 0) return results;
        var key = $"fieldchange:{string.Join(",", relevant.Select(r => r.HistoryId))}";
        try
        {
            results.Add(await AssessAsync(customerId, "FieldChange", key, $"Material field change: {string.Join(", ", relevant.Select(r => r.Code))}", null, ct));
        }
        catch (ConflictException ex)
        {
            _log.LogWarning("Field-change reassessment skipped for {CustomerId}: {Reason}", customerId, ex.Message);
        }
        return results;
    }

    // ---------------- simulation ----------------
    public async Task<SimulationResultDto> SimulateAsync(long methodologyId, long? customerId, string? customerType, Dictionary<string, JsonElement>? inputs, DateTime? asOf, CancellationToken ct)
    {
        var m = await LoadGraphAsync(methodologyId, ct);
        if (m.TenantId != _user.TenantId) throw new NotFoundException("Methodology", methodologyId);
        var at = asOf ?? (m.EffectiveFrom > _clock.UtcNow ? m.EffectiveFrom : _clock.UtcNow);
        await _lookupScores.InjectAsync(m, at, ct);
        Dictionary<string, FactorInput> factorInputs;
        string type;
        if (customerId is { } cid)
        {
            var c = await _db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == cid, ct)
                    ?? throw new ValidationException("customerId", $"Customer {cid} does not exist.");
            type = c.CustomerType;
            factorInputs = await _sources.GatherAsync(c, m.Factors.Select(f => f.DataSource).Concat(m.Overrides.Select(o => o.ConditionDataSource)), at, ct);
        }
        else
        {
            type = customerType ?? throw new ValidationException("customerType", "Provide a customerId or a customerType with sample inputs.");
            if (!CustomerTypes.All.Contains(type)) throw new ValidationException("customerType", $"Unknown customer type '{type}'.");
            factorInputs = new Dictionary<string, FactorInput>();
            foreach (var (k, v) in inputs ?? new())
            {
                factorInputs[k] = v.ValueKind switch
                {
                    JsonValueKind.Null => FactorInput.Missing(),
                    JsonValueKind.Number => FactorInput.Of(v.GetDecimal()),
                    JsonValueKind.True or JsonValueKind.False => FactorInput.Of(v.GetBoolean()),
                    JsonValueKind.Array => FactorInput.Of(v.EnumerateArray().Select(x => x.ToString()).ToList()),
                    JsonValueKind.Object when v.TryGetProperty("status", out var st) => new FactorInput
                    {
                        Status = st.GetString() ?? "Present",
                        Value = v.TryGetProperty("value", out var vv) ? (vv.ValueKind == JsonValueKind.Number ? vv.GetDecimal() : vv.ToString()) : null,
                        ObservedAt = v.TryGetProperty("observedAt", out var oa) ? oa.GetDateTime() : null
                    },
                    _ => FactorInput.Of(v.ToString())
                };
            }
        }
        RiskEngineResult r;
        try { r = _engine.Evaluate(m, type, factorInputs, at); }
        catch (InvalidOperationException ex) { throw new ConflictException("Simulation not possible: " + ex.Message); }
        return new SimulationResultDto(m.Code, m.Version, m.AggregationMethod, type, r.CalculatedScore, r.CalculatedLevel, r.FinalLevel, r.MandatoryOverrideLevel,
            r.AppliedOverrides, r.AppliedFloors, r.SanctionsWorkflow, r.RequiresApproval, r.HasMissingData, r.Warnings, r.CategoryScores,
            r.Factors.Select(ToDto).ToList(), m.Thresholds.OrderBy(t => t.MinScore).Select(t => (object)new { t.Level, t.MinScore, t.MaxScore, t.ReviewMonths, t.IsSample }).ToList(),
            r.Explanation, false);
    }
}
