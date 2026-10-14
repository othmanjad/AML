using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using AML.Application.Common;
using AML.Domain.Entities;
using AML.Infrastructure;
using AML.Infrastructure.Persistence;
using AML.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog.Context;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace AML.Api.Infrastructure;

/// <summary>Requires a granular permission (module.action) evaluated server-side on every request.</summary>
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public HasPermissionAttribute(string permission) : base("perm:" + permission) => Permission = permission;
    public string Permission { get; }
}

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission) => Permission = permission;
    public string Permission { get; }
}

public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;
    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options) => _fallback = new DefaultAuthorizationPolicyProvider(options);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith("perm:", StringComparison.Ordinal))
            return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(policyName[5..])).Build());
        return _fallback.GetPolicyAsync(policyName);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IHttpContextAccessor _http;
    public PermissionHandler(IHttpContextAccessor http) => _http = http;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = _http.HttpContext?.RequestServices.GetRequiredService<CurrentUser>();
        if (user is { IsAuthenticated: true } && user.Has(requirement.Permission)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Audits every denied (403) attempt and returns problem details.</summary>
public sealed class AuditingAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            var audit = context.RequestServices.GetRequiredService<IAuditService>();
            var required = policy.Requirements.OfType<PermissionRequirement>().Select(r => r.Permission).ToList();
            await audit.RecordNowAsync("ACCESS_DENIED", "Endpoint", $"{context.Request.Method} {context.Request.Path}", null, new { required }, outcome: "Denied");
            await ProblemResults.WriteAsync(context, StatusCodes.Status403Forbidden, "Forbidden", $"Missing required permission: {string.Join(", ", required)}.");
            return;
        }
        if (authorizeResult.Challenged)
        {
            await ProblemResults.WriteAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized", "Authentication is required.");
            return;
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

public static class ProblemResults
{
    public static async Task WriteAsync(HttpContext ctx, int status, string title, string detail, IDictionary<string, object?>? extra = null)
    {
        var corr = ctx.RequestServices.GetService<ICorrelationContext>()?.CorrelationId;
        var pd = new ProblemDetails { Status = status, Title = title, Detail = detail, Type = $"https://httpstatuses.io/{status}", Instance = ctx.Request.Path };
        pd.Extensions["correlationId"] = corr;
        pd.Extensions["traceId"] = Activity.Current?.Id ?? ctx.TraceIdentifier;
        if (extra is not null) foreach (var (k, v) in extra) pd.Extensions[k] = v;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsJsonAsync(pd, options: null, contentType: "application/problem+json");
    }
}

public sealed class CorrelationMiddleware
{
    private static readonly Regex Valid = new("^[A-Za-z0-9\\-_.]{1,64}$", RegexOptions.Compiled);
    private readonly RequestDelegate _next;
    public CorrelationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, ICorrelationContext corr)
    {
        var incoming = ctx.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        corr.CorrelationId = incoming is not null && Valid.IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        corr.ClientIp = ctx.Connection.RemoteIpAddress?.ToString();
        ctx.Response.Headers["X-Correlation-ID"] = corr.CorrelationId;
        var id = corr.CorrelationId;
        // Re-applied on start so error responses (which clear headers) still carry the correlation ID.
        ctx.Response.OnStarting(() => { ctx.Response.Headers["X-Correlation-ID"] = id; return Task.CompletedTask; });
        using (LogContext.PushProperty("CorrelationId", corr.CorrelationId))
            await _next(ctx);
    }
}

/// <summary>Populates the per-request security context from the validated token and the current role/permission configuration.</summary>
public sealed class CurrentUserMiddleware
{
    private readonly RequestDelegate _next;
    public CurrentUserMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, CurrentUser user, AmlDbContext db)
    {
        var p = ctx.User;
        if (p.Identity?.IsAuthenticated == true)
        {
            var kind = p.FindFirstValue("typ");
            if (kind == "client")
            {
                user.IsAuthenticated = true;
                user.Username = "client:" + p.FindFirstValue("source");
                user.TenantId = long.Parse(p.FindFirstValue("tenant")!);
                user.SourceSystem = p.FindFirstValue("source");
                user.RoleSet = new HashSet<string> { RoleCodes.IntegrationClient };
                user.PermissionSet = RoleCodes.Defaults[RoleCodes.IntegrationClient].ToHashSet();
            }
            else
            {
                var sub = p.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? p.FindFirstValue(ClaimTypes.NameIdentifier);
                var username = p.FindFirstValue("preferred_username") ?? p.FindFirstValue(ClaimTypes.Name) ?? p.FindFirstValue("name");
                AppUser? u = null;
                if (long.TryParse(sub, out var id) && p.HasClaim(c => c.Type == "tv"))
                    u = await db.Set<AppUser>().IgnoreQueryFilters().AsNoTracking().Include(x => x.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions).ThenInclude(x => x.Permission).AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id);
                else if (sub is not null)
                    u = await db.Set<AppUser>().IgnoreQueryFilters().AsNoTracking().Include(x => x.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions).ThenInclude(x => x.Permission).AsSplitQuery().FirstOrDefaultAsync(x => x.ExternalSubject == sub || x.Username == username);
                var tv = p.FindFirstValue("tv");
                if (u is null || !u.IsActive || (tv is not null && tv != u.TokenVersion.ToString()))
                {
                    await ProblemResults.WriteAsync(ctx, 401, "Unauthorized", "Session is expired, revoked or the account is inactive.");
                    return;
                }
                user.IsAuthenticated = true;
                user.UserId = u.Id;
                user.Username = u.Username;
                user.TenantId = u.TenantId;
                user.Team = u.Team;
                user.DataScopeRegion = u.DataScopeRegion;
                user.RoleSet = u.Roles.Select(r => r.Role!.Code).ToHashSet();
                user.PermissionSet = u.Roles.SelectMany(r => r.Role!.Permissions.Select(x => x.Permission!.Code)).ToHashSet();
            }
        }
        using (LogContext.PushProperty("User", user.Username))
            await _next(ctx);
    }
}

public sealed class TokenService
{
    private readonly IConfiguration _config;
    public TokenService(IConfiguration config) => _config = config;

    public static SymmetricSecurityKey Key(IConfiguration c)
    {
        var k = c["Auth:SigningKey"];
        if (string.IsNullOrWhiteSpace(k) || k.Length < 32) throw new InvalidOperationException("Auth:SigningKey (>=32 chars) must be provided via environment/secret manager.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k));
    }

    public (string Token, DateTime Expires) Issue(IEnumerable<Claim> claims, TimeSpan lifetime)
    {
        var expires = DateTime.UtcNow.Add(lifetime);
        var token = new JwtSecurityToken(_config["Auth:Issuer"] ?? "aml-dev", _config["Auth:Audience"] ?? "aml-api", claims, DateTime.UtcNow, expires,
            new SigningCredentials(Key(_config), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

public static class ExceptionMapping
{
    public static void UseAmlExceptionHandler(this IApplicationBuilder app) => app.UseExceptionHandler(b => b.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var log = ctx.RequestServices.GetRequiredService<ILogger<CorrelationMiddleware>>();
        switch (ex)
        {
            case ValidationException v:
                await ProblemResults.WriteAsync(ctx, 400, "Validation failed", v.Message, new Dictionary<string, object?> { ["errors"] = v.Errors });
                break;
            case NotFoundException n:
                await ProblemResults.WriteAsync(ctx, 404, "Not found", n.Message);
                break;
            case ConflictException c:
                await ProblemResults.WriteAsync(ctx, 409, "Conflict", c.Message, c.Details is null ? null : new Dictionary<string, object?> { ["details"] = c.Details });
                break;
            case ForbiddenException f:
                await ctx.RequestServices.GetRequiredService<IAuditService>().RecordNowAsync("ACCESS_DENIED", "Endpoint", $"{ctx.Request.Method} {ctx.Request.Path}", null, null, f.Message, outcome: "Denied");
                await ProblemResults.WriteAsync(ctx, 403, "Forbidden", f.Message);
                break;
            case DbUpdateConcurrencyException:
                await ProblemResults.WriteAsync(ctx, 409, "Concurrency conflict", "The record was modified by another user. Reload and retry (stale row version).");
                break;
            case DbUpdateException du when du.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true:
                await ProblemResults.WriteAsync(ctx, 409, "Conflict", "A record with the same unique key already exists.");
                break;
            case BadHttpRequestException bad:
                await ProblemResults.WriteAsync(ctx, bad.StatusCode, "Bad request", "The request could not be read.");
                break;
            default:
                log.LogError(ex, "Unhandled error");
                await ProblemResults.WriteAsync(ctx, 500, "Server error", "An unexpected error occurred. Quote the correlation ID when contacting support.");
                break;
        }
    }));
}

/// <summary>Serilog formatter that masks identifiers, tokens, emails and secrets before anything is written.</summary>
public sealed class MaskingFormatter : ITextFormatter
{
    private readonly ITextFormatter _inner = new CompactJsonFormatter();

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var sw = new StringWriter();
        _inner.Format(logEvent, sw);
        output.Write(SensitiveDataMasker.Mask(sw.ToString()));
    }
}

/// <summary>Defence in depth: never serialize tenant keys, search tokens, hashes, ciphertext or credentials in API responses.</summary>
public static class SensitivePropertyFilter
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "TenantId", "PasswordHash", "ClientSecretHash", "IdNumberToken", "IdNumberEncrypted", "SearchToken", "ValueToken", "ValueEncrypted", "ValueEncryptedRaw",
        "SubjectHash", "EntryHash", "ProviderResponseEncrypted", "Hash", "TokenVersion", "ExternalSubject", "StorageKey", "ContentHash", "NameSearchKey", "RequesterPermissions"
    };

    public static void Apply(System.Text.Json.Serialization.Metadata.JsonTypeInfo info)
    {
        if (info.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object) return;
        for (var i = info.Properties.Count - 1; i >= 0; i--)
            if (Blocked.Contains(info.Properties[i].Name)) info.Properties.RemoveAt(i);
    }
}
