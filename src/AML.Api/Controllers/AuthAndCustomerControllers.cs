using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using AML.Api.Infrastructure;
using AML.Application.Admin;
using AML.Application.Common;
using AML.Application.Customers;
using AML.Application.Fields;
using AML.Application.Governance;
using AML.Application.Risk;
using AML.Application.Screening;
using AML.Application.Transactions;
using AML.Domain.Entities;
using AML.Infrastructure;
using AML.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AML.Api.Controllers;

[ApiController]
public abstract class AmlController : ControllerBase
{
    protected static PageRequest Page(int page, int pageSize, string? sort, string? dir) => new() { Page = page, PageSize = pageSize, Sort = sort, Dir = dir };
}

public sealed record LoginRequest(string Username, string Password);
public sealed record ClientTokenRequest(string ClientId, string ClientSecret);

[Route("api/auth")]
public sealed class AuthController : AmlController
{
    private readonly AmlDbContext _db;
    private readonly ICryptoService _crypto;
    private readonly TokenService _tokens;
    private readonly IConfiguration _config;
    private readonly IAuditService _audit;

    public AuthController(AmlDbContext db, ICryptoService crypto, TokenService tokens, IConfiguration config, IAuditService audit)
    {
        _db = db; _crypto = crypto; _tokens = tokens; _config = config; _audit = audit;
    }

    /// <summary>DEVELOPMENT-ONLY local login (disabled unless DevAuth:Enabled=true). Production uses the enterprise OIDC provider.</summary>
    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest r, CancellationToken ct)
    {
        if (!string.Equals(_config["DevAuth:Enabled"], "true", StringComparison.OrdinalIgnoreCase)) return NotFound();
        var u = await _db.Set<AppUser>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Username == r.Username, ct);
        if (u is null || !u.IsActive || u.PasswordHash is null || !_crypto.VerifyPassword(r.Password ?? "", u.PasswordHash))
        {
            await _audit.RecordNowAsync("LOGIN_FAILED", "AppUser", r.Username, outcome: "Denied", ct: ct);
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid credentials" });
        }
        u.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var minutes = int.TryParse(_config["Auth:TokenMinutes"], out var m) ? m : 60;
        var (token, expires) = _tokens.Issue(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.Id.ToString()), new Claim("preferred_username", u.Username), new Claim("name", u.DisplayName),
            new Claim("tenant", u.TenantId.ToString()), new Claim("tv", u.TokenVersion.ToString()), new Claim("typ", "user"), new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        }, TimeSpan.FromMinutes(minutes));
        await _audit.RecordNowAsync("LOGIN", "AppUser", u.Username, ct: ct);
        return Ok(new { accessToken = token, expiresAt = expires, tokenType = "Bearer", devOnly = true });
    }

    /// <summary>Client-credentials token for source-system integrations.</summary>
    [AllowAnonymous, HttpPost("token")]
    public async Task<IActionResult> ClientToken([FromBody] ClientTokenRequest r, CancellationToken ct)
    {
        var s = await _db.Set<SourceSystem>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.ClientId == r.ClientId && x.IsActive, ct);
        if (s?.ClientSecretHash is null || !_crypto.VerifyPassword(r.ClientSecret ?? "", s.ClientSecretHash))
        {
            await _audit.RecordNowAsync("CLIENT_TOKEN_FAILED", "SourceSystem", r.ClientId, outcome: "Denied", ct: ct);
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid client credentials" });
        }
        var (token, expires) = _tokens.Issue(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "client:" + s.ClientId), new Claim("typ", "client"), new Claim("tenant", s.TenantId.ToString()), new Claim("source", s.Code)
        }, TimeSpan.FromMinutes(30));
        return Ok(new { accessToken = token, expiresAt = expires, tokenType = "Bearer" });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromServices] CurrentUser user, [FromServices] AdminService admin, CancellationToken ct)
    {
        if (user.UserId is { } id) await admin.RevokeSessionsAsync(id, ct);
        return NoContent();
    }

    [HttpGet("me")]
    public IActionResult Me([FromServices] CurrentUser u) => Ok(new
    {
        u.UserId, u.Username, u.TenantId, roles = u.Roles.OrderBy(x => x), permissions = u.Permissions.OrderBy(x => x), u.Team, u.DataScopeRegion
    });
}

[Route("api/customers")]
public sealed class CustomersController : AmlController
{
    private readonly CustomerService _svc;
    public CustomersController(CustomerService svc) => _svc = svc;

    [HttpGet, HasPermission(Perm.CustomersRead)]
    public Task<PagedResult<CustomerListItem>> List([FromQuery] CustomerQuery q, int page = 1, int pageSize = 25, string? sort = null, string? dir = null, CancellationToken ct = default) =>
        _svc.ListAsync(q, Page(page, pageSize, sort, dir), ct);

    [HttpPost("search"), HasPermission(Perm.CustomersRead)]
    public Task<List<CustomerListItem>> Search([FromBody] SensitiveSearchRequest r, CancellationToken ct) => _svc.SensitiveSearchAsync(r, ct);

    [HttpPost, HasPermission(Perm.CustomersWrite)]
    public async Task<IActionResult> Create([FromBody] CustomerCreateRequest r, CancellationToken ct)
    {
        var c = await _svc.CreateAsync(r, ct);
        return CreatedAtAction(nameof(Get), new { id = c.Id }, c);
    }

    [HttpGet("{id:long}"), HasPermission(Perm.CustomersRead)]
    public Task<CustomerDetailDto> Get(long id, CancellationToken ct) => _svc.GetAsync(id, ct);

    [HttpPut("{id:long}"), HasPermission(Perm.CustomersWrite)]
    public Task<CustomerDetailDto> Update(long id, [FromBody] CustomerUpdateRequest r, CancellationToken ct) => _svc.UpdateAsync(id, r, ct);

    public sealed record StatusRequest(string Status, string? Reason, string? RowVersion);

    [HttpPost("{id:long}/status"), HasPermission(Perm.CustomersWrite)]
    public Task<CustomerDetailDto> Status(long id, [FromBody] StatusRequest r, CancellationToken ct) => _svc.ChangeStatusAsync(id, r.Status, r.Reason, r.RowVersion, ct);

    [HttpGet("{id:long}/attributes"), HasPermission(Perm.CustomersRead)]
    public Task<List<AttributeValueDto>> Attributes(long id, DateTime? asOf, CancellationToken ct) => _svc.AttributesAsync(id, asOf, ct);

    [HttpPut("{id:long}/attributes"), HasPermission(Perm.CustomersWrite)]
    public async Task<object> PutAttributes(long id, [FromBody] AttributesUpdateRequest r, [FromServices] RiskAssessmentService risk, CancellationToken ct)
    {
        var (values, changes) = await _svc.UpdateAttributesAsync(id, r, ct);
        var reassessments = changes.Count > 0 ? await risk.OnFieldChangesAsync(id, changes, ct) : new();
        return new { values, reassessments = reassessments.Select(a => new { a.Id, a.Trigger, a.FinalLevel, a.CalculatedScore }) };
    }

    [HttpGet("{id:long}/attributes/history"), HasPermission(Perm.CustomersRead)]
    public Task<List<CustomerFieldChangeHistory>> AttributeHistory(long id, string? fieldCode, CancellationToken ct) => _svc.AttributeHistoryAsync(id, fieldCode, ct);

    [HttpPost("{id:long}/identifications"), HasPermission(Perm.CustomersWrite)]
    public Task<IdentificationDto> AddIdentification(long id, [FromBody] IdentificationInput i, CancellationToken ct) => _svc.AddIdentificationAsync(id, i, ct);

    [HttpPost("{id:long}/contacts"), HasPermission(Perm.CustomersWrite)]
    public Task<ContactDto> AddContact(long id, [FromBody] ContactInput i, CancellationToken ct) => _svc.AddContactAsync(id, i, ct);

    [HttpGet("{id:long}/financial-profiles"), HasPermission(Perm.CustomersRead)]
    public Task<List<FinancialProfileDto>> FinancialProfiles(long id, DateTime? asOf, CancellationToken ct) => _svc.FinancialProfilesAsync(id, asOf, ct);

    [HttpPost("{id:long}/financial-profiles"), HasPermission(Perm.CustomersWrite)]
    public Task<FinancialProfileDto> AddFinancialProfile(long id, [FromBody] FinancialProfileInput i, CancellationToken ct) => _svc.AddFinancialProfileAsync(id, i, ct);

    public sealed record DocumentRequest(string DocumentType, string? Reference, DateOnly? ExpiryDate);

    [HttpGet("{id:long}/documents"), HasPermission(Perm.CustomersRead)]
    public Task<List<CustomerDocument>> Documents(long id, CancellationToken ct) => _svc.DocumentsAsync(id, ct);

    [HttpPost("{id:long}/documents"), HasPermission(Perm.CustomersWrite)]
    public Task<CustomerDocument> AddDocument(long id, [FromBody] DocumentRequest r, CancellationToken ct) => _svc.AddDocumentMetadataAsync(id, r.DocumentType, r.Reference, r.ExpiryDate, ct);

    [HttpGet("{id:long}/ownership"), HasPermission(Perm.CustomersRead)]
    public Task<OwnershipAnalysisDto> Ownership(long id, CancellationToken ct) => _svc.OwnershipAsync(id, ct);

    [HttpPost("{id:long}/ownership"), HasPermission(Perm.CustomersWrite)]
    public Task<OwnershipLinkDto> AddOwnership(long id, [FromBody] OwnershipInput i, CancellationToken ct) => _svc.AddOwnershipAsync(id, i, ct);

    [HttpGet("{id:long}/relationships"), HasPermission(Perm.CustomersRead)]
    public Task<List<EntityLink>> Relationships(long id, CancellationToken ct) => _svc.RelationshipsAsync(id, ct);

    [HttpGet("{id:long}/history"), HasPermission(Perm.CustomersRead)]
    public Task<List<AuditLog>> History(long id, CancellationToken ct) => _svc.HistoryAsync(id, ct);

    [HttpGet("{id:long}/wallets"), HasPermission(Perm.CustomersRead)]
    public Task<List<WalletDto>> Wallets(long id, [FromServices] WalletService w, CancellationToken ct) => w.CustomerWalletsAsync(id, ct);

    [HttpGet("{id:long}/transactions"), HasPermission(Perm.TransactionsRead)]
    public Task<PagedResult<TransactionListItem>> Transactions(long id, [FromServices] TransactionQueryService t, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        t.ListAsync(new TransactionQuery { CustomerId = id }, Page(page, pageSize, null, null), ct);

    [HttpGet("{id:long}/risk-assessments"), HasPermission(Perm.RiskRead)]
    public Task<List<AssessmentDto>> Assessments(long id, [FromServices] RiskAssessmentService r, CancellationToken ct) => r.HistoryAsync(id, ct);

    public sealed record AssessRequest(string? Trigger, string? TriggerEventId, string? Reason, DateTime? AsOf);

    [HttpPost("{id:long}/risk-assessments"), HasPermission(Perm.RiskAssess)]
    public Task<AssessmentDto> Assess(long id, [FromBody] AssessRequest r, [FromServices] RiskAssessmentService risk, CancellationToken ct) =>
        risk.AssessAsync(id, r.Trigger ?? "Manual", r.TriggerEventId, r.Reason, r.AsOf, ct);

    [HttpGet("{id:long}/alerts"), HasPermission(Perm.AlertsRead)]
    public Task<PagedResult<AML.Application.Alerts.AlertListItem>> Alerts(long id, [FromServices] AML.Application.Alerts.AlertService a, CancellationToken ct) =>
        a.ListAsync(new AML.Application.Alerts.AlertQuery { CustomerId = id }, Page(1, 200, null, null), ct);

    [HttpGet("{id:long}/cases"), HasPermission(Perm.CasesRead)]
    public Task<PagedResult<AML.Application.Cases.CaseListItem>> Cases(long id, [FromServices] AML.Application.Cases.CaseService c, CancellationToken ct) =>
        c.ListAsync(null, null, id, Page(1, 200, null, null), ct);

    [HttpGet("{id:long}/screening"), HasPermission(Perm.ScreeningRead)]
    public async Task<object> Screening(long id, [FromServices] ScreeningService s, CancellationToken ct)
    {
        await _svc.LoadVisibleAsync(id, ct);
        return await s.CustomerScreeningAsync(id, ct);
    }

    [HttpPost("{id:long}/screening"), HasPermission(Perm.ScreeningRun)]
    public async Task<object> Screen(long id, [FromServices] ScreeningService s, [FromQuery] string? idempotencyKey, CancellationToken ct)
    {
        var reqs = await s.ScreenCustomerAsync(id, "Manual", idempotencyKey, ct);
        return reqs.Select(r => new { r.Id, r.SubjectType, r.SubjectId, r.Status, r.Attempts, r.LastError, r.ResultCount, r.SuppressedCount });
    }

    [HttpGet("{id:long}/device-signals"), HasPermission(Perm.EntityRead)]
    public Task<object> DeviceSignals(long id, [FromServices] EntityResolutionService e, CancellationToken ct) => e.DeviceSignalsAsync(id, ct);

    public sealed record DeviceSignalRequest(string DeviceId, string? IpAddress, string EventType, DateTime? OccurredAt);

    [HttpPost("{id:long}/device-signals"), HasPermission(Perm.CustomersWrite)]
    public async Task<object> AddDeviceSignal(long id, [FromBody] DeviceSignalRequest r, [FromServices] EntityResolutionService e, CancellationToken ct)
    {
        var s = await e.AddSignalAsync(id, r.DeviceId, r.IpAddress, r.EventType, r.OccurredAt, ct);
        return new { s.Id };
    }

    [HttpGet("{id:long}/graph"), HasPermission(Perm.EntityRead)]
    public Task<object> Graph(long id, [FromServices] EntityResolutionService e, int depth = 2, CancellationToken ct = default) => e.GraphAsync(id, depth, ct);

    public sealed record BulkAssignRequest(List<long> CustomerIds, string Officer, string? Reason);

    [HttpPost("bulk-assign"), HasPermission(Perm.CustomersBulk)]
    public async Task<object> BulkAssign([FromBody] BulkAssignRequest r, CancellationToken ct) => new { updated = await _svc.BulkAssignAsync(r.CustomerIds, r.Officer, r.Reason, ct) };

    // ----- CDD -----
    [HttpPost("{id:long}/onboard"), HasPermission(Perm.CddDecide)]
    public Task<OnboardingResult> Onboard(long id, [FromServices] CddService cdd, CancellationToken ct) => cdd.OnboardAsync(id, ct);

    [HttpGet("{id:long}/acceptance"), HasPermission(Perm.CustomersRead)]
    public Task<List<AcceptanceDecisionDto>> Decisions(long id, [FromServices] CddService cdd, CancellationToken ct) => cdd.DecisionsAsync(id, ct);

    [HttpPost("{id:long}/acceptance/decide"), HasPermission(Perm.CddDecide)]
    public Task<AcceptanceDecisionDto> Decide(long id, [FromServices] CddService cdd, CancellationToken ct) => cdd.DecideAsync(id, ct);

    [HttpPost("{id:long}/acceptance/manual"), HasPermission(Perm.CddDecide)]
    public Task<AcceptanceDecisionDto> ManualDecision(long id, [FromBody] ManualDecisionRequest r, [FromServices] CddService cdd, CancellationToken ct) => cdd.ManualDecisionAsync(id, r, ct);

    public sealed record ApproveRequest(bool Approve, string? Reason);

    [HttpPost("{id:long}/acceptance/senior-approval"), HasPermission(Perm.CddSeniorApprove)]
    public Task<AcceptanceDecisionDto> SeniorApproval(long id, [FromBody] ApproveRequest r, [FromServices] CddService cdd, CancellationToken ct) => cdd.SeniorApproveAsync(id, r.Approve, r.Reason, ct);

    [HttpPost("{id:long}/due-diligence/assign"), HasPermission(Perm.CddDecide)]
    public async Task<object> AssignDd(long id, [FromServices] CddService cdd, CancellationToken ct) => new { level = await cdd.AssignDueDiligenceLevelAsync(id, ct) };

    [HttpGet("{id:long}/edd-tasks"), HasPermission(Perm.CustomersRead)]
    public Task<List<EddTask>> EddTasks(long id, [FromServices] CddService cdd, CancellationToken ct) => cdd.EddTasksAsync(id, ct);

    public sealed record EvidenceRequest(string? Evidence);

    [HttpPost("edd-tasks/{taskId:long}/complete"), HasPermission(Perm.CddDecide)]
    public Task<EddTask> CompleteEdd(long taskId, [FromBody] EvidenceRequest r, [FromServices] CddService cdd, CancellationToken ct) => cdd.CompleteEddTaskAsync(taskId, r.Evidence, ct);

    public sealed record ExitRequest(string ReasonCode, string? InternalReason, long? LinkedCaseId, string? Evidence);

    [HttpGet("{id:long}/exit"), HasPermission(Perm.CustomersRead)]
    public Task<List<object>> Exits(long id, [FromServices] CddService cdd, CancellationToken ct) => cdd.ExitsAsync(id, ct);

    [HttpPost("{id:long}/exit"), HasPermission(Perm.CustomersExit)]
    public Task<object> RequestExit(long id, [FromBody] ExitRequest r, [FromServices] CddService cdd, CancellationToken ct) => cdd.RequestExitAsync(id, r.ReasonCode, r.InternalReason, r.LinkedCaseId, r.Evidence, ct);

    [HttpPost("exit/{exitId:long}/decide"), HasPermission(Perm.CustomersExitApprove)]
    public Task<object> DecideExit(long exitId, [FromBody] ApproveRequest r, [FromServices] CddService cdd, CancellationToken ct) => cdd.DecideExitAsync(exitId, r.Approve, r.Reason, ct);
}

[Route("api/config")]
public sealed class ConfigController : AmlController
{
    [HttpGet("customer-fields"), HasPermission(Perm.CustomersRead)]
    public Task<FormMetadataDto> Fields([FromQuery] string customerType, DateTime? asOf, [FromServices] FieldConfigService f, CancellationToken ct) => f.FormMetadataAsync(customerType, asOf, ct);

    [HttpGet("lookups"), HasPermission(Perm.DashboardRead)]
    public Task<List<LookupValue>> Lookups(string? category, [FromServices] AdminService a, CancellationToken ct) => a.LookupsAsync(category, ct);

    [HttpGet("risk-data-sources"), HasPermission(Perm.RiskRead)]
    public async Task<object> DataSources([FromServices] DependencyService d, CancellationToken ct) =>
        new { registry = RiskDataSourceProvider.Static, dynamicFields = (await d.PublishedFieldTypesAsync(ct)).Select(kv => new { code = "field:" + kv.Key, type = kv.Value }) };

    [HttpGet("rule-fields"), HasPermission(Perm.RulesRead)]
    public async Task<object> RuleFields([FromServices] DependencyService d, CancellationToken ct) => new
    {
        fields = AML.Domain.Rules.RuleFieldRegistry.Fields.Select(kv => new { code = kv.Key, type = kv.Value, operators = AML.Domain.Rules.RuleFieldRegistry.OperatorsFor(kv.Value) }),
        dynamicFields = (await d.PublishedFieldTypesAsync(ct)).Select(kv => new { code = "field:" + kv.Key, type = AML.Domain.Rules.RuleFieldRegistry.Resolve("field:" + kv.Key, new Dictionary<string, string> { [kv.Key] = kv.Value }) }),
        aggregates = AML.Domain.Rules.RuleAggregates.All, operators = AML.Domain.Rules.RuleOperators.All
    };
}

[Route("api/me")]
public sealed class MeController : AmlController
{
    [HttpGet("preferences/{key}")]
    public Task<object> Get(string key, [FromServices] AdminService a, CancellationToken ct) => a.UserPreferenceAsync(key, ct);

    public sealed record PrefRequest(string Value);

    [HttpPut("preferences/{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] PrefRequest r, [FromServices] AdminService a, CancellationToken ct)
    {
        await a.SetUserPreferenceAsync(key, r.Value, ct);
        return NoContent();
    }
}
