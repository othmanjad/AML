using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Cases;

public sealed class CaseCreateRequest
{
    public string Title { get; set; } = "";
    public string CaseType { get; set; } = "Investigation";
    public string Priority { get; set; } = "Medium";
    public string Confidentiality { get; set; } = Domain.Common.Confidentiality.Standard;
    public long? PrimaryCustomerId { get; set; }
    public string? AssignedInvestigator { get; set; }
    public string? Reviewer { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Summary { get; set; }
    public List<long>? AlertIds { get; set; }
    public List<string>? TeamMembers { get; set; }
}

public sealed class CaseUpdateRequest
{
    public string? Title { get; set; }
    public string? Priority { get; set; }
    public string? Summary { get; set; }
    public string? Findings { get; set; }
    public string? RecommendedAction { get; set; }
    public string? AssignedInvestigator { get; set; }
    public string? Reviewer { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Confidentiality { get; set; }
    public string RowVersion { get; set; } = "";
}

public sealed class CaseTransitionRequest
{
    public string ToStatus { get; set; } = "";
    public string? Comment { get; set; }
    public string? FinalDecision { get; set; }
    public string? ClosureReason { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class CaseListItem
{
    public long Id { get; init; }
    public string CaseNumber { get; init; }
    public string Title { get; init; }
    public string CaseType { get; init; }
    public string Priority { get; init; }
    public string Status { get; init; }
    public string Confidentiality { get; init; }
    public long? PrimaryCustomerId { get; init; }
    public string? AssignedInvestigator { get; init; }
    public DateTime? DueDate { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsOverdue { get; init; }
}

public sealed class CaseService
{
    private static readonly string[] AllowedExtensions = { ".pdf", ".png", ".jpg", ".jpeg", ".docx", ".xlsx", ".txt", ".csv", ".msg", ".eml" };
    private static readonly string[] RecommendedActions = { "NoFurtherAction", "EnhancedMonitoring", "ExitRelationship", "FileReport" };
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly AccessPolicy _access;
    private readonly IFileStore _files;
    private readonly IMalwareScanner _scanner;
    private readonly TenantSettingsService _settings;

    public CaseService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, AccessPolicy access, IFileStore files, IMalwareScanner scanner, TenantSettingsService settings)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _access = access; _files = files; _scanner = scanner; _settings = settings;
    }

    private static readonly Dictionary<string, Expression<Func<CaseListItem, object?>>> Sorts = new()
    {
        ["createdAt"] = c => c.CreatedAt, ["priority"] = c => c.Priority, ["status"] = c => c.Status, ["dueDate"] = c => c.DueDate, ["caseNumber"] = c => c.CaseNumber
    };

    public async Task<PagedResult<CaseListItem>> ListAsync(string? search, string? status, long? customerId, PageRequest page, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var q = _access.Cases().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(c => c.CaseNumber == search || c.Title.Contains(search));
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status);
        if (customerId is { } cid)
        {
            var linked = _db.Set<AmlCaseCustomer>().Where(x => x.CustomerId == cid).Select(x => x.CaseId);
            q = q.Where(c => c.PrimaryCustomerId == cid || linked.Contains(c.Id));
        }
        var rows = q.Select(c => new CaseListItem { Id = c.Id, CaseNumber = c.CaseNumber, Title = c.Title, CaseType = c.CaseType, Priority = c.Priority, Status = c.Status, Confidentiality = c.Confidentiality, PrimaryCustomerId = c.PrimaryCustomerId, AssignedInvestigator = c.AssignedInvestigator, DueDate = c.DueDate, CreatedAt = c.CreatedAt, IsOverdue = c.Status != CaseStatuses.Closed && c.DueDate < now });
        return await rows.SortBy(page, Sorts, "createdAt", defaultDesc: true).ToPagedAsync(page, ct);
    }

    /// <summary>Loads a visible case; inaccessible restricted cases behave exactly like non-existent ones (404).</summary>
    public async Task<AmlCase> LoadVisibleAsync(long id, CancellationToken ct, bool tracking = false)
    {
        var q = _access.Cases();
        if (!tracking) q = q.AsNoTracking();
        var c = await q.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Case", id);
        if (Confidentiality.IsRestricted(c.Confidentiality))
        {
            var members = await _db.Set<AmlCaseTeamMember>().AsNoTracking().Where(m => m.CaseId == id).Select(m => m.Username).ToListAsync(ct);
            var inTeam = _access.IsCaseTeamMember(c, members);
            await _audit.RecordNowAsync("RESTRICTED_READ", "AmlCase", id.ToString(CultureInfo.InvariantCulture), null, new { inTeam, c.Confidentiality }, restricted: true,
                outcome: inTeam ? "Success" : "OutsideTeam");
        }
        return c;
    }

    public async Task<object> GetAsync(long id, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(id, ct);
        var alerts = await (from ca in _db.Set<AmlCaseAlert>().AsNoTracking() join a in _db.Set<AmlAlert>().AsNoTracking() on ca.AlertId equals a.Id where ca.CaseId == id
                            select new { a.Id, a.AlertNumber, a.Title, a.Severity, a.Status, a.RuleCode, ca.LinkedAt }).ToListAsync(ct);
        var customers = await (from cc in _db.Set<AmlCaseCustomer>().AsNoTracking() join cu in _db.Set<Customer>().AsNoTracking() on cc.CustomerId equals cu.Id where cc.CaseId == id
                               select new { cu.Id, cu.NameLatin, cu.NameArabic, cu.CustomerType, cc.Role }).ToListAsync(ct);
        var txns = await (from ct2 in _db.Set<AmlCaseTransaction>().AsNoTracking() join t in _db.Set<Transaction>().AsNoTracking() on ct2.TransactionId equals t.Id where ct2.CaseId == id
                          select new { t.Id, t.SourceTransactionId, t.TransactionType, t.Amount, t.Currency, t.TimestampUtc, t.Status }).ToListAsync(ct);
        var notes = await _db.Set<AmlCaseNote>().AsNoTracking().Where(n => n.CaseId == id).OrderBy(n => n.Id).ToListAsync(ct);
        var docs = await _db.Set<AmlCaseDocument>().AsNoTracking().Where(d => d.CaseId == id).Select(d => new { d.Id, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.ScanStatus, d.UploadedBy, d.UploadedAt }).ToListAsync(ct);
        var history = await _db.Set<AmlCaseHistory>().AsNoTracking().Where(h => h.CaseId == id).OrderBy(h => h.Id).ToListAsync(ct);
        var tasks = await _db.Set<AmlCaseTask>().AsNoTracking().Where(t => t.CaseId == id).OrderBy(t => t.DueDate).ToListAsync(ct);
        var team = await _db.Set<AmlCaseTeamMember>().AsNoTracking().Where(m => m.CaseId == id).Select(m => m.Username).ToListAsync(ct);
        var reports = _user.Has(Perm.StrPrepare) || _user.Has(Perm.StrApprove)
            ? (await _access.Reports().AsNoTracking().Where(r => r.CaseId == id).Select(r => new { r.Id, r.ReportNumber, r.ReportType, r.Status }).ToListAsync(ct)).Cast<object>().ToList()
            : new List<object>();
        var now = _clock.UtcNow;
        return new
        {
            @case = new
            {
                c.Id, c.CaseNumber, c.Title, c.CaseType, c.Priority, c.Status, c.Confidentiality, c.PrimaryCustomerId, c.AssignedInvestigator, c.Reviewer, c.Team, c.DueDate,
                c.Summary, c.Findings, c.RecommendedAction, c.FinalDecision, c.ClosureReason, c.SuspicionFormedAt, c.ApprovalStatus, c.ApprovalRequestedBy, c.ApprovedBy,
                c.ApprovedAt, c.CreatedAt, c.CreatedBy, c.ClosedAt, c.IsTestData, rowVersion = Convert.ToBase64String(c.RowVersion), isOverdue = c.Status != CaseStatuses.Closed && c.DueDate < now
            },
            alerts, customers, transactions = txns, notes, documents = docs, history, tasks = tasks.Select(t => new { t.Id, t.Title, t.Assignee, t.DueDate, t.Status, t.CompletedAt, isOverdue = t.Status != "Completed" && t.DueDate < now }),
            team, reports,
            allowedTransitions = await _db.Set<CaseStatusTransition>().AsNoTracking().Where(t => t.FromStatus == c.Status).Select(t => t.ToStatus).ToListAsync(ct)
        };
    }

    public async Task<object> CreateAsync(CaseCreateRequest r, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesWork);
        if (string.IsNullOrWhiteSpace(r.Title)) throw new ValidationException("title", "Title is required.");
        if (r.Confidentiality is not (Confidentiality.Standard or Confidentiality.Restricted or Confidentiality.StrictlyConfidential))
            throw new ValidationException("confidentiality", "Allowed: Standard, Restricted, StrictlyConfidential.");
        if (r.PrimaryCustomerId is { } pc && !await _access.Customers().AnyAsync(c => c.Id == pc, ct)) throw new ValidationException("primaryCustomerId", "Unknown customer.");
        var calendar = await _settings.CalendarAsync(ct: ct);
        var c = new AmlCase
        {
            TenantId = _user.TenantId, CaseNumber = "PENDING-" + Guid.NewGuid().ToString("N"), Title = r.Title, CaseType = r.CaseType, Priority = r.Priority,
            Confidentiality = r.Confidentiality, PrimaryCustomerId = r.PrimaryCustomerId, AssignedInvestigator = r.AssignedInvestigator ?? _user.Username, Reviewer = r.Reviewer,
            Team = _user.Team, DueDate = r.DueDate ?? calendar.AddWorkingDays(_clock.UtcNow, await _settings.IntSettingAsync("Cases:DefaultSlaWorkingDays", 20, ct: ct)),
            Summary = r.Summary, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username
        };
        await using var tx = await _db.Database.BeginIfNoneAsync(ct);
        _db.Set<AmlCase>().Add(c);
        await _db.SaveChangesAsync(ct);
        c.CaseNumber = $"CASE-{_clock.UtcNow:yyyy}-{c.Id:D5}";
        _db.Set<AmlCaseHistory>().Add(new AmlCaseHistory { TenantId = c.TenantId, CaseId = c.Id, FromStatus = "", ToStatus = CaseStatuses.Open, Actor = _user.Username, At = _clock.UtcNow, Comment = "Created" });
        if (r.PrimaryCustomerId is { } p) _db.Set<AmlCaseCustomer>().Add(new AmlCaseCustomer { TenantId = c.TenantId, CaseId = c.Id, CustomerId = p, Role = "Primary", LinkedAt = _clock.UtcNow });
        foreach (var m in (r.TeamMembers ?? new()).Distinct()) _db.Set<AmlCaseTeamMember>().Add(new AmlCaseTeamMember { TenantId = c.TenantId, CaseId = c.Id, Username = m });
        _audit.Record("CASE_CREATE", "AmlCase", c.Id.ToString(CultureInfo.InvariantCulture), null, new { c.CaseNumber, c.Title, c.Confidentiality }, restricted: Confidentiality.IsRestricted(c.Confidentiality));
        await _db.SaveChangesAsync(ct);
        foreach (var a in r.AlertIds ?? new()) await LinkAlertInternalAsync(c, a, convert: true, ct);
        await tx.CommitIfOwnedAsync(ct);
        return await GetAsync(c.Id, ct);
    }

    /// <summary>Converts an alert into a new case (alert becomes ConvertedToCase).</summary>
    public async Task<object> ConvertAlertAsync(long alertId, string? title, string? confidentiality, CancellationToken ct)
    {
        var alert = await _access.Alerts().AsNoTracking().FirstOrDefaultAsync(a => a.Id == alertId, ct) ?? throw new NotFoundException("Alert", alertId);
        if (AlertStatuses.IsClosed(alert.Status) || alert.Status == AlertStatuses.ConvertedToCase) throw new ConflictException($"Alert is {alert.Status}.");
        return await CreateAsync(new CaseCreateRequest
        {
            Title = title ?? $"Investigation: {alert.Title}", PrimaryCustomerId = alert.PrimaryCustomerId, AlertIds = new() { alertId },
            Confidentiality = confidentiality ?? Confidentiality.Standard, Priority = alert.Severity is "High" or "Critical" ? "High" : "Medium"
        }, ct);
    }

    public async Task<object> LinkAlertAsync(long caseId, long alertId, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesWork);
        var c = await LoadVisibleAsync(caseId, ct, tracking: true);
        await LinkAlertInternalAsync(c, alertId, convert: false, ct);
        return await GetAsync(caseId, ct);
    }

    private async Task LinkAlertInternalAsync(AmlCase c, long alertId, bool convert, CancellationToken ct)
    {
        var a = await _access.Alerts().FirstOrDefaultAsync(x => x.Id == alertId, ct) ?? throw new NotFoundException("Alert", alertId);
        if (await _db.Set<AmlCaseAlert>().AnyAsync(x => x.CaseId == c.Id && x.AlertId == alertId, ct)) return; // idempotent
        _db.Set<AmlCaseAlert>().Add(new AmlCaseAlert { TenantId = c.TenantId, CaseId = c.Id, AlertId = alertId, LinkedAt = _clock.UtcNow, LinkedBy = _user.Username });
        a.CaseId ??= c.Id;
        if (Confidentiality.IsRestricted(c.Confidentiality)) a.Confidentiality = c.Confidentiality;
        if (convert && !AlertStatuses.IsClosed(a.Status))
        {
            _db.Set<AlertStatusHistory>().Add(new AlertStatusHistory { TenantId = a.TenantId, AlertId = a.Id, FromStatus = a.Status, ToStatus = AlertStatuses.ConvertedToCase, Actor = _user.Username, At = _clock.UtcNow, Reason = c.CaseNumber });
            a.Status = AlertStatuses.ConvertedToCase;
        }
        var txIds = await _db.Set<AmlAlertTransaction>().Where(x => x.AlertId == alertId).Select(x => x.TransactionId).ToListAsync(ct);
        var existingTx = await _db.Set<AmlCaseTransaction>().Where(x => x.CaseId == c.Id).Select(x => x.TransactionId).ToListAsync(ct);
        foreach (var t in txIds.Except(existingTx)) _db.Set<AmlCaseTransaction>().Add(new AmlCaseTransaction { TenantId = c.TenantId, CaseId = c.Id, TransactionId = t, LinkedAt = _clock.UtcNow });
        var custIds = await _db.Set<AmlAlertCustomer>().Where(x => x.AlertId == alertId).Select(x => x.CustomerId).ToListAsync(ct);
        var existingCust = await _db.Set<AmlCaseCustomer>().Where(x => x.CaseId == c.Id).Select(x => x.CustomerId).ToListAsync(ct);
        foreach (var cu in custIds.Except(existingCust)) _db.Set<AmlCaseCustomer>().Add(new AmlCaseCustomer { TenantId = c.TenantId, CaseId = c.Id, CustomerId = cu, LinkedAt = _clock.UtcNow });
        _audit.Record("CASE_LINK_ALERT", "AmlCase", c.Id.ToString(CultureInfo.InvariantCulture), null, new { alertId, a.AlertNumber, convert }, restricted: Confidentiality.IsRestricted(c.Confidentiality));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Customers.CustomerService.IsUniqueViolation(ex)) { _db.ChangeTracker.Clear(); }
    }

    public async Task<object> LinkTransactionAsync(long caseId, long transactionId, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesWork);
        var c = await LoadVisibleAsync(caseId, ct);
        if (!await _db.Set<Transaction>().AnyAsync(t => t.Id == transactionId, ct)) throw new NotFoundException("Transaction", transactionId);
        if (!await _db.Set<AmlCaseTransaction>().AnyAsync(x => x.CaseId == caseId && x.TransactionId == transactionId, ct))
        {
            _db.Set<AmlCaseTransaction>().Add(new AmlCaseTransaction { TenantId = c.TenantId, CaseId = caseId, TransactionId = transactionId, LinkedAt = _clock.UtcNow });
            _audit.Record("CASE_LINK_TRANSACTION", "AmlCase", caseId.ToString(CultureInfo.InvariantCulture), null, new { transactionId }, restricted: Confidentiality.IsRestricted(c.Confidentiality));
            try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException ex) when (Customers.CustomerService.IsUniqueViolation(ex)) { _db.ChangeTracker.Clear(); }
        }
        return await GetAsync(caseId, ct);
    }

    public async Task<object> LinkCustomerAsync(long caseId, long customerId, string role, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesWork);
        var c = await LoadVisibleAsync(caseId, ct);
        if (!await _access.Customers().AnyAsync(x => x.Id == customerId, ct)) throw new NotFoundException("Customer", customerId);
        if (!await _db.Set<AmlCaseCustomer>().AnyAsync(x => x.CaseId == caseId && x.CustomerId == customerId, ct))
        {
            _db.Set<AmlCaseCustomer>().Add(new AmlCaseCustomer { TenantId = c.TenantId, CaseId = caseId, CustomerId = customerId, Role = role, LinkedAt = _clock.UtcNow });
            _audit.Record("CASE_LINK_CUSTOMER", "AmlCase", caseId.ToString(CultureInfo.InvariantCulture), null, new { customerId, role }, restricted: Confidentiality.IsRestricted(c.Confidentiality));
            try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException ex) when (Customers.CustomerService.IsUniqueViolation(ex)) { _db.ChangeTracker.Clear(); }
        }
        return await GetAsync(caseId, ct);
    }

    public async Task<object> UpdateAsync(long id, CaseUpdateRequest r, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesWork);
        var c = await LoadVisibleAsync(id, ct, tracking: true);
        _db.Entry(c).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(r.RowVersion, "case");
        if (c.Status == CaseStatuses.Closed) throw new ConflictException("Closed cases cannot be edited.");
        if (r.RecommendedAction is not null && !RecommendedActions.Contains(r.RecommendedAction)) throw new ValidationException("recommendedAction", $"Allowed: {string.Join(", ", RecommendedActions)}.");
        var old = new { c.Title, c.Priority, c.Findings, c.RecommendedAction, c.AssignedInvestigator, c.Confidentiality };
        if (r.Title is not null) c.Title = r.Title;
        if (r.Priority is not null) c.Priority = r.Priority;
        if (r.Summary is not null) c.Summary = r.Summary;
        if (r.Findings is not null) c.Findings = r.Findings;
        if (r.RecommendedAction is not null)
        {
            c.RecommendedAction = r.RecommendedAction;
            if (r.RecommendedAction == "FileReport") c.SuspicionFormedAt ??= _clock.UtcNow;
        }
        if (r.AssignedInvestigator is not null) c.AssignedInvestigator = r.AssignedInvestigator;
        if (r.Reviewer is not null) c.Reviewer = r.Reviewer;
        if (r.DueDate is not null) c.DueDate = r.DueDate;
        if (r.Confidentiality is not null)
        {
            if (Confidentiality.IsRestricted(c.Confidentiality) && !Confidentiality.IsRestricted(r.Confidentiality)) Guard.Require(_user, Perm.CasesRestricted);
            c.Confidentiality = r.Confidentiality;
            var alertIds = await _db.Set<AmlCaseAlert>().Where(x => x.CaseId == id).Select(x => x.AlertId).ToListAsync(ct);
            foreach (var a in await _db.Set<AmlAlert>().Where(a => alertIds.Contains(a.Id)).ToListAsync(ct)) a.Confidentiality = r.Confidentiality;
        }
        _audit.Record("CASE_UPDATE", "AmlCase", id.ToString(CultureInfo.InvariantCulture), old, new { c.Title, c.Priority, c.Findings, c.RecommendedAction, c.AssignedInvestigator, c.Confidentiality },
            restricted: Confidentiality.IsRestricted(c.Confidentiality));
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<object> TransitionAsync(long id, CaseTransitionRequest r, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesWork);
        var c = await LoadVisibleAsync(id, ct, tracking: true);
        if (r.RowVersion is not null) _db.Entry(c).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(r.RowVersion, "case");
        var allowed = await _db.Set<CaseStatusTransition>().AsNoTracking().Where(t => t.FromStatus == c.Status).Select(t => t.ToStatus).ToListAsync(ct);
        if (!allowed.Contains(r.ToStatus))
            throw new ConflictException($"Invalid case transition {c.Status} -> {r.ToStatus}. Valid next states: {string.Join(", ", allowed)}.", new { validNextStates = allowed });
        if (r.ToStatus == CaseStatuses.PendingApproval)
        {
            if (string.IsNullOrWhiteSpace(c.Findings) || string.IsNullOrWhiteSpace(c.RecommendedAction))
                throw new ValidationException("findings", "Findings and a recommended action are required before submitting for approval.");
            c.ApprovalStatus = "Pending";
            c.ApprovalRequestedBy = _user.Username;
        }
        if (r.ToStatus == CaseStatuses.Closed)
        {
            var errors = new Dictionary<string, List<string>>();
            if (string.IsNullOrWhiteSpace(c.Findings)) errors["findings"] = new() { "Findings are required to close a case." };
            if (string.IsNullOrWhiteSpace(r.FinalDecision ?? c.FinalDecision)) errors["finalDecision"] = new() { "A final decision is required to close a case." };
            if (string.IsNullOrWhiteSpace(r.ClosureReason ?? c.ClosureReason)) errors["closureReason"] = new() { "A closure reason is required." };
            ValidationException.ThrowIf(errors);
            if (RequiresHighApproval(c) && c.ApprovalStatus != "Approved")
                throw new ConflictException("This outcome requires MLRO/senior approval before closure (submit the case for approval).");
            c.FinalDecision = r.FinalDecision ?? c.FinalDecision;
            c.ClosureReason = r.ClosureReason ?? c.ClosureReason;
            c.ClosedAt = _clock.UtcNow;
        }
        _db.Set<AmlCaseHistory>().Add(new AmlCaseHistory { TenantId = c.TenantId, CaseId = id, FromStatus = c.Status, ToStatus = r.ToStatus, Actor = _user.Username, At = _clock.UtcNow, Comment = r.Comment });
        _audit.Record("CASE_TRANSITION", "AmlCase", id.ToString(CultureInfo.InvariantCulture), new { c.Status }, new { Status = r.ToStatus, r.FinalDecision }, r.Comment, restricted: Confidentiality.IsRestricted(c.Confidentiality));
        c.Status = r.ToStatus;
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private static bool RequiresHighApproval(AmlCase c) => c.RecommendedAction is "FileReport" or "ExitRelationship" || c.Priority is "High" or "Critical";

    public async Task<object> ApproveAsync(long id, bool approve, string? comment, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesApprove);
        var c = await LoadVisibleAsync(id, ct, tracking: true);
        if (c.ApprovalStatus != "Pending") throw new ConflictException("Case is not pending approval.");
        Guard.FourEyes(c.ApprovalRequestedBy ?? "", _user.Username, "Case outcome approval");
        if (c.RecommendedAction == "FileReport") Guard.Require(_user, Perm.StrApprove);
        c.ApprovalStatus = approve ? "Approved" : "Rejected";
        c.ApprovedBy = _user.Username;
        c.ApprovedAt = _clock.UtcNow;
        var to = approve ? CaseStatuses.UnderInvestigation : CaseStatuses.UnderInvestigation;
        _db.Set<AmlCaseHistory>().Add(new AmlCaseHistory { TenantId = c.TenantId, CaseId = id, FromStatus = c.Status, ToStatus = to, Actor = _user.Username, At = _clock.UtcNow, Comment = (approve ? "Approved: " : "Returned: ") + comment });
        c.Status = to;
        _audit.Record(approve ? "CASE_APPROVE" : "CASE_APPROVAL_REJECT", "AmlCase", id.ToString(CultureInfo.InvariantCulture), null, new { c.RecommendedAction }, comment, restricted: true);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------- notes, tasks, documents ----------
    public async Task<AmlCaseNote> AddNoteAsync(long caseId, string text, long? supersedesNoteId, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesWork);
        if (string.IsNullOrWhiteSpace(text)) throw new ValidationException("text", "Note text is required.");
        var c = await LoadVisibleAsync(caseId, ct);
        if (supersedesNoteId is { } sid && !await _db.Set<AmlCaseNote>().AnyAsync(n => n.Id == sid && n.CaseId == caseId, ct)) throw new NotFoundException("Note", sid);
        var n = new AmlCaseNote { TenantId = c.TenantId, CaseId = caseId, Author = _user.Username, CreatedAt = _clock.UtcNow, Text = text, SupersedesNoteId = supersedesNoteId };
        _db.Set<AmlCaseNote>().Add(n);
        _audit.Record(supersedesNoteId is null ? "CASE_NOTE_ADD" : "CASE_NOTE_CORRECTION", "AmlCase", caseId.ToString(CultureInfo.InvariantCulture), null, new { supersedesNoteId }, restricted: Confidentiality.IsRestricted(c.Confidentiality));
        await _db.SaveChangesAsync(ct);
        return n;
    }

    public async Task<AmlCaseTask> AddTaskAsync(long caseId, string title, string? assignee, DateTime? due, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesWork);
        if (string.IsNullOrWhiteSpace(title)) throw new ValidationException("title", "Task title is required.");
        var c = await LoadVisibleAsync(caseId, ct);
        var t = new AmlCaseTask { TenantId = c.TenantId, CaseId = caseId, Title = title, Assignee = assignee ?? _user.Username, DueDate = due, CreatedAt = _clock.UtcNow };
        _db.Set<AmlCaseTask>().Add(t);
        _audit.Record("CASE_TASK_ADD", "AmlCase", caseId.ToString(CultureInfo.InvariantCulture), null, new { title, assignee, due }, restricted: Confidentiality.IsRestricted(c.Confidentiality));
        await _db.SaveChangesAsync(ct);
        return t;
    }

    public async Task<AmlCaseTask> CompleteTaskAsync(long caseId, long taskId, CancellationToken ct)
    {
        await LoadVisibleAsync(caseId, ct);
        var t = await _db.Set<AmlCaseTask>().FirstOrDefaultAsync(x => x.Id == taskId && x.CaseId == caseId, ct) ?? throw new NotFoundException("Task", taskId);
        t.Status = "Completed";
        t.CompletedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return t;
    }

    public async Task<object> MyTasksAsync(CancellationToken ct)
    {
        var visible = _access.Cases().Select(c => c.Id);
        var now = _clock.UtcNow;
        return await _db.Set<AmlCaseTask>().AsNoTracking().Where(t => t.Assignee == _user.Username && visible.Contains(t.CaseId) && t.Status != "Completed")
            .OrderBy(t => t.DueDate).Select(t => new { t.Id, t.CaseId, t.Title, t.DueDate, t.Status, isOverdue = t.DueDate < now }).ToListAsync(ct);
    }

    public async Task<AmlCaseDocument> UploadAsync(long caseId, string fileName, string contentType, Stream content, long length, CancellationToken ct)
    {
        Guard.Require(_user, Perm.CasesWork);
        var c = await LoadVisibleAsync(caseId, ct);
        var maxBytes = await _settings.IntSettingAsync("Uploads:MaxBytes", 10 * 1024 * 1024, ct: ct);
        var safeName = Path.GetFileName(fileName);
        var ext = Path.GetExtension(safeName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) throw new ValidationException("file", $"File type '{ext}' is not allowed. Allowed: {string.Join(", ", AllowedExtensions)}.");
        if (length <= 0 || length > maxBytes) throw new ValidationException("file", $"File size must be between 1 byte and {maxBytes} bytes.");
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        if (ms.Length > maxBytes) throw new ValidationException("file", "File too large.");
        var bytes = ms.ToArray();
        if (!MagicMatches(ext, bytes)) throw new ValidationException("file", "File content does not match its extension.");
        ms.Position = 0;
        var (clean, threat) = await _scanner.ScanAsync(ms, ct);
        if (!clean)
        {
            await _audit.RecordNowAsync("UPLOAD_REJECTED_MALWARE", "AmlCase", caseId.ToString(CultureInfo.InvariantCulture), null, new { safeName, threat }, outcome: "Rejected");
            throw new ValidationException("file", $"File rejected by malware scan ({threat}).");
        }
        ms.Position = 0;
        var key = await _files.SaveAsync("case-evidence", ms, ct);
        var doc = new AmlCaseDocument
        {
            TenantId = c.TenantId, CaseId = caseId, FileName = safeName, ContentType = contentType, SizeBytes = bytes.Length, StorageKey = key,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), ScanStatus = "Clean", UploadedBy = _user.Username, UploadedAt = _clock.UtcNow
        };
        _db.Set<AmlCaseDocument>().Add(doc);
        _audit.Record("CASE_DOCUMENT_UPLOAD", "AmlCase", caseId.ToString(CultureInfo.InvariantCulture), null, new { safeName, doc.Sha256, doc.SizeBytes }, restricted: Confidentiality.IsRestricted(c.Confidentiality));
        await _db.SaveChangesAsync(ct);
        return doc;
    }

    private static bool MagicMatches(string ext, byte[] b)
    {
        bool Starts(params byte[] sig) => b.Length >= sig.Length && sig.Select((x, i) => b[i] == x).All(x => x);
        return ext switch
        {
            ".pdf" => Starts(0x25, 0x50, 0x44, 0x46),
            ".png" => Starts(0x89, 0x50, 0x4E, 0x47),
            ".jpg" or ".jpeg" => Starts(0xFF, 0xD8, 0xFF),
            ".docx" or ".xlsx" => Starts(0x50, 0x4B, 0x03, 0x04),
            _ => !b.Take(512).Contains((byte)0) // text formats: no NUL bytes
        };
    }

    public async Task<(Stream Content, string FileName, string ContentType)> DownloadAsync(long caseId, long documentId, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(caseId, ct);
        var d = await _db.Set<AmlCaseDocument>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.CaseId == caseId, ct) ?? throw new NotFoundException("Document", documentId);
        var s = await _files.OpenAsync(d.StorageKey, ct) ?? throw new NotFoundException("Document content", documentId);
        await _audit.RecordNowAsync("CASE_DOCUMENT_DOWNLOAD", "AmlCase", caseId.ToString(CultureInfo.InvariantCulture), null, new { documentId, d.FileName }, restricted: Confidentiality.IsRestricted(c.Confidentiality));
        return (s, d.FileName, d.ContentType);
    }

    public async Task<object> AccessReportAsync(DateTime? from, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AuditRead);
        var since = from ?? _clock.UtcNow.AddDays(-30);
        return await _db.Set<AuditLog>().AsNoTracking().Where(a => a.Action == "RESTRICTED_READ" && a.TimestampUtc >= since)
            .OrderByDescending(a => a.Id).Select(a => new { a.TimestampUtc, a.Actor, a.EntityType, a.EntityId, outsideTeam = a.Outcome == "OutsideTeam", a.CorrelationId }).Take(1000).ToListAsync(ct);
    }
}
