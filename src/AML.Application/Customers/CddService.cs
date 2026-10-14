using System.Globalization;
using AML.Application.Common;
using AML.Application.Risk;
using AML.Application.Screening;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AML.Application.Customers;

public sealed record AcceptanceDecisionDto(long Id, long CustomerId, string Decision, string PolicyCode, int PolicyVersion, string DecidedBy, string? ApprovedBy,
    DateTime? ApprovedAt, string? Reason, string? ReasonCodes, string? Conditions, DateOnly? ConditionsDueDate, bool IsAutomatic, bool SanctionsWorkflow, DateTime DecidedAt, bool IsCurrent);

public sealed class ManualDecisionRequest
{
    public string Decision { get; set; } = "";
    public string? Reason { get; set; }
    public string? Conditions { get; set; }
    public DateOnly? ConditionsDueDate { get; set; }
}

public sealed record OnboardingResult(AcceptanceDecisionDto Decision, string? DueDiligenceLevel, object? Assessment, IReadOnlyList<object> Screening, IReadOnlyList<string> KycGaps);

public sealed class CddService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly CustomerService _customers;
    private readonly RiskAssessmentService _risk;
    private readonly ScreeningService _screening;
    private readonly ICryptoService _crypto;
    private readonly ILogger<CddService> _log;

    public CddService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, CustomerService customers, RiskAssessmentService risk,
        ScreeningService screening, ICryptoService crypto, ILogger<CddService> log)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _customers = customers; _risk = risk; _screening = screening; _crypto = crypto; _log = log;
    }

    public static AcceptanceDecisionDto ToDto(CustomerAcceptanceDecision d) => new(d.Id, d.CustomerId, d.Decision, d.PolicyCode, d.PolicyVersion, d.DecidedBy, d.ApprovedBy,
        d.ApprovedAt, d.Reason, d.ReasonCodes, d.Conditions, d.ConditionsDueDate, d.IsAutomatic, d.SanctionsWorkflow, d.DecidedAt, d.IsCurrent);

    /// <summary>Full onboarding: do-not-onboard check, screening (customer + owners), risk assessment, due-diligence level and policy decision.</summary>
    public async Task<OnboardingResult> OnboardAsync(long customerId, CancellationToken ct)
    {
        var c = await _customers.LoadVisibleAsync(customerId, ct);
        await DoNotOnboardCheckAsync(c, ct);
        var reqs = await _screening.ScreenCustomerAsync(customerId, "Onboarding", $"onboarding:{customerId}", ct);
        object? assessment = null;
        try { assessment = await _risk.AssessAsync(customerId, "Onboarding", $"onboarding:{customerId}", "Onboarding assessment", null, ct); }
        catch (ConflictException ex) { _log.LogWarning("Onboarding assessment not possible: {Reason}", ex.Message); }
        var level = await AssignDueDiligenceLevelAsync(customerId, ct);
        var decision = await DecideAsync(customerId, ct);
        c = await _customers.LoadVisibleAsync(customerId, ct);
        return new OnboardingResult(decision, level, assessment,
            reqs.Select(r => (object)new { r.Id, r.SubjectType, r.SubjectId, r.Status, r.ResultCount, r.LastError }).ToList(), await _customers.KycGapsAsync(c, ct));
    }

    public async Task DoNotOnboardCheckAsync(Customer c, CancellationToken ct)
    {
        var tokens = await _db.Set<CustomerIdentification>().AsNoTracking().Where(i => i.CustomerId == c.Id).Select(i => i.IdNumberToken).ToListAsync(ct);
        var hits = await _db.Set<DoNotOnboardEntry>().AsNoTracking().Where(d => d.IsActive && d.OriginalCustomerId != c.Id &&
            ((d.IdNumberToken != null && tokens.Contains(d.IdNumberToken)) || (d.NameSearchKey != null && d.NameSearchKey == c.NameSearchKey && d.DateOfBirth == c.DateOfBirth))).ToListAsync(ct);
        foreach (var h in hits)
        {
            var key = $"dno:{c.Id}:{h.Id}";
            if (await _db.Set<ScreeningRequest>().AnyAsync(r => r.IdempotencyKey == key, ct)) continue;
            var req = new ScreeningRequest
            {
                TenantId = c.TenantId, IdempotencyKey = key, SubjectType = "Customer", SubjectId = c.Id.ToString(CultureInfo.InvariantCulture), CustomerId = c.Id,
                SubjectName = c.NameLatin ?? c.NameArabic ?? "", ProviderCode = "INTERNAL-DNO", Trigger = "Onboarding", Status = "Completed", Attempts = 1, ResultCount = 1,
                CreatedAt = _clock.UtcNow, CompletedAt = _clock.UtcNow
            };
            _db.Set<ScreeningRequest>().Add(req);
            await _db.SaveChangesAsync(ct);
            _db.Set<ScreeningResult>().Add(new ScreeningResult
            {
                TenantId = c.TenantId, RequestId = req.Id, SubjectType = "Customer", SubjectId = req.SubjectId, CustomerId = c.Id, SubjectName = req.SubjectName,
                ProviderCode = "INTERNAL-DNO", SourceCode = "INTERNAL-DNO", ListEntryId = $"DNO-{h.Id}", MatchScore = 1m,
                MatchMethod = h.IdNumberToken != null && tokens.Contains(h.IdNumberToken) ? "identity-token" : "name+dob", Category = "Internal",
                Explanation = $"Matches internal do-not-onboard entry (reason code {h.ReasonCode}); onboarding blocked pending review.",
                Status = ScreeningStatuses.PotentialMatch, ScreenedAt = _clock.UtcNow
            });
            _audit.Record("DNO_MATCH", "Customer", c.Id.ToString(CultureInfo.InvariantCulture), null, new { entry = h.Id, h.ReasonCode }, restricted: h.LinkedCaseId is not null);
            await _db.SaveChangesAsync(ct);
        }
        if (hits.Count > 0) await _screening.RefreshCustomerStatusAsync(c.Id, ct);
    }

    public async Task<string?> AssignDueDiligenceLevelAsync(long customerId, CancellationToken ct)
    {
        var c = await _db.Set<Customer>().FirstAsync(x => x.Id == customerId, ct);
        var profile = await _db.Set<CurrentCustomerRiskProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.CustomerId == customerId, ct);
        var rules = await _db.Set<DueDiligenceSelectionRule>().AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Priority).ToListAsync(ct);
        string? level = null;
        string? basis = null;
        var highRiskCountries = await _db.Set<LookupValue>().AsNoTracking().Where(l => l.Category == "COUNTRY" && l.Attribute == "HighRisk").Select(l => l.Code).ToListAsync(ct);
        foreach (var r in rules)
        {
            var hit = r.ConditionType switch
            {
                "Pep" => c.IsPep,
                "RiskLevelIn" => profile is not null && RiskScoringEngine.Split(r.ConditionValue).Contains(profile.Level),
                "HighRiskJurisdiction" => new[] { c.NationalityCountry, c.ResidenceCountry, c.IncorporationCountry }.Any(x => x != null && highRiskCountries.Contains(x)),
                "ComplexOwnership" => c.CustomerType != CustomerTypes.Individual && (await _customers.AnalyzeOwnershipAsync(c, ct)).Analysis.MaxDepth >= int.Parse(r.ConditionValue ?? "2"),
                "FieldEquals" => await FieldEqualsAsync(c.Id, r.ConditionValue, ct) && profile?.Level == RiskLevels.Low,
                "Default" => true,
                _ => false
            };
            if (!hit) continue;
            level = r.LevelCode;
            basis = $"{r.ConditionType} {r.ConditionValue}".Trim();
            break;
        }
        if (level is null) return c.DueDiligenceLevel;
        var old = c.DueDiligenceLevel;
        c.DueDiligenceLevel = level;
        if (old != level) _audit.Record("DD_LEVEL_ASSIGNED", "Customer", c.Id.ToString(CultureInfo.InvariantCulture), new { Level = old }, new { Level = level, basis });
        await _db.SaveChangesAsync(ct);
        if (level == "EDD") await EnsureEddTasksAsync(c, ct);
        return level;
    }

    private async Task<bool> FieldEqualsAsync(long customerId, string? spec, CancellationToken ct)
    {
        var parts = (spec ?? "").Split('=', 2);
        if (parts.Length != 2) return false;
        return await _db.Set<CustomerFieldValue>().AnyAsync(v => v.CustomerId == customerId && v.FieldCode == parts[0] && v.EffectiveTo == null && v.ValueText == parts[1], ct);
    }

    private async Task EnsureEddTasksAsync(Customer c, CancellationToken ct)
    {
        if (await _db.Set<EddTask>().AnyAsync(t => t.CustomerId == c.Id && t.Status != "Completed", ct)) return;
        var def = await _db.Set<DueDiligenceLevelDefinition>().AsNoTracking().FirstOrDefaultAsync(d => d.Code == "EDD", ct);
        var tasks = new List<(string Type, string Desc)>();
        foreach (var d in RiskScoringEngine.Split(def?.RequiredDocuments)) tasks.Add(("Document", $"Obtain and verify document: {d}"));
        if (def?.RequiresSourceOfFunds ?? true) tasks.Add(("SourceOfFunds", "Establish and evidence source of funds"));
        if (def?.RequiresSourceOfWealth ?? true) tasks.Add(("SourceOfWealth", "Establish and evidence source of wealth"));
        tasks.Add(("SeniorManagementApproval", "Senior management approval of the relationship"));
        foreach (var (type, desc) in tasks)
            _db.Set<EddTask>().Add(new EddTask { TenantId = c.TenantId, CustomerId = c.Id, TaskType = type, Description = desc, CreatedAt = _clock.UtcNow });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<EddTask>> EddTasksAsync(long customerId, CancellationToken ct)
    {
        await _customers.LoadVisibleAsync(customerId, ct);
        return await _db.Set<EddTask>().AsNoTracking().Where(t => t.CustomerId == customerId).OrderBy(t => t.Id).ToListAsync(ct);
    }

    public async Task<EddTask> CompleteEddTaskAsync(long taskId, string? evidence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evidence)) throw new ValidationException("evidence", "Evidence reference is required to sign off an EDD task.");
        var t = await _db.Set<EddTask>().FirstOrDefaultAsync(x => x.Id == taskId, ct) ?? throw new NotFoundException("EDD task", taskId);
        await _customers.LoadVisibleAsync(t.CustomerId, ct);
        if (t.TaskType == "SeniorManagementApproval") Guard.Require(_user, Perm.CddSeniorApprove);
        t.Status = "Completed";
        t.EvidenceReference = evidence;
        t.SignedOffBy = _user.Username;
        t.SignedOffAt = _clock.UtcNow;
        _audit.Record("EDD_TASK_SIGNOFF", "Customer", t.CustomerId.ToString(CultureInfo.InvariantCulture), null, new { taskId, t.TaskType, evidence });
        await _db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>Policy-driven decision. Pending/unavailable screening or a confirmed sanctions match can never be auto-accepted.</summary>
    public async Task<AcceptanceDecisionDto> DecideAsync(long customerId, CancellationToken ct)
    {
        var c = await _db.Set<Customer>().FirstAsync(x => x.Id == customerId, ct);
        var now = _clock.UtcNow;
        var policy = await _db.Set<AcceptancePolicy>().AsNoTracking().Where(p => p.Status == ConfigStatus.Active && p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now))
            .OrderByDescending(p => p.Version).FirstOrDefaultAsync(ct) ?? throw new ConflictException("No active acceptance policy is configured.");
        var profile = await _db.Set<CurrentCustomerRiskProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.CustomerId == customerId, ct);
        var gaps = await _customers.KycGapsAsync(c, ct);
        string decision; var reasons = new List<string>(); string? conditions = null; var sanctions = false;
        if (c.ScreeningStatus == "ConfirmedMatch" || profile?.SanctionsWorkflow == true)
        {
            decision = "Rejected"; sanctions = true; reasons.Add("CONFIRMED_SANCTIONS_MATCH");
        }
        else if (c.ScreeningStatus is "Pending" or "Unavailable" or "NotScreened")
        {
            decision = "PendingInformation"; reasons.Add("SCREENING_" + c.ScreeningStatus.ToUpperInvariant());
        }
        else if (c.ScreeningStatus == "PotentialMatch")
        {
            decision = "PendingInformation"; reasons.Add("SCREENING_MATCH_REVIEW");
        }
        else if (profile is null)
        {
            decision = "PendingInformation"; reasons.Add("RISK_NOT_ASSESSED");
        }
        else if (policy.PepRequiresSeniorApproval && c.IsPep)
        {
            // Senior management approval is required for PEPs; outstanding EDD/KYC items are listed with the decision.
            decision = "PendingSeniorApproval"; reasons.Add("PEP_SENIOR_APPROVAL");
            if (gaps.Count > 0) reasons.Add("EDD_ITEMS_OUTSTANDING");
        }
        else if (RiskScoringEngine.Split(policy.SeniorApprovalRiskLevels).Contains(profile.Level))
        {
            decision = "PendingSeniorApproval"; reasons.Add("RISK_" + profile.Level.ToUpperInvariant());
            if (gaps.Count > 0) reasons.Add("EDD_ITEMS_OUTSTANDING");
        }
        else if (policy.RequireCompleteKyc && gaps.Count > 0)
        {
            decision = "PendingInformation"; reasons.Add("KYC_INCOMPLETE");
        }
        else if (RiskScoringEngine.Split(policy.ConditionalRiskLevels).Contains(profile.Level))
        {
            decision = "AcceptedWithConditions"; reasons.Add("RISK_" + profile.Level.ToUpperInvariant()); conditions = "Enhanced monitoring for 6 months";
        }
        else { decision = "Accepted"; reasons.Add("POLICY_CRITERIA_MET"); }

        var d = await RecordDecisionAsync(c, decision, policy, isAutomatic: true, reason: string.Join("; ", reasons.Concat(gaps)), reasons, conditions,
            conditions is null ? null : DateOnly.FromDateTime(now.AddMonths(6)), sanctions, ct);
        return ToDto(d);
    }

    private async Task<CustomerAcceptanceDecision> RecordDecisionAsync(Customer c, string decision, AcceptancePolicy policy, bool isAutomatic, string? reason, IEnumerable<string> codes,
        string? conditions, DateOnly? due, bool sanctions, CancellationToken ct)
    {
        foreach (var prev in await _db.Set<CustomerAcceptanceDecision>().Where(x => x.CustomerId == c.Id && x.IsCurrent).ToListAsync(ct)) prev.IsCurrent = false;
        var d = new CustomerAcceptanceDecision
        {
            TenantId = c.TenantId, CustomerId = c.Id, Decision = decision, PolicyCode = policy.Code, PolicyVersion = policy.Version,
            DecidedBy = isAutomatic ? $"policy:{_user.Username}" : _user.Username, Reason = reason, ReasonCodes = string.Join(",", codes), Conditions = conditions,
            ConditionsDueDate = due, IsAutomatic = isAutomatic, SanctionsWorkflow = sanctions, DecidedAt = _clock.UtcNow
        };
        _db.Set<CustomerAcceptanceDecision>().Add(d);
        if (decision is "Accepted" or "AcceptedWithConditions" && c.Status == "Prospect") c.Status = "Active";
        if (decision == "Rejected" && c.Status == "Prospect") c.Status = "Rejected";
        _audit.Record("ACCEPTANCE_DECISION", "Customer", c.Id.ToString(CultureInfo.InvariantCulture), null, new { decision, policy = $"{policy.Code} v{policy.Version}", codes, conditions, due }, reason);
        await _db.SaveChangesAsync(ct);
        return d;
    }

    public async Task<AcceptanceDecisionDto> ManualDecisionAsync(long customerId, ManualDecisionRequest r, CancellationToken ct)
    {
        var allowed = new[] { "Accepted", "AcceptedWithConditions", "PendingInformation", "PendingSeniorApproval", "Rejected" };
        if (!allowed.Contains(r.Decision)) throw new ValidationException("decision", $"Allowed decisions: {string.Join(", ", allowed)}.");
        if (r.Decision == "Rejected" && string.IsNullOrWhiteSpace(r.Reason)) throw new ValidationException("reason", "A rejection reason is required.");
        if (r.Decision == "AcceptedWithConditions" && (string.IsNullOrWhiteSpace(r.Conditions) || r.ConditionsDueDate is null))
            throw new ValidationException("conditions", "Conditions and a due date are required when accepting with conditions.");
        var c = await _customers.LoadVisibleAsync(customerId, ct, tracking: true);
        var current = await _db.Set<CustomerAcceptanceDecision>().AsNoTracking().Where(x => x.CustomerId == customerId && x.IsCurrent).FirstOrDefaultAsync(ct);
        if (r.Decision is "Accepted" or "AcceptedWithConditions")
        {
            if (c.ScreeningStatus is "ConfirmedMatch") throw new ConflictException("Customer with a confirmed sanctions match cannot be accepted; the sanctions workflow applies.");
            if (c.ScreeningStatus is "Pending" or "Unavailable" or "NotScreened" or "PotentialMatch")
                throw new ConflictException($"Customer cannot be accepted while screening is {c.ScreeningStatus}.");
            if (current?.Decision == "PendingSeniorApproval")
                throw new ConflictException("Decision pending senior approval; use the senior approval action.");
        }
        var policy = await _db.Set<AcceptancePolicy>().AsNoTracking().Where(p => p.Status == ConfigStatus.Active).OrderByDescending(p => p.Version).FirstAsync(ct);
        return ToDto(await RecordDecisionAsync(c, r.Decision, policy, false, r.Reason, new[] { "MANUAL" }, r.Conditions, r.ConditionsDueDate, false, ct));
    }

    public async Task<AcceptanceDecisionDto> SeniorApproveAsync(long customerId, bool approve, string? reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CddSeniorApprove);
        var c = await _customers.LoadVisibleAsync(customerId, ct, tracking: true);
        var current = await _db.Set<CustomerAcceptanceDecision>().FirstOrDefaultAsync(x => x.CustomerId == customerId && x.IsCurrent, ct) ?? throw new ConflictException("No decision recorded.");
        if (current.Decision != "PendingSeniorApproval") throw new ConflictException("Decision is not pending senior approval.");
        Guard.FourEyes(c.CreatedBy ?? "", _user.Username, "Senior onboarding approval");
        if (!approve && string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A rejection reason is required.");
        var policy = await _db.Set<AcceptancePolicy>().AsNoTracking().Where(p => p.Status == ConfigStatus.Active).OrderByDescending(p => p.Version).FirstAsync(ct);
        var d = await RecordDecisionAsync(c, approve ? "Accepted" : "Rejected", policy, false, reason ?? "Senior management approval", new[] { "SENIOR_DECISION" }, null, null, false, ct);
        d.ApprovedBy = _user.Username;
        d.ApprovedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(d);
    }

    public async Task<List<AcceptanceDecisionDto>> DecisionsAsync(long customerId, CancellationToken ct)
    {
        await _customers.LoadVisibleAsync(customerId, ct);
        return (await _db.Set<CustomerAcceptanceDecision>().AsNoTracking().Where(d => d.CustomerId == customerId).OrderByDescending(d => d.Id).ToListAsync(ct)).Select(ToDto).ToList();
    }

    /// <summary>Creates a KYC-refresh task for each customer holding an expired identification or document (once per customer while open).</summary>
    public async Task<int> CreateKycRefreshTasksAsync(long tenantId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow);
        var expiredIds = await _db.Set<CustomerIdentification>().IgnoreQueryFilters().Where(i => i.TenantId == tenantId && i.ExpiryDate != null && i.ExpiryDate < today).Select(i => i.CustomerId).ToListAsync(ct);
        var expiredDocs = await _db.Set<CustomerDocument>().IgnoreQueryFilters().Where(d => d.TenantId == tenantId && d.ExpiryDate != null && d.ExpiryDate < today).Select(d => d.CustomerId).ToListAsync(ct);
        var created = 0;
        foreach (var cid in expiredIds.Concat(expiredDocs).Distinct())
        {
            if (await _db.Set<EddTask>().IgnoreQueryFilters().AnyAsync(t => t.CustomerId == cid && t.TaskType == "KycRefresh" && t.Status != "Completed", ct)) continue;
            _db.Set<EddTask>().Add(new EddTask { TenantId = tenantId, CustomerId = cid, LevelCode = "KYC", TaskType = "KycRefresh", Description = "Expired identification/document: obtain updated KYC evidence", CreatedAt = _clock.UtcNow });
            var c = await _db.Set<Customer>().IgnoreQueryFilters().FirstAsync(x => x.Id == cid, ct);
            c.KycStatus = "Expired";
            _audit.Record("KYC_REFRESH_TASK", "Customer", cid.ToString(CultureInfo.InvariantCulture), null, new { reason = "expired document" });
            created++;
        }
        await _db.SaveChangesAsync(ct);
        return created;
    }

    // ---------------- relationship exit ----------------
    public async Task<object> RequestExitAsync(long customerId, string reasonCode, string? internalReason, long? linkedCaseId, string? evidence, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CustomersExit);
        if (string.IsNullOrWhiteSpace(reasonCode)) throw new ValidationException("reasonCode", "A neutral reason code is required.");
        var c = await _customers.LoadVisibleAsync(customerId, ct);
        if (c.Status == "Exited") throw new ConflictException("Customer already exited.");
        if (await _db.Set<CustomerExitRequest>().AnyAsync(e => e.CustomerId == customerId && e.Status == "PendingApproval", ct)) throw new ConflictException("An exit request is already pending.");
        var e = new CustomerExitRequest
        {
            TenantId = c.TenantId, CustomerId = customerId, ReasonCode = reasonCode, InternalReason = internalReason, LinkedCaseId = linkedCaseId, Evidence = evidence,
            RequestedBy = _user.Username, RequestedAt = _clock.UtcNow
        };
        _db.Set<CustomerExitRequest>().Add(e);
        _audit.Record("EXIT_REQUEST", "Customer", customerId.ToString(CultureInfo.InvariantCulture), null, new { reasonCode }, restricted: linkedCaseId is not null);
        await _db.SaveChangesAsync(ct);
        return ExitView(e);
    }

    public async Task<object> DecideExitAsync(long exitId, bool approve, string? note, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CustomersExitApprove);
        var e = await _db.Set<CustomerExitRequest>().FirstOrDefaultAsync(x => x.Id == exitId, ct) ?? throw new NotFoundException("Exit request", exitId);
        if (e.Status != "PendingApproval") throw new ConflictException("Exit request is not pending.");
        Guard.FourEyes(e.RequestedBy, _user.Username, "Relationship exit");
        var c = await _db.Set<Customer>().FirstAsync(x => x.Id == e.CustomerId, ct);
        e.Status = approve ? "Approved" : "Rejected";
        e.ApprovedBy = _user.Username;
        e.DecidedAt = _clock.UtcNow;
        if (approve)
        {
            c.Status = "Exited";
            c.ExitedAt = _clock.UtcNow;
            if (e.AddToDoNotOnboard)
            {
                var tokens = await _db.Set<CustomerIdentification>().AsNoTracking().Where(i => i.CustomerId == c.Id).Select(i => i.IdNumberToken).ToListAsync(ct);
                foreach (var t in tokens.DefaultIfEmpty(null))
                    _db.Set<DoNotOnboardEntry>().Add(new DoNotOnboardEntry
                    {
                        TenantId = c.TenantId, IdNumberToken = t, NameSearchKey = c.NameSearchKey, DateOfBirth = c.DateOfBirth, OriginalCustomerId = c.Id,
                        LinkedCaseId = e.LinkedCaseId, ReasonCode = e.ReasonCode, CreatedAt = _clock.UtcNow
                    });
            }
        }
        _audit.Record(approve ? "EXIT_APPROVE" : "EXIT_REJECT", "Customer", e.CustomerId.ToString(CultureInfo.InvariantCulture), null, new { e.ReasonCode, e.Status }, note, restricted: e.LinkedCaseId is not null);
        await _db.SaveChangesAsync(ct);
        return ExitView(e);
    }

    /// <summary>Exit records expose only neutral reason codes to users without restricted access (no suspicion or case link).</summary>
    public object ExitView(CustomerExitRequest e)
    {
        var full = _user.Has(Perm.CasesRestricted) || _user.Has(Perm.CustomersExitApprove);
        return new
        {
            e.Id, e.CustomerId, e.ReasonCode, e.Status, e.RequestedBy, e.ApprovedBy, e.RequestedAt, e.DecidedAt, e.AddToDoNotOnboard,
            internalReason = full ? e.InternalReason : null, linkedCaseId = full ? e.LinkedCaseId : null, evidence = full ? e.Evidence : null
        };
    }

    public async Task<List<object>> ExitsAsync(long customerId, CancellationToken ct)
    {
        await _customers.LoadVisibleAsync(customerId, ct);
        return (await _db.Set<CustomerExitRequest>().AsNoTracking().Where(e => e.CustomerId == customerId).OrderByDescending(e => e.Id).ToListAsync(ct)).Select(ExitView).ToList();
    }
}
