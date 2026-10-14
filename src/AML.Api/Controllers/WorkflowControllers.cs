using AML.Api.Infrastructure;
using AML.Application.Actions;
using AML.Application.Alerts;
using AML.Application.Cases;
using AML.Application.Common;
using AML.Application.Realtime;
using AML.Application.Reporting;
using AML.Application.Screening;
using AML.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AML.Api.Controllers;

[Route("api/alerts")]
public sealed class AlertsController : AmlController
{
    private readonly AlertService _svc;
    public AlertsController(AlertService svc) => _svc = svc;

    [HttpGet, HasPermission(Perm.AlertsRead)]
    public Task<PagedResult<AlertListItem>> List([FromQuery] AlertQuery q, int page = 1, int pageSize = 25, string? sort = null, string? dir = null, CancellationToken ct = default) =>
        _svc.ListAsync(q, Page(page, pageSize, sort, dir), ct);

    [HttpGet("{id:long}"), HasPermission(Perm.AlertsRead)]
    public Task<object> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    public sealed record AssignRequest(string Assignee, string? Team, string? RowVersion);

    [HttpPost("{id:long}/assign"), HasPermission(Perm.AlertsWork)]
    public Task<object> Assign(long id, [FromBody] AssignRequest r, CancellationToken ct) => _svc.AssignAsync(id, r.Assignee, r.Team, r.RowVersion, ct);

    public sealed record RowVersionRequest(string? RowVersion);

    [HttpPost("{id:long}/start-review"), HasPermission(Perm.AlertsWork)]
    public Task<object> StartReview(long id, [FromBody] RowVersionRequest? r, CancellationToken ct) => _svc.StartReviewAsync(id, r?.RowVersion, ct);

    [HttpPost("{id:long}/disposition"), HasPermission(Perm.AlertsWork)]
    public Task<object> Disposition(long id, [FromBody] DispositionRequest r, CancellationToken ct) => _svc.DispositionAsync(id, r, ct);

    public sealed record ApproveClosureRequest(bool Approve, string? Comment, string? RowVersion);

    [HttpPost("{id:long}/approve-closure"), HasPermission(Perm.AlertsApprove)]
    public Task<object> ApproveClosure(long id, [FromBody] ApproveClosureRequest r, CancellationToken ct) => _svc.ApproveClosureAsync(id, r.Approve, r.Comment, r.RowVersion, ct);

    public sealed record EscalateRequest(string? Reason, string? RowVersion);

    [HttpPost("{id:long}/escalate"), HasPermission(Perm.AlertsWork)]
    public Task<object> Escalate(long id, [FromBody] EscalateRequest r, CancellationToken ct) => _svc.EscalateAsync(id, r.Reason, r.RowVersion, ct);

    public sealed record ConvertRequest(string? Title, string? Confidentiality);

    [HttpPost("{id:long}/convert-to-case"), HasPermission(Perm.CasesWork)]
    public Task<object> Convert(long id, [FromBody] ConvertRequest? r, [FromServices] CaseService cases, CancellationToken ct) => cases.ConvertAlertAsync(id, r?.Title, r?.Confidentiality, ct);

    public sealed record ReopenRequest(string Reason);

    [HttpPost("{id:long}/reopen"), HasPermission(Perm.QaReview)]
    public Task<object> Reopen(long id, [FromBody] ReopenRequest r, CancellationToken ct) => _svc.ReopenAsync(id, r.Reason, ct);
}

[Route("api/cases")]
public sealed class CasesController : AmlController
{
    private readonly CaseService _svc;
    public CasesController(CaseService svc) => _svc = svc;

    [HttpGet, HasPermission(Perm.CasesRead)]
    public Task<PagedResult<CaseListItem>> List(string? search, string? status, long? customerId, int page = 1, int pageSize = 25, string? sort = null, string? dir = null, CancellationToken ct = default) =>
        _svc.ListAsync(search, status, customerId, Page(page, pageSize, sort, dir), ct);

    [HttpGet("{id:long}"), HasPermission(Perm.CasesRead)]
    public Task<object> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    [HttpPost, HasPermission(Perm.CasesWork)]
    public Task<object> Create([FromBody] CaseCreateRequest r, CancellationToken ct) => _svc.CreateAsync(r, ct);

    [HttpPut("{id:long}"), HasPermission(Perm.CasesWork)]
    public Task<object> Update(long id, [FromBody] CaseUpdateRequest r, CancellationToken ct) => _svc.UpdateAsync(id, r, ct);

    public sealed record LinkAlert(long AlertId);
    public sealed record LinkTxn(long TransactionId);
    public sealed record LinkCustomer(long CustomerId, string? Role);

    [HttpPost("{id:long}/link-alert"), HasPermission(Perm.CasesWork)]
    public Task<object> LinkAlertAction(long id, [FromBody] LinkAlert r, CancellationToken ct) => _svc.LinkAlertAsync(id, r.AlertId, ct);

    [HttpPost("{id:long}/link-transaction"), HasPermission(Perm.CasesWork)]
    public Task<object> LinkTransaction(long id, [FromBody] LinkTxn r, CancellationToken ct) => _svc.LinkTransactionAsync(id, r.TransactionId, ct);

    [HttpPost("{id:long}/link-customer"), HasPermission(Perm.CasesWork)]
    public Task<object> LinkCustomerAction(long id, [FromBody] LinkCustomer r, CancellationToken ct) => _svc.LinkCustomerAsync(id, r.CustomerId, r.Role ?? "Related", ct);

    public sealed record NoteRequest(string Text, long? SupersedesNoteId);

    [HttpPost("{id:long}/notes"), HasPermission(Perm.CasesWork)]
    public Task<AmlCaseNote> AddNote(long id, [FromBody] NoteRequest r, CancellationToken ct) => _svc.AddNoteAsync(id, r.Text, r.SupersedesNoteId, ct);

    /// <summary>Notes are immutable; edits must be submitted as correcting notes.</summary>
    [HttpPut("{id:long}/notes/{noteId:long}"), HasPermission(Perm.CasesWork)]
    public IActionResult EditNote(long id, long noteId) =>
        StatusCode(405, new ProblemDetails { Status = 405, Title = "Method not allowed", Detail = "Case notes are immutable. POST a new note with supersedesNoteId to correct it." });

    [HttpPost("{id:long}/transition"), HasPermission(Perm.CasesWork)]
    public Task<object> Transition(long id, [FromBody] CaseTransitionRequest r, CancellationToken ct) => _svc.TransitionAsync(id, r, ct);

    public sealed record ApproveRequest(bool Approve, string? Comment);

    [HttpPost("{id:long}/approve"), HasPermission(Perm.CasesApprove)]
    public Task<object> Approve(long id, [FromBody] ApproveRequest r, CancellationToken ct) => _svc.ApproveAsync(id, r.Approve, r.Comment, ct);

    public sealed record TaskRequest(string Title, string? Assignee, DateTime? DueDate);

    [HttpPost("{id:long}/tasks"), HasPermission(Perm.CasesWork)]
    public Task<AmlCaseTask> AddTask(long id, [FromBody] TaskRequest r, CancellationToken ct) => _svc.AddTaskAsync(id, r.Title, r.Assignee, r.DueDate, ct);

    [HttpPost("{id:long}/tasks/{taskId:long}/complete"), HasPermission(Perm.CasesWork)]
    public Task<AmlCaseTask> CompleteTask(long id, long taskId, CancellationToken ct) => _svc.CompleteTaskAsync(id, taskId, ct);

    [HttpGet("my-tasks"), HasPermission(Perm.CasesRead)]
    public Task<object> MyTasks(CancellationToken ct) => _svc.MyTasksAsync(ct);

    [HttpPost("{id:long}/documents"), HasPermission(Perm.CasesWork), RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<object> Upload(long id, IFormFile file, CancellationToken ct)
    {
        if (file is null) throw new ValidationException("file", "A file is required.");
        await using var s = file.OpenReadStream();
        var d = await _svc.UploadAsync(id, file.FileName, file.ContentType, s, file.Length, ct);
        return new { d.Id, d.FileName, d.SizeBytes, d.Sha256, d.ScanStatus };
    }

    [HttpGet("{id:long}/documents/{docId:long}"), HasPermission(Perm.CasesRead)]
    public async Task<IActionResult> Download(long id, long docId, CancellationToken ct)
    {
        var (content, name, type) = await _svc.DownloadAsync(id, docId, ct);
        return File(content, "application/octet-stream", name);
    }

    [HttpGet("access-report"), HasPermission(Perm.AuditRead)]
    public Task<object> AccessReport(DateTime? from, CancellationToken ct) => _svc.AccessReportAsync(from, ct);

    public sealed record CreateReportRequest(string? ReportType, long? PreviousReportId);

    [HttpPost("{id:long}/reports"), HasPermission(Perm.StrPrepare)]
    public Task<object> CreateReport(long id, [FromBody] CreateReportRequest? r, [FromServices] RegulatoryReportService reports, CancellationToken ct) =>
        reports.CreateFromCaseAsync(id, r?.ReportType ?? "STR", r?.PreviousReportId, ct);
}

[Route("api/regulatory-reports")]
public sealed class RegulatoryReportsController : AmlController
{
    private readonly RegulatoryReportService _svc;
    public RegulatoryReportsController(RegulatoryReportService svc) => _svc = svc;

    [HttpGet, HasPermission(Perm.StrPrepare)]
    public Task<List<object>> List(string? status, CancellationToken ct) => _svc.ListAsync(status, ct);

    [HttpGet("{id:long}"), HasPermission(Perm.StrPrepare)]
    public Task<object> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    [HttpPut("{id:long}"), HasPermission(Perm.StrPrepare)]
    public Task<object> Update(long id, [FromBody] StrUpdateRequest r, CancellationToken ct) => _svc.UpdateAsync(id, r, ct);

    [HttpPost("{id:long}/submit-for-review"), HasPermission(Perm.StrPrepare)]
    public Task<object> SubmitForReview(long id, CancellationToken ct) => _svc.SubmitForReviewAsync(id, ct);

    [HttpPost("{id:long}/validate"), HasPermission(Perm.StrPrepare)]
    public async Task<object> Validate(long id, CancellationToken ct) { var e = await _svc.ValidateAsync(id, ct); return new { valid = e.Count == 0, errors = e }; }

    public sealed record CommentRequest(string? Comment);

    [HttpPost("{id:long}/approve"), HasPermission(Perm.StrApprove)]
    public Task<object> Approve(long id, [FromBody] CommentRequest? r, CancellationToken ct) => _svc.ApproveAsync(id, r?.Comment, ct);

    [HttpPost("{id:long}/submit"), HasPermission(Perm.StrSubmit)]
    public Task<object> Submit(long id, CancellationToken ct) => _svc.SubmitAsync(id, ct);

    [HttpGet("{id:long}/export"), HasPermission(Perm.StrSubmit)]
    public async Task<IActionResult> Export(long id, CancellationToken ct)
    {
        var (xml, hash) = await _svc.ExportXmlAsync(id, ct);
        Response.Headers["X-Payload-SHA256"] = hash;
        return File(System.Text.Encoding.UTF8.GetBytes(xml), "application/xml; charset=utf-8", $"report-{id}.xml");
    }

    public sealed record ManualSubmission(string Evidence, string? FiuReference);

    [HttpPost("{id:long}/manual-submission"), HasPermission(Perm.StrSubmit)]
    public Task<object> Manual(long id, [FromBody] ManualSubmission r, CancellationToken ct) => _svc.RecordManualSubmissionAsync(id, r.Evidence, r.FiuReference, ct);

    public sealed record AckRequest(string FiuReference, bool Accepted, string? Details);

    [HttpPost("{id:long}/acknowledgement"), HasPermission(Perm.StrSubmit)]
    public Task<object> Ack(long id, [FromBody] AckRequest r, CancellationToken ct) => _svc.RecordAcknowledgementAsync(id, r.FiuReference, r.Accepted, r.Details, ct);

    public sealed record WithdrawRequest(string Reason);

    [HttpPost("{id:long}/withdraw"), HasPermission(Perm.StrPrepare)]
    public Task<object> Withdraw(long id, [FromBody] WithdrawRequest r, CancellationToken ct) => _svc.WithdrawAsync(id, r.Reason, ct);
}

[Route("api/authority-requests")]
public sealed class AuthorityRequestsController : AmlController
{
    private readonly AuthorityRequestService _svc;
    public AuthorityRequestsController(AuthorityRequestService svc) => _svc = svc;

    [HttpGet, HasPermission(Perm.AuthorityManage)]
    public Task<List<AuthorityRequest>> List(CancellationToken ct) => _svc.ListAsync(ct);

    [HttpGet("{id:long}"), HasPermission(Perm.AuthorityManage)]
    public Task<AuthorityRequest> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    [HttpPost, HasPermission(Perm.AuthorityManage)]
    public Task<AuthorityRequest> Create([FromBody] AuthorityRequestInput i, CancellationToken ct) => _svc.CreateAsync(i, ct);

    public sealed record RespondRequest(string Response, string? Evidence);

    [HttpPost("{id:long}/respond"), HasPermission(Perm.AuthorityManage)]
    public Task<AuthorityRequest> Respond(long id, [FromBody] RespondRequest r, CancellationToken ct) => _svc.RespondAsync(id, r.Response, r.Evidence, ct);
}

[Route("api/screening")]
public sealed class ScreeningController : AmlController
{
    private readonly ScreeningService _svc;
    public ScreeningController(ScreeningService svc) => _svc = svc;

    [HttpGet("results"), HasPermission(Perm.ScreeningRead)]
    public Task<object> Results(string? status, string? category, int take = 200, CancellationToken ct = default) => _svc.ResultsAsync(status, category, take, ct);

    [HttpGet("results/{id:long}"), HasPermission(Perm.ScreeningRead)]
    public Task<object> Result(long id, CancellationToken ct) => _svc.GetResultAsync(id, ct);

    public sealed record TransitionRequest(string ToStatus, string? Notes, string? RowVersion);

    [HttpPost("results/{id:long}/transition"), HasPermission(Perm.ScreeningReview)]
    public async Task<object> Transition(long id, [FromBody] TransitionRequest r, CancellationToken ct)
    {
        var res = await _svc.TransitionAsync(id, r.ToStatus, r.Notes, r.RowVersion, ct);
        return _svc.ToDto(res, false);
    }

    [HttpPost("requests/{id:long}/retry"), HasPermission(Perm.ScreeningRun)]
    public async Task<object> Retry(long id, CancellationToken ct)
    {
        var r = await _svc.RetryAsync(id, true, ct);
        return new { r.Id, r.Status, r.Attempts, r.LastError, r.FailureHistory, r.ResultCount };
    }

    [HttpGet("/api/watchlists"), HasPermission(Perm.ScreeningRead)]
    public async Task<object> Sources([FromServices] IAmlDbContext db, CancellationToken ct) =>
        await db.Set<WatchlistSource>().AsNoTracking().Select(s => new { s.Id, s.Code, s.Name, s.ListType, s.IsMandatory, s.CurrentVersion, s.IsActive, s.IsTestData }).ToListAsync(ct);

    [HttpGet("/api/watchlists/{code}/versions"), HasPermission(Perm.ScreeningRead)]
    public async Task<object> Versions(string code, [FromServices] IAmlDbContext db, CancellationToken ct) =>
        await db.Set<WatchlistVersion>().AsNoTracking().Where(v => v.SourceCode == code).OrderByDescending(v => v.Id).ToListAsync(ct);

    [HttpGet("/api/watchlists/{code}/entries"), HasPermission(Perm.ScreeningRead)]
    public async Task<object> Entries(string code, [FromServices] IAmlDbContext db, CancellationToken ct) =>
        await db.Set<WatchlistEntry>().AsNoTracking().Where(e => e.SourceCode == code).OrderBy(e => e.EntryId)
            .Select(e => new { e.Id, e.EntryId, e.PrimaryName, e.AliasesJson, e.DateOfBirth, e.Nationality, e.Category, e.Status, e.VersionAdded, e.LastAmendedVersion, e.IsTestData }).Take(1000).ToListAsync(ct);

    [HttpPost("/api/watchlists/{code}/versions"), HasPermission(Perm.WatchlistsManage)]
    public async Task<object> Import(string code, [FromBody] ListUpdateRequest r, CancellationToken ct)
    {
        var v = await _svc.ImportListUpdateAsync(code, r, ct);
        RealtimeService.InvalidateCache(v.TenantId);
        return v;
    }

    public sealed record ProviderSimulation(string Mode, int FailTimes);

    /// <summary>Development/test only: simulates provider outages (ignored in Production).</summary>
    [HttpPost("provider-simulation"), HasPermission(Perm.AdminSettings)]
    public async Task<IActionResult> Simulate([FromBody] ProviderSimulation r, [FromServices] IAmlDbContext db, [FromServices] IEnvironmentInfo env, [FromServices] IAuditService audit, CancellationToken ct)
    {
        if (env.IsProduction) return NotFound();
        var s = await db.Set<ScreeningProviderStatus>().FirstAsync(ct);
        s.SimulationMode = r.Mode;
        s.SimulatedFailuresRemaining = r.FailTimes;
        s.IsAvailable = r.Mode == "Normal";
        audit.Record("SCREENING_PROVIDER_SIMULATION", "ScreeningProviderStatus", s.ProviderCode, null, r);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

[Route("api/compliance-actions")]
public sealed class ActionsController : AmlController
{
    private readonly ComplianceActionService _svc;
    public ActionsController(ComplianceActionService svc) => _svc = svc;

    [HttpGet, HasPermission(Perm.ActionsRead)]
    public Task<List<ActionDto>> List(long? customerId, string? status, CancellationToken ct) => _svc.ListAsync(customerId, status, ct);

    [HttpGet("{id:long}"), HasPermission(Perm.ActionsRead)]
    public Task<ActionDto> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    [HttpPost, HasPermission(Perm.ActionsRequest)]
    public Task<ActionDto> Request([FromBody] ActionRequest r, CancellationToken ct) => _svc.RequestAsync(r, ct);

    public sealed record ApproveRequest(bool Approve, string? Reason);

    [HttpPost("{id:long}/approve"), HasPermission(Perm.ActionsApprove)]
    public Task<ActionDto> Approve(long id, [FromBody] ApproveRequest r, CancellationToken ct) => _svc.ApproveAsync(id, r.Approve, r.Reason, ct);

    [HttpPost("{id:long}/retry"), HasPermission(Perm.ActionsRequest)]
    public async Task<ActionDto> Retry(long id, CancellationToken ct) { await _svc.DispatchAsync(id, ct); return await _svc.GetAsync(id, ct); }

    [HttpPost("reconcile"), HasPermission(Perm.ActionsRequest)]
    public async Task<object> Reconcile([FromServices] ICurrentUser user, CancellationToken ct) => await _svc.ReconcileAsync(user.TenantId, ct);

    [HttpGet("notifications"), HasPermission(Perm.ActionsRead)]
    public Task<List<AuthorityNotification>> Notifications(long? customerId, CancellationToken ct) => _svc.NotificationsAsync(customerId, ct);

    [HttpGet("/api/holds"), HasPermission(Perm.HoldsWork)]
    public Task<List<HoldQueueItem>> Holds(string? status, [FromServices] RealtimeService rt, CancellationToken ct) => rt.HoldQueueAsync(status, ct);

    public sealed record HoldDecision(bool Release, string? Note);

    [HttpPost("/api/holds/{id:long}/decision"), HasPermission(Perm.HoldsWork)]
    public Task<HoldQueueItem> DecideHold(long id, [FromBody] HoldDecision r, [FromServices] RealtimeService rt, CancellationToken ct) => rt.DecideHoldAsync(id, r.Release, r.Note, ct);

    [HttpGet("/api/realtime/decisions"), HasPermission(Perm.HoldsWork)]
    public Task<object> Decisions([FromServices] RealtimeService rt, int take = 100, CancellationToken ct = default) => rt.DecisionsAsync(take, ct);
}
