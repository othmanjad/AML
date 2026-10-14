using AML.Application;
using AML.Application.Actions;
using AML.Application.Common;
using AML.Application.Realtime;
using AML.Application.Reporting;
using AML.Application.Reports;
using AML.Application.Screening;
using AML.Application.Transactions;
using AML.Infrastructure.Adapters;
using AML.Infrastructure.Jobs;
using AML.Infrastructure.Persistence;
using AML.Infrastructure.Security;
using AML.Infrastructure.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AML.Infrastructure;

/// <summary>Per-request (or per-job) security context.</summary>
public sealed class CurrentUser : ICurrentUser
{
    public bool IsAuthenticated { get; set; }
    public long? UserId { get; set; }
    public string Username { get; set; } = "";
    public long TenantId { get; set; }
    public HashSet<string> PermissionSet { get; set; } = new();
    public HashSet<string> RoleSet { get; set; } = new();
    public IReadOnlySet<string> Permissions => PermissionSet;
    public IReadOnlySet<string> Roles => RoleSet;
    public string? Team { get; set; }
    public string? DataScopeRegion { get; set; }
    public string? SourceSystem { get; set; }
    public bool IsSystem { get; set; }
    public bool Has(string permission) => IsSystem || PermissionSet.Contains(permission);
}

public sealed class SqlTransientErrorDetector : ITransientErrorDetector
{
    private static readonly int[] Numbers = { -2, 1205, 4060, 40197, 40501, 40613, 49918, 49919, 49920, 10928, 10929, 233, 64 };
    public bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is TransientFaultException or TimeoutException) return true;
            if (e is SqlException s && s.Errors.Cast<SqlError>().Any(x => Numbers.Contains(x.Number))) return true;
        }
        return false;
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddAmlInfrastructure(this IServiceCollection s, IConfiguration config, string environmentName)
    {
        var cs = config.GetConnectionString("Aml") ?? throw new InvalidOperationException("ConnectionStrings:Aml is not configured (use environment variable ConnectionStrings__Aml).");
        s.AddDbContext<AmlDbContext>(o => o.UseSqlServer(cs, sql => { sql.CommandTimeout(120); sql.MigrationsAssembly(typeof(AmlDbContext).Assembly.FullName); }));
        s.AddScoped<IAmlDbContext>(sp => sp.GetRequiredService<AmlDbContext>());
        s.AddScoped<CurrentUser>();
        s.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
        s.AddScoped<ICorrelationContext, CorrelationContext>();
        s.AddSingleton<ICryptoService, CryptoService>();
        s.AddScoped<IAuditService, AuditService>();
        s.AddSingleton<IFileStore, LocalFileStore>();
        s.AddSingleton<IMalwareScanner, SignatureMalwareScanner>();
        s.AddSingleton<IEventPublisher, DbEventPublisher>();
        s.AddSingleton<ITransientErrorDetector, SqlTransientErrorDetector>();
        var env = new EnvironmentInfo(environmentName);
        s.AddSingleton<IEnvironmentInfo>(env);
        s.AddSingleton<IScreeningProvider, InternalWatchlistScreeningProvider>();
        // Development and test environments always use the sandbox core adapter (no real freeze/release possible).
        var coreMode = config["Integrations:CoreAdapter"] ?? "Mock";
        if (!env.IsProduction && coreMode != "Mock")
            Console.WriteLine($"[AML] Integrations:CoreAdapter={coreMode} ignored in {environmentName}; sandbox/mock core adapter enforced.");
        s.AddSingleton<ICoreSystemAdapter, MockCoreSystemAdapter>();
        s.AddSingleton<IFiuReportAdapter, GoAmlXmlAdapter>();
        if (env.IsProduction) s.AddSingleton<IFiuSubmissionChannel, ManualOnlyFiuChannel>();
        else s.AddSingleton<IFiuSubmissionChannel, SandboxFiuChannel>();
        s.AddSingleton<IExportWriter, ExportWriter>();
        s.AddSingleton<JobExecutor>();
        s.AddScoped<ReferenceDataSeeder>();
        s.AddAmlApplication();
        return s;
    }
}

/// <summary>Design-time factory for EF migrations (connection string from environment, never source control).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AmlDbContext>
{
    public AmlDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Aml") ?? "Server=localhost;Database=AML_Design;Integrated Security=false;TrustServerCertificate=true";
        return new AmlDbContext(new DbContextOptionsBuilder<AmlDbContext>().UseSqlServer(cs).Options);
    }
}
