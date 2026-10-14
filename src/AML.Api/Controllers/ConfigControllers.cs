using System.Text.Json;
using AML.Api.Infrastructure;
using AML.Application.Common;
using AML.Application.Fields;
using AML.Application.Risk;
using AML.Application.Rules;
using AML.Application.Transactions;
using AML.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace AML.Api.Controllers;

[Route("api/admin/customer-fields")]
public sealed class AdminFieldsController : AmlController
{
    private readonly FieldConfigService _svc;
    public AdminFieldsController(FieldConfigService svc) => _svc = svc;

    [HttpGet, HasPermission(Perm.FieldsConfig)]
    public Task<List<FieldDefinitionDto>> List(string? code, string? status, CancellationToken ct) => _svc.ListAsync(code, status, ct);

    [HttpGet("{id:long}"), HasPermission(Perm.FieldsConfig)]
    public Task<FieldDefinitionDto> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    [HttpPost, HasPermission(Perm.FieldsConfig)]
    public Task<FieldDefinitionDto> Create([FromBody] FieldDefinitionInput i, CancellationToken ct) => _svc.CreateAsync(i, ct);

    [HttpPut("{id:long}"), HasPermission(Perm.FieldsConfig)]
    public Task<FieldDefinitionDto> Update(long id, [FromBody] FieldDefinitionInput i, CancellationToken ct) => _svc.UpdateDraftAsync(id, i, ct);

    [HttpPost("{id:long}/new-version"), HasPermission(Perm.FieldsConfig)]
    public Task<FieldDefinitionDto> NewVersion(long id, CancellationToken ct) => _svc.NewVersionAsync(id, ct);

    public sealed record PublishRequest(string? RowVersion);

    [HttpPost("{id:long}/publish"), HasPermission(Perm.FieldsConfig)]
    public Task<FieldDefinitionDto> Publish(long id, [FromBody] PublishRequest? r, CancellationToken ct) => _svc.PublishAsync(id, r?.RowVersion, ct);

    [HttpPost("{id:long}/approve"), HasPermission(Perm.FieldsApprove)]
    public Task<FieldDefinitionDto> Approve(long id, CancellationToken ct) => _svc.ApproveAsync(id, ct);

    public sealed record ReasonRequest(string? Reason);

    [HttpPost("{id:long}/deactivate"), HasPermission(Perm.FieldsConfig)]
    public Task<FieldDefinitionDto> Deactivate(long id, [FromBody] ReasonRequest? r, CancellationToken ct) => _svc.DeactivateAsync(id, r?.Reason, ct);

    [HttpGet("{code}/where-used"), HasPermission(Perm.FieldsConfig)]
    public Task<List<WhereUsedItem>> WhereUsed(string code, CancellationToken ct) => _svc.WhereUsedAsync(code, ct);

    [HttpGet("{code}/options"), HasPermission(Perm.FieldsConfig)]
    public Task<List<FieldOptionDto>> Options(string code, bool includeInactive = true, CancellationToken ct = default) => _svc.OptionsAsync(code, includeInactive, ct);

    public sealed record OptionRequest(string OptionCode, string LabelEn, string? LabelAr, int SortOrder);

    [HttpPost("{code}/options"), HasPermission(Perm.FieldsConfig)]
    public Task<FieldOptionDto> AddOption(string code, [FromBody] OptionRequest r, CancellationToken ct) => _svc.AddOptionAsync(code, r.OptionCode, r.LabelEn, r.LabelAr, r.SortOrder, ct);

    public sealed record OptionActiveRequest(bool Active);

    [HttpPost("options/{optionId:long}/active"), HasPermission(Perm.FieldsConfig)]
    public Task<FieldOptionDto> SetOptionActive(long optionId, [FromBody] OptionActiveRequest r, CancellationToken ct) => _svc.SetOptionActiveAsync(optionId, r.Active, ct);

    [HttpGet("{code}/options/{option}/where-used"), HasPermission(Perm.FieldsConfig)]
    public Task<List<WhereUsedItem>> OptionWhereUsed(string code, string option, CancellationToken ct) => _svc.OptionWhereUsedAsync(code, option, ct);

    [HttpGet("/api/admin/field-groups"), HasPermission(Perm.FieldsConfig)]
    public Task<List<CustomerFieldGroup>> Groups(CancellationToken ct) => _svc.GroupsAsync(ct);

    public sealed record GroupRequest(string Code, string LabelEn, string? LabelAr, int SortOrder);

    [HttpPost("/api/admin/field-groups"), HasPermission(Perm.FieldsConfig)]
    public Task<CustomerFieldGroup> UpsertGroup([FromBody] GroupRequest r, CancellationToken ct) => _svc.UpsertGroupAsync(r.Code, r.LabelEn, r.LabelAr, r.SortOrder, ct);

    [HttpGet("/api/admin/form-layouts"), HasPermission(Perm.LayoutsConfig)]
    public Task<List<CustomerFormLayout>> Layouts(CancellationToken ct) => _svc.LayoutsAsync(ct);

    public sealed record LayoutRequest(string CustomerType, string RoleCode, JsonElement Layout);

    [HttpPost("/api/admin/form-layouts"), HasPermission(Perm.LayoutsConfig)]
    public Task<CustomerFormLayout> SaveLayout([FromBody] LayoutRequest r, CancellationToken ct) => _svc.SaveLayoutAsync(r.CustomerType, r.RoleCode, r.Layout.GetRawText(), ct);
}

[Route("api/admin/risk-methodologies")]
public sealed class RiskAdminController : AmlController
{
    private readonly RiskConfigService _svc;
    public RiskAdminController(RiskConfigService svc) => _svc = svc;

    [HttpGet, HasPermission(Perm.RiskRead)]
    public Task<List<MethodologyDto>> List(CancellationToken ct) => _svc.ListAsync(ct);

    [HttpGet("{id:long}"), HasPermission(Perm.RiskRead)]
    public Task<MethodologyDto> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    [HttpGet("history/{code}"), HasPermission(Perm.RiskRead)]
    public Task<List<MethodologyDto>> History(string code, CancellationToken ct) => _svc.HistoryAsync(code, ct);

    [HttpPost, HasPermission(Perm.RiskConfig)]
    public Task<MethodologyDto> Create([FromBody] MethodologyInput i, CancellationToken ct) => _svc.CreateAsync(i, ct);

    [HttpPut("{id:long}"), HasPermission(Perm.RiskConfig)]
    public Task<MethodologyDto> Update(long id, [FromBody] MethodologyInput i, CancellationToken ct) => _svc.UpdateAsync(id, i, ct);

    [HttpPost("{id:long}/new-version"), HasPermission(Perm.RiskConfig)]
    public Task<MethodologyDto> NewVersion(long id, CancellationToken ct) => _svc.NewVersionAsync(id, ct);

    [HttpPost("{id:long}/factors"), HasPermission(Perm.RiskConfig)]
    public Task<FactorDto> AddFactor(long id, [FromBody] FactorInputDto f, CancellationToken ct) => _svc.AddFactorAsync(id, f, ct);

    [HttpPut("/api/admin/risk-factors/{factorId:long}"), HasPermission(Perm.RiskConfig)]
    public Task<FactorDto> UpdateFactor(long factorId, [FromBody] FactorInputDto f, CancellationToken ct) => _svc.UpdateFactorAsync(factorId, f, ct);

    [HttpDelete("/api/admin/risk-factors/{factorId:long}"), HasPermission(Perm.RiskConfig)]
    public async Task<IActionResult> DeleteFactor(long factorId, CancellationToken ct) { await _svc.DeleteFactorAsync(factorId, ct); return NoContent(); }

    [HttpPut("{id:long}/thresholds"), HasPermission(Perm.RiskConfig)]
    public Task<MethodologyDto> Thresholds(long id, [FromBody] List<ThresholdInput> t, CancellationToken ct) => _svc.SetThresholdsAsync(id, t, ct);

    [HttpPost("{id:long}/overrides"), HasPermission(Perm.RiskConfig)]
    public Task<MethodologyDto> AddOverride(long id, [FromBody] OverrideInput o, CancellationToken ct) => _svc.AddOverrideAsync(id, o, ct);

    [HttpDelete("{id:long}/overrides/{overrideId:long}"), HasPermission(Perm.RiskConfig)]
    public Task<MethodologyDto> RemoveOverride(long id, long overrideId, CancellationToken ct) => _svc.RemoveOverrideAsync(id, overrideId, ct);

    public sealed record FloorRequest(string CategoryCode, string TriggerLevel, string MinimumLevel);

    [HttpPost("{id:long}/floors"), HasPermission(Perm.RiskConfig)]
    public Task<MethodologyDto> AddFloor(long id, [FromBody] FloorRequest f, CancellationToken ct) => _svc.AddFloorAsync(id, f.CategoryCode, f.TriggerLevel, f.MinimumLevel, ct);

    [HttpPost("{id:long}/validate"), HasPermission(Perm.RiskConfig)]
    public async Task<object> Validate(long id, CancellationToken ct) { var e = await _svc.ValidateAsync(id, ct); return new { valid = e.Count == 0, errors = e }; }

    [HttpPost("{id:long}/simulate"), HasPermission(Perm.RiskConfig)]
    public Task<SimulationResultDto> Simulate(long id, [FromBody] SimulationRequest r, CancellationToken ct) => _svc.SimulateAsync(id, r, ct);

    public sealed record RationaleRequest(string? Rationale);

    [HttpPost("{id:long}/submit"), HasPermission(Perm.RiskConfig)]
    public Task<MethodologyDto> Submit(long id, [FromBody] RationaleRequest? r, CancellationToken ct) => _svc.SubmitAsync(id, r?.Rationale, ct);

    [HttpPost("{id:long}/approve"), HasPermission(Perm.RiskApprove)]
    public Task<MethodologyDto> Approve(long id, CancellationToken ct) => _svc.ApproveAsync(id, ct);

    [HttpPost("{id:long}/publish"), HasPermission(Perm.RiskConfig)]
    public Task<MethodologyDto> Publish(long id, [FromBody] RationaleRequest? r, CancellationToken ct) => _svc.PublishAsync(id, r?.Rationale, ct);

    public sealed record RejectRequest(string Reason);

    [HttpPost("{id:long}/reject"), HasPermission(Perm.RiskApprove)]
    public Task<MethodologyDto> Reject(long id, [FromBody] RejectRequest r, CancellationToken ct) => _svc.RejectAsync(id, r.Reason, ct);

    [HttpGet("/api/admin/risk-categories"), HasPermission(Perm.RiskRead)]
    public Task<List<RiskFactorCategory>> Categories(CancellationToken ct) => _svc.CategoriesAsync(ct);

    public sealed record CategoryRequest(string Code, string Name, string? NameAr);

    [HttpPost("/api/admin/risk-categories"), HasPermission(Perm.RiskConfig)]
    public Task<RiskFactorCategory> AddCategory([FromBody] CategoryRequest r, CancellationToken ct) => _svc.AddCategoryAsync(r.Code, r.Name, r.NameAr, ct);
}

[Route("api/risk")]
public sealed class RiskController : AmlController
{
    private readonly RiskAssessmentService _svc;
    public RiskController(RiskAssessmentService svc) => _svc = svc;

    [HttpGet("assessments"), HasPermission(Perm.RiskRead)]
    public Task<PagedResult<AssessmentDto>> List(string? status, string? level, int page = 1, int pageSize = 25, CancellationToken ct = default) => _svc.ListAsync(status, level, Page(page, pageSize, null, null), ct);

    [HttpGet("assessments/{id:long}"), HasPermission(Perm.RiskRead)]
    public Task<AssessmentDto> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    public sealed record DecisionRequest(bool Approve, string? Note);

    [HttpPost("assessments/{id:long}/decision"), HasPermission(Perm.RiskApprove)]
    public Task<AssessmentDto> Decide(long id, [FromBody] DecisionRequest r, CancellationToken ct) => _svc.DecideApprovalAsync(id, r.Approve, r.Note, ct);

    public sealed record OverrideRequestBody(long CustomerId, string RequestedLevel, string? Reason, string? Evidence);

    [HttpGet("overrides"), HasPermission(Perm.RiskRead)]
    public Task<List<OverrideRequestDto>> Overrides(long? customerId, string? status, CancellationToken ct) => _svc.OverridesAsync(customerId, status, ct);

    [HttpPost("overrides"), HasPermission(Perm.RiskOverrideRequest)]
    public Task<OverrideRequestDto> RequestOverride([FromBody] OverrideRequestBody r, CancellationToken ct) => _svc.RequestOverrideAsync(r.CustomerId, r.RequestedLevel, r.Reason, r.Evidence, ct);

    public sealed record OverrideDecision(bool Approve, string? Reason);

    [HttpPost("overrides/{id:long}/decision"), HasPermission(Perm.RiskOverrideApprove)]
    public Task<OverrideRequestDto> DecideOverride(long id, [FromBody] OverrideDecision r, CancellationToken ct) => _svc.DecideOverrideAsync(id, r.Approve, r.Reason, ct);

    [HttpGet("review-tasks"), HasPermission(Perm.RiskRead)]
    public Task<List<RiskReviewTask>> Tasks(long? customerId, string? status, bool? overdue, CancellationToken ct) => _svc.ReviewTasksAsync(customerId, status, overdue, ct);

    public sealed record CompleteRequest(string? Evidence);

    [HttpPost("review-tasks/{id:long}/complete"), HasPermission(Perm.RiskReviewWork)]
    public Task<RiskReviewTask> Complete(long id, [FromBody] CompleteRequest r, CancellationToken ct) => _svc.CompleteReviewAsync(id, r.Evidence, ct);
}

[Route("api/admin/aml-rules")]
public sealed class RulesController : AmlController
{
    private readonly RuleService _svc;
    public RulesController(RuleService svc) => _svc = svc;

    [HttpGet, HasPermission(Perm.RulesRead)]
    public Task<List<RuleDto>> List(CancellationToken ct) => _svc.ListAsync(ct);

    [HttpGet("{id:long}"), HasPermission(Perm.RulesRead)]
    public Task<RuleDto> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    [HttpPost, HasPermission(Perm.RulesConfig)]
    public Task<RuleDto> Create([FromBody] RuleCreateRequest r, CancellationToken ct) => _svc.CreateAsync(r, ct);

    [HttpPut("{id:long}/versions/{versionId:long}"), HasPermission(Perm.RulesConfig)]
    public Task<RuleVersionDto> UpdateDraft(long id, long versionId, [FromBody] RuleVersionUpdateRequest r, CancellationToken ct) => _svc.UpdateDraftAsync(id, versionId, r, ct);

    [HttpPost("{id:long}/versions"), HasPermission(Perm.RulesConfig)]
    public Task<RuleVersionDto> NewVersion(long id, CancellationToken ct) => _svc.NewVersionAsync(id, ct);

    public sealed record ValidateRequest(long? VersionId, JsonElement? Definition);

    [HttpPost("{id:long}/validate"), HasPermission(Perm.RulesConfig)]
    public Task<ValidationResultDto> Validate(long id, [FromBody] ValidateRequest? r, CancellationToken ct) => _svc.ValidateAsync(id, r?.VersionId, r?.Definition, ct);

    [HttpPost("{id:long}/simulate"), HasPermission(Perm.RulesConfig)]
    public Task<RuleRunResult> Simulate(long id, [FromBody] RuleWindowRequest r, CancellationToken ct) => _svc.SimulateAsync(id, r, ct);

    [HttpPost("{id:long}/submit"), HasPermission(Perm.RulesConfig)]
    public Task<RuleVersionDto> Submit(long id, [FromBody] RuleSubmitRequest r, CancellationToken ct) => _svc.SubmitAsync(id, r, ct);

    public sealed record CommentRequest(string? Comment);

    [HttpPost("{id:long}/approve"), HasPermission(Perm.RulesApprove)]
    public Task<RuleVersionDto> Approve(long id, [FromBody] CommentRequest? r, CancellationToken ct) => _svc.ApproveAsync(id, r?.Comment, ct);

    [HttpPost("{id:long}/reject"), HasPermission(Perm.RulesApprove)]
    public Task<RuleVersionDto> Reject(long id, [FromBody] CommentRequest r, CancellationToken ct) => _svc.RejectAsync(id, r.Comment ?? "", ct);

    [HttpPost("{id:long}/publish"), HasPermission(Perm.RulesConfig)]
    public Task<RuleVersionDto> Publish(long id, CancellationToken ct) => _svc.PublishAsync(id, ct);

    public sealed record EnableRequest(bool Enabled, string? Reason);

    [HttpPost("{id:long}/enabled"), HasPermission(Perm.RulesConfig)]
    public Task<RuleVersionDto> Enable(long id, [FromBody] EnableRequest r, CancellationToken ct) => _svc.SetEnabledAsync(id, r.Enabled, r.Reason, ct);

    [HttpPost("{id:long}/run"), HasPermission(Perm.RulesRun)]
    public Task<RuleRunResult> Run(long id, [FromBody] RuleWindowRequest r, CancellationToken ct) => _svc.RunAsync(id, r, ct);

    [HttpPost("{id:long}/run-backtest"), HasPermission(Perm.RulesConfig)]
    public Task<RuleRunResult> Backtest(long id, [FromBody] RuleWindowRequest r, CancellationToken ct) => _svc.BacktestAsync(id, r, ct);

    [HttpGet("executions"), HasPermission(Perm.RulesRead)]
    public Task<List<AmlRuleExecution>> Executions(long? ruleId, int take = 100, CancellationToken ct = default) => _svc.ExecutionsAsync(ruleId, take, ct);

    [HttpPost("{id:long}/tuning"), HasPermission(Perm.TuningRun)]
    public Task<TuningRun> Tune(long id, [FromBody] TuningRequest r, CancellationToken ct) => _svc.TuneAsync(id, r, ct);

    [HttpGet("{id:long}/tuning"), HasPermission(Perm.RulesRead)]
    public Task<List<TuningRun>> TuningRuns(long id, CancellationToken ct) => _svc.TuningRunsAsync(id, ct);
}

[Route("api")]
public sealed class TransactionsController : AmlController
{
    [HttpGet("transactions"), HasPermission(Perm.TransactionsRead)]
    public Task<PagedResult<TransactionListItem>> List([FromQuery] TransactionQuery q, [FromServices] TransactionQueryService s, int page = 1, int pageSize = 25, string? sort = null, string? dir = null, CancellationToken ct = default) =>
        s.ListAsync(q, Page(page, pageSize, sort, dir), ct);

    [HttpGet("transactions/{id:long}"), HasPermission(Perm.TransactionsRead)]
    public Task<object> Get(long id, [FromServices] TransactionQueryService s, CancellationToken ct) => s.GetAsync(id, ct);

    [HttpGet("transactions/{id:long}/related"), HasPermission(Perm.TransactionsRead)]
    public Task<object> Related(long id, [FromServices] TransactionQueryService s, CancellationToken ct) => s.RelatedAsync(id, ct);

    [HttpGet("transactions/{id:long}/raw"), HasPermission(Perm.TransactionsRaw)]
    public Task<object> Raw(long id, [FromServices] TransactionQueryService s, CancellationToken ct) => s.RawPayloadAsync(id, ct);

    /// <summary>Secured ingestion contract for a source system (client credentials, source must match the token).</summary>
    [HttpPost("integrations/{source}/transactions"), HasPermission(Perm.IntegrationIngest)]
    public async Task<IActionResult> Ingest(string source, [FromBody] IngestionBatchRequest r, [FromServices] IngestionService s, [FromServices] ICurrentUser user, CancellationToken ct)
    {
        if (user.SourceSystem is not null && !string.Equals(user.SourceSystem, source, StringComparison.OrdinalIgnoreCase))
            return StatusCode(403, new ProblemDetails { Status = 403, Title = "Forbidden", Detail = "Client is not authorized for this source system." });
        return Ok(await s.IngestAsync(source, r, ct));
    }

    [HttpPost("realtime/transactions/decide"), HasPermission(Perm.RealtimeDecide)]
    public Task<AML.Application.Realtime.DecideResponse> Decide([FromBody] AML.Application.Realtime.DecideRequest r, [FromServices] AML.Application.Realtime.RealtimeService s, CancellationToken ct) => s.DecideAsync(r, ct);

    [HttpGet("admin/wallet-mappings"), HasPermission(Perm.AdminWalletMappings)]
    public Task<List<WalletMapping>> Mappings([FromServices] WalletService w, CancellationToken ct) => w.MappingsAsync(ct);

    [HttpPost("admin/wallet-mappings"), HasPermission(Perm.AdminWalletMappings)]
    public Task<WalletMapping> CreateMapping([FromBody] WalletMappingInput i, [FromServices] WalletService w, CancellationToken ct) => w.CreateMappingAsync(i, ct);

    [HttpPut("admin/wallet-mappings/{id:long}"), HasPermission(Perm.AdminWalletMappings)]
    public Task<WalletMapping> UpdateMapping(long id, [FromBody] WalletMappingInput i, [FromServices] WalletService w, CancellationToken ct) => w.UpdateMappingAsync(id, i, ct);

    [HttpPost("wallets"), HasPermission(Perm.CustomersWrite)]
    public Task<WalletDto> CreateWallet([FromBody] WalletInput i, [FromServices] WalletService w, CancellationToken ct) => w.CreateAsync(i, ct);
}
