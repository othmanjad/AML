using System.Text.Json.Serialization;
using AML.Api.Infrastructure;
using AML.Infrastructure;
using AML.Infrastructure.Jobs;
using AML.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, sp, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext()
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console(new MaskingFormatter());
    var file = ctx.Configuration["Logging:FilePath"];
    if (!string.IsNullOrWhiteSpace(file)) cfg.WriteTo.File(new MaskingFormatter(), file, shared: true);
}, preserveStaticLogger: true);

var config = builder.Configuration;
builder.Services.AddAmlInfrastructure(config, builder.Environment.EnvironmentName);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    var authority = config["Auth:Authority"];
    if (!string.IsNullOrWhiteSpace(authority))
    {
        // Enterprise OIDC/OAuth identity provider.
        o.Authority = authority;
        o.Audience = config["Auth:Audience"];
    }
    else
    {
        // Development-only local issuer (clearly marked; disable via DevAuth:Enabled=false and configure Auth:Authority).
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = config["Auth:Issuer"] ?? "aml-dev", ValidateAudience = true, ValidAudience = config["Auth:Audience"] ?? "aml-api",
            ValidateIssuerSigningKey = true, IssuerSigningKey = TokenService.Key(config), ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30)
        };
    }
    o.MapInboundClaims = false;
});
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddControllers(o => o.Filters.Add(new ProducesAttribute("application/json")))
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        o.JsonSerializerOptions.TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver { Modifiers = { SensitivePropertyFilter.Apply } };
    })
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx =>
    {
        var corr = ctx.HttpContext.RequestServices.GetRequiredService<AML.Application.Common.ICorrelationContext>().CorrelationId;
        var pd = new ValidationProblemDetails(ctx.ModelState) { Status = 400, Title = "Validation failed", Instance = ctx.HttpContext.Request.Path };
        pd.Extensions["correlationId"] = corr;
        pd.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(pd) { ContentTypes = { "application/problem+json" } };
    });
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "AML Platform API", Version = "v1", Description = "AML and transaction-monitoring platform API. Development documentation only." });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>() });
    c.CustomSchemaIds(t => t.FullName!.Replace("+", "."));
});
var origins = (config["Cors:Origins"] ?? "http://localhost:5173").Split(',', StringSplitOptions.RemoveEmptyEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("X-Correlation-ID", "Content-Disposition")));
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });
builder.Services.AddHttpsRedirection(o => { o.RedirectStatusCode = StatusCodes.Status308PermanentRedirect; o.HttpsPort = int.TryParse(config["Https:Port"], out var hp) ? hp : 443; });
if (string.Equals(config["Jobs:RunInApi"], "true", StringComparison.OrdinalIgnoreCase)) builder.Services.AddHostedService<JobRunnerHostedService>();

var app = builder.Build();

if (string.Equals(config["Database:MigrateOnStartup"], "true", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AmlDbContext>();
    await db.Database.MigrateAsync();
    JobExecutor.SetSystem(scope.ServiceProvider, 0);
    var tenant = await scope.ServiceProvider.GetRequiredService<ReferenceDataSeeder>().SeedAsync();
    if (string.Equals(config["Seed:DemoData"], "true", StringComparison.OrdinalIgnoreCase)) await DemoDataSeeder.SeedAsync(app.Services, tenant.Id);
}

var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
// Behind a reverse proxy on a private container network (e.g. docker compose), the proxy's address is not loopback.
// Only enable when the API is reachable exclusively through that proxy.
if (string.Equals(config["Proxy:TrustForwardedHeaders"], "true", StringComparison.OrdinalIgnoreCase))
{
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
}
app.UseForwardedHeaders(forwarded);
app.UseMiddleware<CorrelationMiddleware>();
app.UseAmlExceptionHandler();
if (app.Environment.IsDevelopment() || string.Equals(config["Swagger:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    // Set Https:Redirect=false only when TLS is terminated elsewhere or for an internal HTTP-only test deployment.
    if (!string.Equals(config["Https:Redirect"], "false", StringComparison.OrdinalIgnoreCase)) app.UseHttpsRedirection();
}
app.UseSerilogRequestLogging(o => o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms");
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.Run();

public partial class Program { }
