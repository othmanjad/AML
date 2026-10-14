using AML.Api.Infrastructure;
using AML.Application.Admin;
using AML.Application.Common;
using AML.Application.Governance;
using AML.Application.Reports;
using AML.Application.Transactions;
using AML.Domain.Entities;
using AML.Infrastructure.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace AML.Api.Controllers;

[Route("api/dashboard")]
public sealed class DashboardController : AmlController
{
    [HttpGet, HasPermission(Perm.DashboardRead)]
    public Task<object> Get([FromServices] DashboardService d, CancellationToken ct) => d.GetAsync(ct);
}

[Route("api/reports")]
public sealed class ReportsController : AmlController
{
    [HttpGet("catalogue"), HasPermission(Perm.ReportsRead)]
    public object Catalogue() => ReportService.Catalogue.Select(kv => new { code = kv.Key, title = kv.Value });

    [HttpGet("{code}"), HasPermission(Perm.ReportsRead)]
    public async Task<object> Run(string code, [FromQuery] ReportFilters f, [FromServices] ReportService r, [FromServices] ICurrentUser user, CancellationToken ct)
    {
        f.DataScopeRegion = user.DataScopeRegion;
        f.IncludeRestricted = false;
        f.IncludeSensitive = false;
        var t = await r.GenerateAsync(user.TenantId, code, f, user.Permissions, ct);
        return new { t.Code, t.Title, t.Columns, rows = t.Rows.Take(5000), total = t.Rows.Count, t.Notes };
    }

    public sealed record ExportRequest(string Format, DateTime? From, DateTime? To, string? GroupBy, bool IncludeSensitive, bool IncludeRestricted);

    [HttpPost("{code}/export"), HasPermission(Perm.ReportsExport)]
    public async Task<IActionResult> Export(string code, [FromBody] ExportRequest r, [FromServices] ExportService e, CancellationToken ct)
    {
        var job = await e.RequestAsync(code, r.Format, new ReportFilters { From = r.From, To = r.To, GroupBy = r.GroupBy, IncludeSensitive = r.IncludeSensitive, IncludeRestricted = r.IncludeRestricted }, ct);
        return Accepted(new { job.JobId, job.Status, statusUrl = $"/api/exports/{job.JobId}" });
    }

    [HttpGet("/api/exports"), HasPermission(Perm.ReportsExport)]
    public Task<List<ExportJob>> Mine([FromServices] ExportService e, CancellationToken ct) => e.MineAsync(ct);

    [HttpGet("/api/exports/{jobId:guid}"), HasPermission(Perm.ReportsExport)]
    public async Task<object> Status(Guid jobId, [FromServices] ExportService e, CancellationToken ct)
    {
        var j = await e.GetAsync(jobId, ct);
        return new { j.JobId, j.ReportCode, j.Format, j.Status, j.RowCount, j.FileName, j.Sha256, j.Error, j.CreatedAt, j.CompletedAt, j.IncludeSensitive, j.IncludeRestricted };
    }

    [HttpGet("/api/exports/{jobId:guid}/download"), HasPermission(Perm.ReportsExport)]
    public async Task<IActionResult> Download(Guid jobId, [FromServices] ExportService e, CancellationToken ct)
    {
        var (content, name, type) = await e.DownloadAsync(jobId, ct);
        return File(content, type, name);
    }
}

[Route("api/governance")]
public sealed class GovernanceController : AmlController
{
    [HttpGet("rule-metrics"), HasPermission(Perm.EffectivenessRead)]
    public Task<List<RuleMetricsDto>> Metrics(DateTime? from, DateTime? to, [FromServices] EffectivenessService e, CancellationToken ct) => e.RuleMetricsAsync(from, to, ct);

    [HttpGet("coverage"), HasPermission(Perm.EffectivenessRead)]
    public Task<object> Coverage([FromServices] EffectivenessService e, CancellationToken ct) => e.CoverageAsync(ct);

    public sealed record MapRequest(string TypologyCode, string ControlType, string Reference);

    [HttpPost("coverage"), HasPermission(Perm.TuningRun)]
    public Task<TypologyControl> Map([FromBody] MapRequest r, [FromServices] EffectivenessService e, CancellationToken ct) => e.MapControlAsync(r.TypologyCode, r.ControlType, r.Reference, ct);

    [HttpGet("feed-completeness"), HasPermission(Perm.EffectivenessRead)]
    public Task<object> Completeness(DateOnly from, DateOnly to, [FromServices] EffectivenessService e, CancellationToken ct) => e.FeedCompletenessAsync(from, to, ct);

    [HttpGet("validation-pack"), HasPermission(Perm.EffectivenessRead)]
    public Task<object> Pack([FromServices] EffectivenessService e, CancellationToken ct) => e.ValidationPackAsync(ct);

    public sealed record SampleRequest(DateTime From, DateTime To);

    [HttpPost("qa/sample"), HasPermission(Perm.QaReview)]
    public Task<object> Sample([FromBody] SampleRequest r, [FromServices] QaService q, CancellationToken ct) => q.RunSamplingAsync(r.From, r.To, ct);

    [HttpGet("qa/samples"), HasPermission(Perm.QaReview)]
    public Task<List<QaSample>> Samples(string? status, [FromServices] QaService q, CancellationToken ct) => q.SamplesAsync(status, ct);

    public sealed record QaReviewRequest(bool Passed, string? ErrorCategory, string? Findings, bool Reopen);

    [HttpPost("qa/samples/{id:long}/review"), HasPermission(Perm.QaReview)]
    public Task<QaSample> Review(long id, [FromBody] QaReviewRequest r, [FromServices] QaService q, CancellationToken ct) => q.ReviewAsync(id, r.Passed, r.ErrorCategory, r.Findings, r.Reopen, ct);

    [HttpGet("qa/analyst-quality"), HasPermission(Perm.QaReview)]
    public Task<object> AnalystQuality([FromServices] QaService q, CancellationToken ct) => q.AnalystQualityAsync(ct);

    [HttpGet("retention/policies"), HasPermission(Perm.RetentionManage)]
    public Task<List<RetentionPolicy>> Policies([FromServices] RetentionService r, CancellationToken ct) => r.PoliciesAsync(ct);

    public sealed record HoldRequest(string EntityType, long EntityId, string Reason);

    [HttpPost("retention/legal-holds"), HasPermission(Perm.RetentionManage)]
    public Task<LegalHold> Hold([FromBody] HoldRequest h, [FromServices] RetentionService r, CancellationToken ct) => r.AddHoldAsync(h.EntityType, h.EntityId, h.Reason, ct);

    public sealed record DryRunRequest(DateTime? AsOf);

    [HttpPost("retention/dry-run"), HasPermission(Perm.RetentionManage)]
    public Task<RetentionRun> DryRun([FromBody] DryRunRequest? d, [FromServices] RetentionService r, CancellationToken ct) => r.DryRunAsync(d?.AsOf, ct);

    [HttpGet("retention/runs"), HasPermission(Perm.RetentionManage)]
    public Task<List<RetentionRun>> Runs([FromServices] RetentionService r, CancellationToken ct) => r.RunsAsync(ct);

    [HttpPost("retention/runs/{id:long}/submit"), HasPermission(Perm.RetentionManage)]
    public Task<RetentionRun> SubmitRun(long id, [FromServices] RetentionService r, CancellationToken ct) => r.SubmitAsync(id, ct);

    [HttpPost("retention/runs/{id:long}/approve"), HasPermission(Perm.RetentionApprove)]
    public Task<RetentionRun> ApproveRun(long id, [FromServices] RetentionService r, CancellationToken ct) => r.ApproveAsync(id, ct);

    [HttpPost("retention/runs/{id:long}/execute"), HasPermission(Perm.RetentionManage)]
    public Task<RetentionRun> ExecuteRun(long id, [FromServices] RetentionService r, CancellationToken ct) => r.ExecuteAsync(id, ct);

    [HttpGet("jurisdiction"), HasPermission(Perm.JurisdictionManage)]
    public Task<List<JurisdictionSetting>> Jurisdiction([FromServices] JurisdictionService j, CancellationToken ct) => j.ListAsync(ct);

    public sealed record JurisdictionRequest(string Key, string Value, string? Unit, string? LegalSource, string? ArticleReference, DateTime? EffectiveFrom, string? ComplianceOwner);

    [HttpPost("jurisdiction"), HasPermission(Perm.JurisdictionManage)]
    public Task<JurisdictionSetting> Propose([FromBody] JurisdictionRequest r, [FromServices] JurisdictionService j, CancellationToken ct) =>
        j.ProposeAsync(r.Key, r.Value, r.Unit, r.LegalSource, r.ArticleReference, r.EffectiveFrom, r.ComplianceOwner, ct);

    [HttpPost("jurisdiction/{id:long}/approve"), HasPermission(Perm.JurisdictionApprove)]
    public Task<JurisdictionSetting> ApproveJurisdiction(long id, [FromServices] JurisdictionService j, CancellationToken ct) => j.ApproveAsync(id, ct);

    [HttpPost("entity-resolution/run"), HasPermission(Perm.EntityRead)]
    public Task<object> RunEntityResolution([FromServices] EntityResolutionService e, [FromServices] ICurrentUser u, CancellationToken ct) => e.RunAsync(u.TenantId, ct);
}

[Route("api/admin")]
public sealed class AdminController : AmlController
{
    private readonly AdminService _svc;
    public AdminController(AdminService svc) => _svc = svc;

    [HttpGet("users"), HasPermission(Perm.AdminUsers)]
    public Task<List<UserDto>> Users(CancellationToken ct) => _svc.UsersAsync(ct);

    [HttpPost("users"), HasPermission(Perm.AdminUsers)]
    public Task<UserDto> CreateUser([FromBody] UserInput i, CancellationToken ct) => _svc.CreateUserAsync(i, ct);

    [HttpPut("users/{id:long}"), HasPermission(Perm.AdminUsers)]
    public Task<UserDto> UpdateUser(long id, [FromBody] UserInput i, CancellationToken ct) => _svc.UpdateUserAsync(id, i, ct);

    [HttpPost("users/{id:long}/revoke-sessions"), HasPermission(Perm.AdminUsers)]
    public async Task<IActionResult> Revoke(long id, CancellationToken ct) { await _svc.RevokeSessionsAsync(id, ct); return NoContent(); }

    [HttpGet("roles"), HasPermission(Perm.AdminUsers)]
    public Task<object> Roles(CancellationToken ct) => _svc.RolesAsync(ct);

    public sealed record RolePermsRequest(List<string> Permissions, string? Reason);

    [HttpPut("roles/{code}/permissions"), HasPermission(Perm.AdminUsers)]
    public async Task<IActionResult> SetPerms(string code, [FromBody] RolePermsRequest r, CancellationToken ct) { await _svc.SetRolePermissionsAsync(code, r.Permissions, r.Reason, ct); return NoContent(); }

    [HttpGet("lookups"), HasPermission(Perm.AdminLookups)]
    public Task<List<LookupValue>> Lookups(string? category, CancellationToken ct) => _svc.LookupsAsync(category, ct);

    [HttpPost("lookups"), HasPermission(Perm.AdminLookups)]
    public Task<LookupValue> UpsertLookup([FromBody] LookupValue v, CancellationToken ct) => _svc.UpsertLookupAsync(v, ct);

    [HttpGet("settings"), HasPermission(Perm.AdminSettings)]
    public Task<List<SystemSetting>> Settings(CancellationToken ct) => _svc.SettingsAsync(ct);

    public sealed record SettingRequest(string Value, string? Reason);

    [HttpPut("settings/{key}"), HasPermission(Perm.AdminSettings)]
    public Task<SystemSetting> SetSetting(string key, [FromBody] SettingRequest r, CancellationToken ct) => _svc.SetSettingAsync(key, r.Value, r.Reason, ct);

    [HttpGet("fx-rates"), HasPermission(Perm.AdminFx)]
    public Task<List<FxRate>> Fx(bool includeHistory = false, CancellationToken ct = default) => _svc.FxRatesAsync(includeHistory, ct);

    public sealed record FxRequest(string FromCurrency, string ToCurrency, decimal Rate, DateTime EffectiveFrom, string Source, string? RateType);

    [HttpPost("fx-rates"), HasPermission(Perm.AdminFx)]
    public Task<FxRate> AddFx([FromBody] FxRequest r, CancellationToken ct) => _svc.AddFxRateAsync(r.FromCurrency, r.ToCurrency, r.Rate, r.EffectiveFrom, r.Source, r.RateType ?? "Mid", ct);

    public sealed record FxEditRequest(decimal Rate, string? Reason);

    [HttpPut("fx-rates/{id:long}"), HasPermission(Perm.AdminFx)]
    public Task<FxRate> EditFx(long id, [FromBody] FxEditRequest r, CancellationToken ct) => _svc.EditFxRateAsync(id, r.Rate, r.Reason, ct);

    [HttpGet("holidays"), HasPermission(Perm.AdminSettings)]
    public Task<List<Holiday>> Holidays(CancellationToken ct) => _svc.HolidaysAsync(ct);

    public sealed record HolidayRequest(DateOnly Date, string Name);

    [HttpPost("holidays"), HasPermission(Perm.AdminSettings)]
    public Task<Holiday> AddHoliday([FromBody] HolidayRequest r, CancellationToken ct) => _svc.AddHolidayAsync(r.Date, r.Name, ct);

    [HttpGet("source-systems"), HasPermission(Perm.AdminSettings)]
    public Task<object> Sources(CancellationToken ct) => _svc.SourceSystemsAsync(ct);

    [HttpPost("source-systems/{code}/rotate-secret"), HasPermission(Perm.AdminSettings)]
    public Task<object> Rotate(string code, CancellationToken ct) => _svc.RotateClientSecretAsync(code, ct);

    [HttpGet("jobs"), HasPermission(Perm.AdminJobs)]
    public object Jobs([FromServices] IConfiguration c) => JobCatalog.All(c).Select(j => new { j.Name, interval = j.Interval.ToString(), j.PerTenant });

    [HttpPost("jobs/{name}/run"), HasPermission(Perm.AdminJobs)]
    public async Task<object> RunJob(string name, [FromServices] JobExecutor jobs, [FromServices] IAuditService audit, CancellationToken ct)
    {
        await audit.RecordNowAsync("JOB_RUN_MANUAL", "Job", name, ct: ct);
        return new { job = name, processed = await jobs.RunAsync(name, ct) };
    }

    [HttpGet("audit"), HasPermission(Perm.AuditRead)]
    public Task<PagedResult<AuditLog>> Audit(string? entityType, string? entityId, string? actor, string? action, DateTime? from, DateTime? to, [FromServices] AuditQueryService a, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        a.QueryAsync(entityType, entityId, actor, action, from, to, Page(page, pageSize, null, null), ct);

    [HttpPost("audit/verify"), HasPermission(Perm.AuditRead)]
    public Task<object> Verify([FromServices] AuditQueryService a, CancellationToken ct) => a.VerifyIntegrityAsync(ct);
}

[Route("api/operations")]
public sealed class OperationsController : AmlController
{
    private readonly OperationsService _svc;
    public OperationsController(OperationsService svc) => _svc = svc;

    [HttpGet("health"), HasPermission(Perm.OperationsRead)]
    public Task<object> Health(CancellationToken ct) => _svc.HealthAsync(ct);

    [HttpGet("ingestion-runs"), HasPermission(Perm.OperationsRead)]
    public Task<object> Runs(int take = 50, CancellationToken ct = default) => _svc.IngestionRunsAsync(take, ct);

    [HttpGet("ingestion-records"), HasPermission(Perm.OperationsRead)]
    public Task<PagedResult<IngestionRecord>> Records(string? status, long? runId, int page = 1, int pageSize = 50, CancellationToken ct = default) => _svc.RecordsAsync(status, runId, Page(page, pageSize, null, null), ct);

    public sealed record ReplayRequest(System.Text.Json.JsonElement? CorrectedPayload, string? Reason);

    [HttpPost("ingestion-records/{id:long}/replay"), HasPermission(Perm.OperationsReplay)]
    public Task<IngestionRecordResult> Replay(long id, [FromBody] ReplayRequest r, [FromServices] IngestionService s, CancellationToken ct) => s.ReplayAsync(id, r.CorrectedPayload, r.Reason, ct);

    [HttpGet("duplicates"), HasPermission(Perm.OperationsRead)]
    public Task<object> Duplicates(CancellationToken ct) => _svc.DuplicatesAsync(ct);

    [HttpGet("data-quality"), HasPermission(Perm.OperationsRead)]
    public Task<PagedResult<DataQualityIssue>> DataQuality(string? type, string? status, int page = 1, int pageSize = 50, CancellationToken ct = default) => _svc.DataQualityAsync(type, status, Page(page, pageSize, null, null), ct);

    public sealed record ResolveRequest(string Note);

    [HttpPost("data-quality/{id:long}/resolve"), HasPermission(Perm.OperationsReplay)]
    public Task<object> Resolve(long id, [FromBody] ResolveRequest r, [FromServices] IAuditService audit, CancellationToken ct) => _svc.ResolveIssueAsync(id, r.Note, audit, ct);

    [HttpGet("screening-queue"), HasPermission(Perm.OperationsRead)]
    public Task<object> ScreeningQueue(CancellationToken ct) => _svc.ScreeningQueueAsync(ct);

    public sealed record ReconcileRequest(string? Source, DateOnly From, DateOnly To);

    [HttpPost("reconciliation"), HasPermission(Perm.OperationsRead)]
    public async Task<object> Reconcile([FromBody] ReconcileRequest r, [FromServices] IngestionService s, CancellationToken ct) =>
        (await s.ReconcileAsync(r.Source, r.From, r.To, ct)).Select(x => new { x.SourceSystem, x.BusinessDate, currency = x.TransactionType, x.SourceCount, x.SourceAmount, x.ImportedCount, x.ImportedAmount, x.CountDifference, x.AmountDifference, x.Status });
}
