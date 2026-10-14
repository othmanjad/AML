using System.Globalization;
using System.Text.Json;
using AML.Application.Cases;
using AML.Application.Common;
using AML.Application.Realtime;
using AML.Application.Risk;
using AML.Application.Transactions;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AML.Application.Reporting;

public sealed record FiuPayloadSubject(long CustomerId, string Name, string? NameArabic, string CustomerType, DateOnly? DateOfBirth, string? Nationality, string? IdType, string? IdNumber, IReadOnlyList<string> Accounts);
public sealed record FiuPayloadTransaction(string Reference, DateTime TimestampUtc, decimal Amount, string Currency, string Type, string? From, string? To);
public sealed record FiuPayload(string ReportingEntityId, string ReportNumber, string ReportType, string? Narrative, IReadOnlyList<string> Indicators,
    IReadOnlyList<FiuPayloadSubject> Subjects, IReadOnlyList<FiuPayloadTransaction> Transactions, string? PreviousReportNumber, DateTime GeneratedAt);

/// <summary>Pluggable FIU report-format adapter (e.g. goAML XML). The schema must be confirmed with the national FIU before production.</summary>
public interface IFiuReportAdapter
{
    string Format { get; }
    string BuildXml(FiuPayload payload);
    IReadOnlyList<string> Validate(string xml);
}

/// <summary>Submission channel. Non-production environments only use the sandbox channel.</summary>
public interface IFiuSubmissionChannel
{
    string Name { get; }
    Task<(bool Accepted, string? Reference, string? Message)> SubmitAsync(string reportNumber, string xml, CancellationToken ct);
}

public sealed class StrUpdateRequest
{
    public string? Narrative { get; set; }
    public List<string>? IndicatorCodes { get; set; }
    public List<long>? TransactionIds { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class RegulatoryReportService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly AccessPolicy _access;
    private readonly CaseService _cases;
    private readonly IFiuReportAdapter _adapter;
    private readonly IFiuSubmissionChannel _channel;
    private readonly ICryptoService _crypto;
    private readonly TenantSettingsService _settings;
    private readonly IEnvironmentInfo _env;

    public RegulatoryReportService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, AccessPolicy access, CaseService cases, IFiuReportAdapter adapter,
        IFiuSubmissionChannel channel, ICryptoService crypto, TenantSettingsService settings, IEnvironmentInfo env)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _access = access; _cases = cases; _adapter = adapter; _channel = channel; _crypto = crypto; _settings = settings; _env = env;
    }

    public object ToDto(RegulatoryReport r) => new
    {
        r.Id, r.ReportNumber, r.ReportType, r.CaseId, r.Status, r.Confidentiality, r.Narrative, indicators = Split(r.IndicatorCodes), subjects = JsonDocument.Parse(r.SubjectsJson).RootElement,
        transactions = JsonDocument.Parse(r.TransactionsJson).RootElement, r.PreparedBy, r.ReviewedBy, r.ApprovedBy, r.ApprovedAt,
        validationErrors = r.ValidationErrors is null ? null : JsonSerializer.Deserialize<List<string>>(r.ValidationErrors), r.ValidatedAt, r.SubmittedBy, r.SubmittedAt,
        r.SubmissionChannel, r.PayloadHash, r.FiuReference, r.AcknowledgedAt, r.AcknowledgementDetails, r.ManualSubmissionEvidence, r.PreviousReportId, r.SuspicionFormedAt,
        r.DueAt, r.IsOverdue, r.ReminderSentAt, r.EscalatedAt, r.IsTestData, r.CreatedAt, rowVersion = Convert.ToBase64String(r.RowVersion),
        submitAllowed = r.Status == ReportStatuses.Approved && !r.IsTestData
    };

    private static List<string> Split(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public async Task<List<object>> ListAsync(string? status, CancellationToken ct) =>
        (await _access.Reports().AsNoTracking().Where(r => status == null || r.Status == status).OrderByDescending(r => r.Id).Take(500).ToListAsync(ct)).Select(ToDto).ToList();

    private async Task<RegulatoryReport> LoadAsync(long id, CancellationToken ct, bool tracking = true)
    {
        var q = _access.Reports();
        if (!tracking) q = q.AsNoTracking();
        var r = await q.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Report", id);
        await _audit.RecordNowAsync("RESTRICTED_READ", "RegulatoryReport", id.ToString(CultureInfo.InvariantCulture), null, null, restricted: true);
        return r;
    }

    public async Task<object> GetAsync(long id, CancellationToken ct) => ToDto(await LoadAsync(id, ct, tracking: false));

    /// <summary>Creates an STR/SAR from a case (never from a raw alert), prefilled with subjects, identifiers and transactions.</summary>
    public async Task<object> CreateFromCaseAsync(long caseId, string reportType, long? previousReportId, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrPrepare);
        if (reportType is not ("STR" or "SAR" or "ContinuingActivity" or "Amendment")) throw new ValidationException("reportType", "Allowed: STR, SAR, ContinuingActivity, Amendment.");
        var c = await _cases.LoadVisibleAsync(caseId, ct);
        var customerIds = await _db.Set<AmlCaseCustomer>().AsNoTracking().Where(x => x.CaseId == caseId).Select(x => x.CustomerId).ToListAsync(ct);
        if (c.PrimaryCustomerId is { } p && !customerIds.Contains(p)) customerIds.Insert(0, p);
        var customers = await _db.Set<Customer>().AsNoTracking().Where(x => customerIds.Contains(x.Id)).ToListAsync(ct);
        var ids = await _db.Set<CustomerIdentification>().AsNoTracking().Where(i => customerIds.Contains(i.CustomerId)).ToListAsync(ct);
        var wallets = await _db.Set<Wallet>().AsNoTracking().Where(w => w.CustomerId != null && customerIds.Contains(w.CustomerId.Value)).ToListAsync(ct);
        var subjects = customers.Select(cu => new
        {
            customerId = cu.Id, name = cu.NameLatin ?? cu.NameArabic, nameArabic = cu.NameArabic, cu.CustomerType, cu.DateOfBirth, nationality = cu.NationalityCountry,
            identifiers = ids.Where(i => i.CustomerId == cu.Id).Select(i => new { i.IdType, masked = "******" + i.Last4, i.IssuingCountry }),
            accounts = wallets.Where(w => w.CustomerId == cu.Id).Select(w => w.ExternalWalletId)
        }).ToList();
        var txIds = await _db.Set<AmlCaseTransaction>().AsNoTracking().Where(x => x.CaseId == caseId).Select(x => x.TransactionId).ToListAsync(ct);
        var txns = await _db.Set<Transaction>().AsNoTracking().Where(t => txIds.Contains(t.Id)).OrderBy(t => t.TimestampUtc)
            .Select(t => new { t.Id, reference = t.SourceTransactionId, t.TimestampUtc, t.Amount, t.Currency, type = t.TransactionType, from = t.SenderWalletExternalId, to = t.ReceiverWalletExternalId }).ToListAsync(ct);
        RegulatoryReport? previous = null;
        if (previousReportId is { } pid)
        {
            previous = await LoadAsync(pid, ct);
            if (!ReportStatuses.IsFiled(previous.Status)) throw new ConflictException("Only filed reports can be amended or continued.");
        }
        var formed = c.SuspicionFormedAt ?? _clock.UtcNow;
        var r = new RegulatoryReport
        {
            TenantId = c.TenantId, ReportNumber = "PENDING-" + Guid.NewGuid().ToString("N"), ReportType = reportType, CaseId = caseId, Status = ReportStatuses.Draft,
            SubjectsJson = JsonSerializer.Serialize(subjects), TransactionsJson = JsonSerializer.Serialize(txns), PreparedBy = _user.Username, PreviousReportId = previousReportId,
            SuspicionFormedAt = formed, DueAt = await DueAtAsync(formed, ct), Narrative = previous?.Narrative, IndicatorCodes = previous?.IndicatorCodes, CreatedAt = _clock.UtcNow
        };
        _db.Set<RegulatoryReport>().Add(r);
        await _db.SaveChangesAsync(ct);
        r.ReportNumber = $"STR-{_clock.UtcNow:yyyy}-{r.Id:D5}";
        if (previous is not null && reportType == "Amendment") previous.Status = ReportStatuses.Amended;
        _audit.Record("STR_CREATE", "RegulatoryReport", r.Id.ToString(CultureInfo.InvariantCulture), null, new { r.ReportNumber, caseId, reportType, previousReportId, r.DueAt }, restricted: true);
        await _db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    private async Task<DateTime> DueAtAsync(DateTime formed, CancellationToken ct)
    {
        var j = await _settings.JurisdictionAsync("STR_FILING_DEADLINE", formed, ct: ct);
        var days = j is not null && int.TryParse(j.Value, out var d) ? d : 10;
        if (j?.Unit == "WorkingDays") return (await _settings.CalendarAsync(ct: ct)).AddWorkingDays(formed, days);
        return formed.AddDays(days);
    }

    public async Task<object> UpdateAsync(long id, StrUpdateRequest u, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrPrepare);
        var r = await LoadAsync(id, ct);
        if (r.Status is not (ReportStatuses.Draft or ReportStatuses.UnderReview))
            throw new ConflictException($"Report is {r.Status}; submitted/approved reports are immutable. Create an amendment instead.");
        if (u.RowVersion is not null) _db.Entry(r).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(u.RowVersion, "report");
        if (u.Narrative is not null) r.Narrative = u.Narrative;
        if (u.IndicatorCodes is not null) r.IndicatorCodes = string.Join(",", u.IndicatorCodes);
        if (u.TransactionIds is not null)
        {
            var allowed = await _db.Set<AmlCaseTransaction>().Where(x => x.CaseId == r.CaseId).Select(x => x.TransactionId).ToListAsync(ct);
            if (u.TransactionIds.Except(allowed).Any()) throw new ValidationException("transactionIds", "Selected transactions must be linked to the case.");
            var txns = await _db.Set<Transaction>().AsNoTracking().Where(t => u.TransactionIds.Contains(t.Id)).OrderBy(t => t.TimestampUtc)
                .Select(t => new { t.Id, reference = t.SourceTransactionId, t.TimestampUtc, t.Amount, t.Currency, type = t.TransactionType, from = t.SenderWalletExternalId, to = t.ReceiverWalletExternalId }).ToListAsync(ct);
            r.TransactionsJson = JsonSerializer.Serialize(txns);
        }
        r.ValidationErrors = null;
        _audit.Record("STR_UPDATE", "RegulatoryReport", id.ToString(CultureInfo.InvariantCulture), null, new { narrativeLength = r.Narrative?.Length, r.IndicatorCodes }, restricted: true);
        await _db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    public async Task<object> SubmitForReviewAsync(long id, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrPrepare);
        var r = await LoadAsync(id, ct);
        if (r.Status != ReportStatuses.Draft) throw new ConflictException("Only drafts can be submitted for review.");
        var min = await _settings.IntSettingAsync("Str:MinNarrativeLength", 50, ct: ct);
        if (string.IsNullOrWhiteSpace(r.Narrative) || r.Narrative.Trim().Length < min) throw new ValidationException("narrative", $"A narrative of at least {min} characters is required.");
        r.Status = ReportStatuses.UnderReview;
        _audit.Record("STR_SUBMIT_REVIEW", "RegulatoryReport", id.ToString(CultureInfo.InvariantCulture), null, null, restricted: true);
        await _db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    private async Task<FiuPayload> BuildPayloadAsync(RegulatoryReport r, CancellationToken ct)
    {
        var tenant = await _settings.TenantAsync(r.TenantId, ct);
        using var subjects = JsonDocument.Parse(r.SubjectsJson);
        var list = new List<FiuPayloadSubject>();
        foreach (var s in subjects.RootElement.EnumerateArray())
        {
            var cid = s.GetProperty("customerId").GetInt64();
            var id = await _db.Set<CustomerIdentification>().AsNoTracking().Where(i => i.CustomerId == cid).OrderByDescending(i => i.Id).FirstOrDefaultAsync(ct);
            list.Add(new FiuPayloadSubject(cid, s.GetProperty("name").GetString() ?? "", s.TryGetProperty("nameArabic", out var na) ? na.GetString() : null,
                s.GetProperty("CustomerType").GetString() ?? "", s.TryGetProperty("DateOfBirth", out var dob) && dob.ValueKind == JsonValueKind.String ? DateOnly.Parse(dob.GetString()!, CultureInfo.InvariantCulture) : null,
                s.TryGetProperty("nationality", out var nat) ? nat.GetString() : null, id?.IdType, id is null ? null : _crypto.Decrypt(id.IdNumberEncrypted),
                s.GetProperty("accounts").EnumerateArray().Select(a => a.GetString()!).ToList()));
        }
        using var txDoc = JsonDocument.Parse(r.TransactionsJson);
        var txns = txDoc.RootElement.EnumerateArray().Select(t => new FiuPayloadTransaction(t.GetProperty("reference").GetString()!, t.GetProperty("TimestampUtc").GetDateTime(),
            t.GetProperty("Amount").GetDecimal(), t.GetProperty("Currency").GetString()!, t.GetProperty("type").GetString()!,
            t.TryGetProperty("from", out var f) ? f.GetString() : null, t.TryGetProperty("to", out var to) ? to.GetString() : null)).ToList();
        var prev = r.PreviousReportId is { } pid ? await _db.Set<RegulatoryReport>().AsNoTracking().Where(x => x.Id == pid).Select(x => x.ReportNumber).FirstOrDefaultAsync(ct) : null;
        return new FiuPayload(tenant.ReportingEntityId, r.ReportNumber, r.ReportType, r.Narrative, Split(r.IndicatorCodes), list, txns, prev, _clock.UtcNow);
    }

    public async Task<List<string>> ValidateAsync(long id, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct);
        var xml = _adapter.BuildXml(await BuildPayloadAsync(r, ct));
        var errors = _adapter.Validate(xml).ToList();
        r.ValidationErrors = JsonSerializer.Serialize(errors);
        r.ValidatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return errors;
    }

    public async Task<object> ApproveAsync(long id, string? comment, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrApprove);
        var r = await LoadAsync(id, ct);
        if (r.Status != ReportStatuses.UnderReview) throw new ConflictException("Only reports under review can be approved.");
        Guard.FourEyes(r.PreparedBy, _user.Username, "STR approval");
        var errors = await ValidateAsync(id, ct);
        if (errors.Count > 0) throw new ValidationException(new Dictionary<string, string[]> { ["payload"] = errors.ToArray() }, "Report payload failed schema validation: " + string.Join(" ", errors));
        r.Status = ReportStatuses.Approved;
        r.ReviewedBy ??= _user.Username;
        r.ApprovedBy = _user.Username;
        r.ApprovedAt = _clock.UtcNow;
        _audit.Record("STR_APPROVE", "RegulatoryReport", id.ToString(CultureInfo.InvariantCulture), null, new { r.ReportNumber }, comment, restricted: true);
        await _db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    /// <summary>Explicit submission only (never automatic). Non-production environments submit to the sandbox channel; demo reports cannot be submitted.</summary>
    public async Task<object> SubmitAsync(long id, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrSubmit);
        var r = await LoadAsync(id, ct);
        if (r.Status != ReportStatuses.Approved) throw new ConflictException("Only approved reports can be submitted.");
        if (r.IsTestData) throw new ConflictException("Demo/test reports can never be submitted.");
        var xml = _adapter.BuildXml(await BuildPayloadAsync(r, ct));
        var errors = _adapter.Validate(xml);
        if (errors.Count > 0) throw new ValidationException(new Dictionary<string, string[]> { ["payload"] = errors.ToArray() }, "Payload failed validation.");
        var (accepted, reference, message) = await _channel.SubmitAsync(r.ReportNumber, xml, ct);
        if (!accepted) throw new ConflictException($"Submission channel {_channel.Name} rejected the submission: {message}");
        r.PayloadXml = xml;
        r.PayloadHash = IngestionService.Sha256(xml);
        r.SubmittedAt = _clock.UtcNow;
        r.SubmittedBy = _user.Username;
        r.SubmissionChannel = _channel.Name;
        r.Status = ReportStatuses.Submitted;
        if (reference is not null) r.FiuReference = reference;
        _audit.Record("STR_SUBMIT", "RegulatoryReport", id.ToString(CultureInfo.InvariantCulture), null, new { r.ReportNumber, r.PayloadHash, r.SubmissionChannel, environment = _env.Name }, restricted: true);
        await _db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    public async Task<(string Xml, string Hash)> ExportXmlAsync(long id, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrSubmit);
        var r = await LoadAsync(id, ct);
        if (r.Status is not (ReportStatuses.Approved or ReportStatuses.Submitted or ReportStatuses.Acknowledged)) throw new ConflictException("Only approved reports can be exported.");
        var xml = r.PayloadXml ?? _adapter.BuildXml(await BuildPayloadAsync(r, ct));
        var hash = IngestionService.Sha256(xml);
        if (r.PayloadXml is null) { r.PayloadXml = xml; r.PayloadHash = hash; }
        _audit.Record("STR_EXPORT", "RegulatoryReport", id.ToString(CultureInfo.InvariantCulture), null, new { hash }, restricted: true);
        await _db.SaveChangesAsync(ct);
        return (xml, hash);
    }

    public async Task<object> RecordManualSubmissionAsync(long id, string evidence, string? fiuReference, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrSubmit);
        if (string.IsNullOrWhiteSpace(evidence)) throw new ValidationException("evidence", "Submission evidence is required.");
        var r = await LoadAsync(id, ct);
        if (r.Status != ReportStatuses.Approved) throw new ConflictException("Only approved reports can be recorded as submitted.");
        if (r.IsTestData) throw new ConflictException("Demo/test reports can never be submitted.");
        if (r.PayloadXml is null) throw new ConflictException("Export the XML before recording a manual submission (file hash required).");
        r.SubmittedAt = _clock.UtcNow;
        r.SubmittedBy = _user.Username;
        r.SubmissionChannel = "ManualUpload";
        r.ManualSubmissionEvidence = evidence;
        r.FiuReference = fiuReference;
        r.Status = ReportStatuses.Submitted;
        _audit.Record("STR_MANUAL_SUBMISSION", "RegulatoryReport", id.ToString(CultureInfo.InvariantCulture), null, new { r.PayloadHash, evidence, fiuReference }, restricted: true);
        await _db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    public async Task<object> RecordAcknowledgementAsync(long id, string fiuReference, bool accepted, string? details, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrSubmit);
        if (string.IsNullOrWhiteSpace(fiuReference)) throw new ValidationException("fiuReference", "FIU reference is required.");
        var r = await LoadAsync(id, ct);
        if (r.Status != ReportStatuses.Submitted) throw new ConflictException("Only submitted reports can be acknowledged.");
        r.FiuReference = fiuReference;
        r.AcknowledgedAt = _clock.UtcNow;
        r.AcknowledgementDetails = details;
        r.Status = accepted ? ReportStatuses.Acknowledged : ReportStatuses.RejectedByFiu;
        _audit.Record("STR_ACKNOWLEDGEMENT", "RegulatoryReport", id.ToString(CultureInfo.InvariantCulture), null, new { fiuReference, accepted, details }, restricted: true);
        await _db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    public async Task<object> WithdrawAsync(long id, string reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.StrPrepare);
        var r = await LoadAsync(id, ct);
        if (ReportStatuses.IsFiled(r.Status)) throw new ConflictException("Filed reports cannot be withdrawn; create an amendment.");
        r.Status = ReportStatuses.Withdrawn;
        _audit.Record("STR_WITHDRAW", "RegulatoryReport", id.ToString(CultureInfo.InvariantCulture), null, null, reason, restricted: true);
        await _db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    /// <summary>SLA reminders and overdue escalation (escalates once).</summary>
    public async Task<int> CheckSlasAsync(long tenantId, CancellationToken ct)
    {
        var reminderHours = await _settings.IntSettingAsync("Str:ReminderHoursBeforeDue", 48, tenantId, ct);
        var now = _clock.UtcNow;
        var open = await _db.Set<RegulatoryReport>().IgnoreQueryFilters().Where(r => r.TenantId == tenantId && r.DueAt != null &&
            (r.Status == ReportStatuses.Draft || r.Status == ReportStatuses.UnderReview || r.Status == ReportStatuses.Approved)).ToListAsync(ct);
        var n = 0;
        foreach (var r in open)
        {
            if (r.ReminderSentAt is null && r.DueAt <= now.AddHours(reminderHours))
            {
                r.ReminderSentAt = now; n++;
                _db.Set<OperationalAlert>().Add(new OperationalAlert { TenantId = tenantId, Category = "StrDeadlineReminder", Message = $"Report {r.ReportNumber} due {r.DueAt:O}", Reference = r.Id.ToString(CultureInfo.InvariantCulture), CreatedAt = now });
            }
            if (r.DueAt < now && !r.IsOverdue)
            {
                r.IsOverdue = true; r.EscalatedAt = now; n++;
                _db.Set<OperationalAlert>().Add(new OperationalAlert { TenantId = tenantId, Category = "StrOverdue", Message = $"Report {r.ReportNumber} overdue since {r.DueAt:O}; escalated to MLRO", Reference = r.Id.ToString(CultureInfo.InvariantCulture), Severity = "Error", CreatedAt = now });
                _audit.Record("STR_OVERDUE_ESCALATION", "RegulatoryReport", r.Id.ToString(CultureInfo.InvariantCulture), null, new { r.DueAt }, restricted: true);
            }
        }
        await _db.SaveChangesAsync(ct);
        return n;
    }
}

public sealed class AuthorityRequestInput
{
    public string Reference { get; set; } = "";
    public string Authority { get; set; } = "";
    public string RequestType { get; set; } = "InformationRequest";
    public long? CustomerId { get; set; }
    public string Scope { get; set; } = "";
    public DateTime DueDate { get; set; }
    public string? AssignedOfficer { get; set; }
    public string? Confidentiality { get; set; }
}

public sealed class AuthorityRequestService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly AccessPolicy _access;
    private readonly TenantSettingsService _settings;
    private readonly RiskAssessmentService _risk;
    private readonly ILogger<AuthorityRequestService> _log;

    public AuthorityRequestService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, AccessPolicy access, TenantSettingsService settings,
        RiskAssessmentService risk, ILogger<AuthorityRequestService> log)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _access = access; _settings = settings; _risk = risk; _log = log;
    }

    public async Task<List<AuthorityRequest>> ListAsync(CancellationToken ct) =>
        await _access.AuthorityRequests().AsNoTracking().OrderBy(r => r.DueDate).Take(500).ToListAsync(ct);

    public async Task<AuthorityRequest> GetAsync(long id, CancellationToken ct)
    {
        var r = await _access.AuthorityRequests().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Authority request", id);
        await _audit.RecordNowAsync("RESTRICTED_READ", "AuthorityRequest", id.ToString(CultureInfo.InvariantCulture), null, null, restricted: true);
        return r;
    }

    public async Task<AuthorityRequest> CreateAsync(AuthorityRequestInput i, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AuthorityManage);
        if (string.IsNullOrWhiteSpace(i.Reference) || string.IsNullOrWhiteSpace(i.Authority)) throw new ValidationException("reference", "Reference and authority are required.");
        if (i.DueDate <= _clock.UtcNow.AddMinutes(-1)) throw new ValidationException("dueDate", "Due date must be in the future.");
        if (i.CustomerId is { } cid && !await _access.Customers().AnyAsync(c => c.Id == cid, ct)) throw new ValidationException("customerId", "Unknown customer.");
        var r = new AuthorityRequest
        {
            TenantId = _user.TenantId, Reference = i.Reference, Authority = i.Authority, RequestType = i.RequestType, CustomerId = i.CustomerId, Scope = i.Scope,
            ReceivedAt = _clock.UtcNow, DueDate = i.DueDate, AssignedOfficer = i.AssignedOfficer ?? _user.Username,
            Confidentiality = i.Confidentiality ?? Domain.Common.Confidentiality.StrictlyConfidential, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username
        };
        _db.Set<AuthorityRequest>().Add(r);
        _audit.Record("AUTHORITY_REQUEST_CREATE", "AuthorityRequest", i.Reference, null, new { i.Authority, i.CustomerId, i.DueDate }, restricted: true);
        await _db.SaveChangesAsync(ct);
        if (i.CustomerId is { } customer && await _settings.BoolSettingAsync("AuthorityRequests:TriggerReassessment", true, ct: ct))
        {
            try
            {
                var a = await _risk.AssessAsync(customer, "AuthorityRequest", $"authreq:{r.Id}", $"Authority request {r.Reference}", null, ct);
                r.TriggeredAssessmentId = a.Id;
                await _db.SaveChangesAsync(ct);
            }
            catch (ConflictException ex) { _log.LogWarning("Authority-request reassessment skipped: {Reason}", ex.Message); }
        }
        return r;
    }

    public async Task<AuthorityRequest> RespondAsync(long id, string response, string? evidence, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AuthorityManage);
        if (string.IsNullOrWhiteSpace(response)) throw new ValidationException("response", "Response is required.");
        var r = await _access.AuthorityRequests().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Authority request", id);
        r.Response = response;
        r.ResponseEvidence = evidence;
        r.RespondedAt = _clock.UtcNow;
        r.Status = "Responded";
        _audit.Record("AUTHORITY_REQUEST_RESPOND", "AuthorityRequest", r.Reference, null, new { evidence }, restricted: true);
        await _db.SaveChangesAsync(ct);
        return r;
    }

    public async Task<int> MarkOverdueAsync(long tenantId, CancellationToken ct)
    {
        var due = await _db.Set<AuthorityRequest>().IgnoreQueryFilters().Where(r => r.TenantId == tenantId && r.Status != "Responded" && r.Status != "Closed" && !r.IsOverdue && r.DueDate < _clock.UtcNow).ToListAsync(ct);
        foreach (var r in due) r.IsOverdue = true;
        await _db.SaveChangesAsync(ct);
        return due.Count;
    }
}
