using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Alerts;

public sealed class AlertListItem
{
    public long Id { get; init; }
    public string AlertNumber { get; init; }
    public string Source { get; init; }
    public string AlertType { get; init; }
    public string? RuleCode { get; init; }
    public int? RuleVersion { get; init; }
    public long? PrimaryCustomerId { get; init; }
    public string Severity { get; init; }
    public string Status { get; init; }
    public string Title { get; init; }
    public string? AssignedTo { get; init; }
    public string? AssignedTeam { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? DueAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public bool IsOverdue { get; init; }
    public long? CaseId { get; init; }
    public bool IsBacktest { get; init; }
    public string? ResolutionCode { get; init; }
}

public sealed class AlertQuery
{
    public string? Status { get; set; }
    public string? Severity { get; set; }
    public string? RuleCode { get; set; }
    public string? AssignedTo { get; set; }
    public string? Source { get; set; }
    public long? CustomerId { get; set; }
    public bool? Overdue { get; set; }
    public bool? Open { get; set; }
    public bool IncludeBacktest { get; set; }
    public string? Team { get; set; }
}

public sealed class DispositionRequest
{
    public string ResolutionCode { get; set; } = "";
    public string? Notes { get; set; }
    public Dictionary<string, bool>? Checklist { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class AlertService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly AccessPolicy _access;
    private readonly TenantSettingsService _settings;

    public AlertService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, AccessPolicy access, TenantSettingsService settings)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _access = access; _settings = settings;
    }

    private static readonly Dictionary<string, Expression<Func<AlertListItem, object?>>> Sorts = new()
    {
        ["createdAt"] = a => a.CreatedAt, ["severity"] = a => a.Severity, ["status"] = a => a.Status, ["dueAt"] = a => a.DueAt, ["id"] = a => a.Id, ["rule"] = a => a.RuleCode
    };

    public async Task<PagedResult<AlertListItem>> ListAsync(AlertQuery f, PageRequest page, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var q = _access.Alerts().AsNoTracking();
        if (!f.IncludeBacktest) q = q.Where(a => !a.IsBacktest);
        if (!string.IsNullOrWhiteSpace(f.Status)) q = q.Where(a => a.Status == f.Status);
        if (!string.IsNullOrWhiteSpace(f.Severity)) q = q.Where(a => a.Severity == f.Severity);
        if (!string.IsNullOrWhiteSpace(f.RuleCode)) q = q.Where(a => a.RuleCode == f.RuleCode);
        if (!string.IsNullOrWhiteSpace(f.AssignedTo)) q = q.Where(a => a.AssignedTo == f.AssignedTo);
        if (!string.IsNullOrWhiteSpace(f.Team)) q = q.Where(a => a.AssignedTeam == f.Team);
        if (!string.IsNullOrWhiteSpace(f.Source)) q = q.Where(a => a.Source == f.Source);
        if (f.CustomerId is { } cid)
        {
            var linked = _db.Set<AmlAlertCustomer>().Where(x => x.CustomerId == cid).Select(x => x.AlertId);
            q = q.Where(a => a.PrimaryCustomerId == cid || linked.Contains(a.Id));
        }
        var closed = AlertStatuses.Closed;
        if (f.Open == true) q = q.Where(a => !closed.Contains(a.Status) && a.Status != AlertStatuses.ConvertedToCase);
        if (f.Overdue == true) q = q.Where(a => !closed.Contains(a.Status) && a.DueAt < now);
        var rows = q.Select(a => new AlertListItem { Id = a.Id, AlertNumber = a.AlertNumber, Source = a.Source, AlertType = a.AlertType, RuleCode = a.RuleCode, RuleVersion = a.RuleVersion, PrimaryCustomerId = a.PrimaryCustomerId, Severity = a.Severity, Status = a.Status, Title = a.Title, AssignedTo = a.AssignedTo, AssignedTeam = a.AssignedTeam, CreatedAt = a.CreatedAt, DueAt = a.DueAt, ClosedAt = a.ClosedAt, IsOverdue = !closed.Contains(a.Status) && a.DueAt < now, CaseId = a.CaseId, IsBacktest = a.IsBacktest, ResolutionCode = a.ResolutionCode });
        return await rows.SortBy(page, Sorts, "createdAt", defaultDesc: true).ToPagedAsync(page, ct);
    }

    public async Task<AmlAlert> LoadVisibleAsync(long id, CancellationToken ct, bool tracking = false)
    {
        var q = _access.Alerts();
        if (!tracking) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Alert", id);
    }

    public async Task<object> GetAsync(long id, CancellationToken ct)
    {
        var a = await LoadVisibleAsync(id, ct);
        if (Confidentiality.IsRestricted(a.Confidentiality))
        {
            _audit.Record("RESTRICTED_READ", "AmlAlert", id.ToString(CultureInfo.InvariantCulture), null, null, restricted: true);
            await _db.SaveChangesAsync(ct);
        }
        var matches = await _db.Set<AmlAlertRuleMatch>().AsNoTracking().Where(m => m.AlertId == id).OrderBy(m => m.Id).ToListAsync(ct);
        var txIds = await _db.Set<AmlAlertTransaction>().AsNoTracking().Where(x => x.AlertId == id).Select(x => x.TransactionId).ToListAsync(ct);
        var txns = await _db.Set<Transaction>().AsNoTracking().Where(t => txIds.Contains(t.Id)).Select(t => new
        {
            t.Id, t.SourceTransactionId, t.TransactionType, t.Status, t.Amount, t.Currency, t.BaseAmount, t.BaseCurrency, t.FxRate, t.FxRateDate, t.TimestampUtc,
            t.SenderCustomerId, t.ReceiverCustomerId
        }).ToListAsync(ct);
        var custIds = await _db.Set<AmlAlertCustomer>().AsNoTracking().Where(x => x.AlertId == id).Select(x => new { x.CustomerId, x.Role }).ToListAsync(ct);
        var history = await _db.Set<AlertStatusHistory>().AsNoTracking().Where(h => h.AlertId == id).OrderBy(h => h.Id).ToListAsync(ct);
        var caseVisible = a.CaseId is { } caseId && await _access.Cases().AnyAsync(c => c.Id == caseId, ct);
        return new
        {
            alert = new
            {
                a.Id, a.AlertNumber, a.Source, a.AlertType, a.RuleId, a.RuleVersionId, a.RuleVersion, a.RuleCode, a.ExecutionId, a.PrimaryCustomerId, a.SubjectKey, a.Severity, a.Status,
                a.Score, a.Title, a.Description, a.AssignedTo, a.AssignedTeam, a.CreatedAt, a.DueAt, a.ClosedAt, a.ResolutionCode, a.ResolutionNotes, a.PendingClosureStatus,
                a.ClosureRequestedBy, a.ClosureApprovedBy, a.EscalationReason, a.DeduplicationKey, a.Confidentiality, CaseId = caseVisible ? a.CaseId : null, a.IsBacktest,
                a.IsTestData, a.ReopenCount, checklist = a.ChecklistJson is null ? (JsonElement?)null : JsonDocument.Parse(a.ChecklistJson).RootElement,
                evidence = JsonDocument.Parse(a.EvidenceJson).RootElement, rowVersion = Convert.ToBase64String(a.RowVersion),
                isOverdue = !AlertStatuses.IsClosed(a.Status) && a.DueAt < _clock.UtcNow
            },
            matches = matches.Select(m => new
            {
                m.Id, m.ExecutionId, m.RuleVersion, m.MatchKey, m.WindowStart, m.WindowEnd, m.MatchedAt,
                matchedConditions = JsonDocument.Parse(m.MatchedConditionsJson).RootElement, evidence = JsonDocument.Parse(m.EvidenceJson).RootElement
            }),
            transactions = txns, customers = custIds, history
        };
    }

    private void Concurrency(AmlAlert a, string? rowVersion)
    {
        if (rowVersion is not null) _db.Entry(a).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(rowVersion, "alert");
    }

    private void Transition(AmlAlert a, string to, string? reason, string? code = null, string? notes = null)
    {
        _db.Set<AlertStatusHistory>().Add(new AlertStatusHistory
        {
            TenantId = a.TenantId, AlertId = a.Id, FromStatus = a.Status, ToStatus = to, Actor = _user.Username, At = _clock.UtcNow, Reason = reason, ResolutionCode = code, Notes = notes
        });
        _audit.Record("ALERT_" + to.ToUpperInvariant().Replace("-", "_"), "AmlAlert", a.Id.ToString(CultureInfo.InvariantCulture), new { a.Status }, new { Status = to, code }, reason ?? notes,
            restricted: Confidentiality.IsRestricted(a.Confidentiality));
        a.Status = to;
    }

    public async Task<object> AssignAsync(long id, string assignee, string? team, string? rowVersion, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AlertsWork);
        if (string.IsNullOrWhiteSpace(assignee)) throw new ValidationException("assignee", "Assignee is required.");
        var a = await LoadVisibleAsync(id, ct, tracking: true);
        Concurrency(a, rowVersion);
        if (AlertStatuses.IsClosed(a.Status) || a.Status == AlertStatuses.ConvertedToCase) throw new ConflictException($"Alert is {a.Status}.");
        var user = await _db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Username == assignee && u.IsActive, ct) ?? throw new ValidationException("assignee", $"Unknown user '{assignee}'.");
        a.AssignedTo = user.Username;
        a.AssignedTeam = team ?? user.Team;
        Transition(a, AlertStatuses.Assigned, $"Assigned to {assignee}");
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<object> StartReviewAsync(long id, string? rowVersion, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AlertsWork);
        var a = await LoadVisibleAsync(id, ct, tracking: true);
        Concurrency(a, rowVersion);
        if (a.Status is not (AlertStatuses.New or AlertStatuses.Assigned or AlertStatuses.PendingInformation or AlertStatuses.Escalated))
            throw new ConflictException($"Cannot start review from {a.Status}.");
        a.AssignedTo ??= _user.Username;
        Transition(a, AlertStatuses.UnderReview, null);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<object> EscalateAsync(long id, string? reason, string? rowVersion, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AlertsWork);
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "An escalation reason is required.");
        var a = await LoadVisibleAsync(id, ct, tracking: true);
        Concurrency(a, rowVersion);
        if (AlertStatuses.IsClosed(a.Status) || a.Status == AlertStatuses.ConvertedToCase) throw new ConflictException($"Alert is {a.Status}.");
        a.EscalationReason = reason;
        a.AssignedTeam = await _settings.SettingAsync("Alerts:EscalationTeam", "SeniorReview", ct: ct);
        Transition(a, AlertStatuses.Escalated, reason);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Closure with documented disposition; configured policies require a second reviewer (four-eyes).</summary>
    public async Task<object> DispositionAsync(long id, DispositionRequest r, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AlertsWork);
        var a = await LoadVisibleAsync(id, ct, tracking: true);
        Guard.RowVersion(r.RowVersion, "alert");
        Concurrency(a, r.RowVersion);
        if (!AlertStatuses.Closed.Contains(r.ResolutionCode)) throw new ValidationException("resolutionCode", $"Allowed resolution codes: {string.Join(", ", AlertStatuses.Closed)}.");
        if (AlertStatuses.IsClosed(a.Status) || a.Status is AlertStatuses.ConvertedToCase or AlertStatuses.PendingClosureApproval)
            throw new ConflictException($"Alert is {a.Status}; it cannot be closed again.");
        var policies = await ApplicablePoliciesAsync(a, ct);
        var minNarrative = policies.Count == 0 ? 10 : policies.Max(p => p.MinNarrativeLength);
        if (string.IsNullOrWhiteSpace(r.Notes) || r.Notes.Trim().Length < minNarrative)
            throw new ValidationException("notes", $"Disposition notes (minimum {minNarrative} characters) are required.");
        var required = policies.SelectMany(p => RiskScoringEngine.Split(p.RequiredChecklistItems)).Distinct().ToList();
        var missing = required.Where(i => r.Checklist is null || !r.Checklist.TryGetValue(i, out var done) || !done).ToList();
        if (missing.Count > 0) throw new ValidationException("checklist", $"Disposition checklist incomplete: {string.Join(", ", missing)}.");
        a.ResolutionNotes = r.Notes;
        a.ChecklistJson = r.Checklist is null ? null : JsonSerializer.Serialize(r.Checklist);
        if (policies.Any(p => p.RequiresSecondApproval))
        {
            a.PendingClosureStatus = r.ResolutionCode;
            a.ClosureRequestedBy = _user.Username;
            a.ResolutionCode = r.ResolutionCode;
            Transition(a, AlertStatuses.PendingClosureApproval, $"Closure as {r.ResolutionCode} requires second review ({string.Join(", ", policies.Where(p => p.RequiresSecondApproval).Select(p => p.Code))})", r.ResolutionCode, r.Notes);
        }
        else
        {
            a.ResolutionCode = r.ResolutionCode;
            a.ClosedAt = _clock.UtcNow;
            Transition(a, r.ResolutionCode, null, r.ResolutionCode, r.Notes);
        }
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task<List<AlertClosurePolicy>> ApplicablePoliciesAsync(AmlAlert a, CancellationToken ct)
    {
        var all = await _db.Set<AlertClosurePolicy>().AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        var level = a.PrimaryCustomerId is { } cid ? await _db.Set<CurrentCustomerRiskProfile>().Where(p => p.CustomerId == cid).Select(p => p.Level).FirstOrDefaultAsync(ct) : null;
        return all.Where(p => p.ConditionType switch
        {
            "Any" => true,
            "Severity" => RiskScoringEngine.Split(p.ConditionValues).Contains(a.Severity),
            "CustomerRiskLevel" => level is not null && RiskScoringEngine.Split(p.ConditionValues).Contains(level),
            "RuleCode" => a.RuleCode is not null && RiskScoringEngine.Split(p.ConditionValues).Contains(a.RuleCode),
            "Source" => RiskScoringEngine.Split(p.ConditionValues).Contains(a.Source),
            _ => false
        }).ToList();
    }

    public async Task<object> ApproveClosureAsync(long id, bool approve, string? comment, string? rowVersion, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AlertsApprove);
        var a = await LoadVisibleAsync(id, ct, tracking: true);
        Concurrency(a, rowVersion);
        if (a.Status != AlertStatuses.PendingClosureApproval) throw new ConflictException("Alert closure is not pending approval.");
        Guard.FourEyes(a.ClosureRequestedBy ?? "", _user.Username, "Alert closure");
        if (approve)
        {
            a.ClosureApprovedBy = _user.Username;
            a.ClosedAt = _clock.UtcNow;
            Transition(a, a.PendingClosureStatus!, comment, a.PendingClosureStatus, a.ResolutionNotes);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(comment)) throw new ValidationException("comment", "A reason is required to return the alert.");
            a.ResolutionCode = null;
            Transition(a, AlertStatuses.UnderReview, "Closure rejected: " + comment);
        }
        a.PendingClosureStatus = null;
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Reopens a closed alert (e.g. QA finding). The original disposition stays in the status history.</summary>
    public async Task<object> ReopenAsync(long id, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A reason is required to reopen an alert.");
        var a = await LoadVisibleAsync(id, ct, tracking: true);
        if (!AlertStatuses.IsClosed(a.Status)) throw new ConflictException("Only closed alerts can be reopened.");
        a.ReopenCount++;
        a.ClosedAt = null;
        a.ResolutionCode = null;
        Transition(a, AlertStatuses.UnderReview, "Reopened: " + reason);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<int> MarkOverdueAsync(long tenantId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var closed = AlertStatuses.Closed;
        return await _db.Set<AmlAlert>().IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId && !closed.Contains(a.Status) && a.Status != AlertStatuses.ConvertedToCase && a.DueAt < now, ct);
    }
}
