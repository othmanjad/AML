using System.Diagnostics;
using AML.Application;
using AML.Application.Actions;
using AML.Application.Alerts;
using AML.Application.Common;
using AML.Application.Governance;
using AML.Application.Realtime;
using AML.Application.Reporting;
using AML.Application.Reports;
using AML.Application.Risk;
using AML.Application.Rules;
using AML.Application.Screening;
using AML.Application.Transactions;
using AML.Domain.Entities;
using AML.Infrastructure.Persistence;
using AML.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AML.Infrastructure.Jobs;

public sealed record JobDefinition(string Name, TimeSpan Interval, bool PerTenant, Func<IServiceProvider, long, CancellationToken, Task<int>> Run);

public static class JobCatalog
{
    public static IReadOnlyList<JobDefinition> All(IConfiguration c) => new List<JobDefinition>
    {
        new("outbox-publisher", TimeSpan.FromSeconds(10), false, (sp, _, ct) => sp.GetRequiredService<OutboxPublisher>().PublishPendingAsync(ct)),
        new("export-processor", TimeSpan.FromSeconds(5), false, (sp, _, ct) => sp.GetRequiredService<ExportService>().ProcessQueuedAsync(ct)),
        new("screening-retry", TimeSpan.FromSeconds(30), true, (sp, t, ct) => sp.GetRequiredService<ScreeningService>().RetryDueAsync(t, ct)),
        new("action-retry", TimeSpan.FromSeconds(30), true, (sp, t, ct) => sp.GetRequiredService<ComplianceActionService>().RetryDueAsync(t, ct)),
        new("action-reconciliation", TimeSpan.FromMinutes(10), true, async (sp, t, ct) => (await sp.GetRequiredService<ComplianceActionService>().ReconcileAsync(t, ct)).Count),
        new("ingestion-retry", TimeSpan.FromSeconds(60), true, (sp, t, ct) => sp.GetRequiredService<IngestionService>().RetryQueuedAsync(t, ct)),
        new("rule-scheduler", TimeSpan.FromMinutes(int.TryParse(c["Jobs:RuleIntervalMinutes"], out var m) ? m : 60), true, (sp, t, ct) => sp.GetRequiredService<RuleExecutionService>().RunScheduledAsync(t, ct)),
        new("review-scheduler", TimeSpan.FromHours(6), true, (sp, t, ct) => sp.GetRequiredService<RiskAssessmentService>().ScheduleReviewTasksAsync(t, ct)),
        new("review-escalation", TimeSpan.FromHours(1), true, (sp, t, ct) => sp.GetRequiredService<RiskAssessmentService>().EscalateOverdueReviewsAsync(t, ct)),
        new("hold-sla", TimeSpan.FromMinutes(1), true, (sp, t, ct) => sp.GetRequiredService<RealtimeService>().EscalateHoldsAsync(t, ct)),
        new("str-sla", TimeSpan.FromMinutes(15), true, (sp, t, ct) => sp.GetRequiredService<RegulatoryReportService>().CheckSlasAsync(t, ct)),
        new("authority-overdue", TimeSpan.FromHours(1), true, (sp, t, ct) => sp.GetRequiredService<AuthorityRequestService>().MarkOverdueAsync(t, ct)),
        new("alert-sla", TimeSpan.FromMinutes(15), true, (sp, t, ct) => sp.GetRequiredService<AlertService>().MarkOverdueAsync(t, ct)),
        new("kyc-expiry", TimeSpan.FromHours(12), true, (sp, t, ct) => sp.GetRequiredService<AML.Application.Customers.CddService>().CreateKycRefreshTasksAsync(t, ct)),
        new("entity-resolution", TimeSpan.FromHours(6), true, async (sp, t, ct) => { await sp.GetRequiredService<EntityResolutionService>().RunAsync(t, ct); return 1; }),
    };
}

/// <summary>Executes jobs in a system security context with heartbeat tracking (consistent hosted-worker approach).</summary>
public sealed class JobExecutor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<JobExecutor> _log;
    private readonly IConfiguration _config;

    public JobExecutor(IServiceScopeFactory scopes, ILogger<JobExecutor> log, IConfiguration config)
    {
        _scopes = scopes; _log = log; _config = config;
    }

    public async Task<int> RunAsync(string name, CancellationToken ct)
    {
        var job = JobCatalog.All(_config).FirstOrDefault(j => j.Name == name) ?? throw new NotFoundException("Job", name);
        var sw = Stopwatch.StartNew();
        var total = 0;
        string? error = null;
        try
        {
            List<long> tenants;
            using (var s = _scopes.CreateScope())
            {
                SetSystem(s.ServiceProvider, 0);
                tenants = await s.ServiceProvider.GetRequiredService<AmlDbContext>().Set<Tenant>().Where(t => t.IsActive).Select(t => t.Id).ToListAsync(ct);
            }
            foreach (var tenant in job.PerTenant ? tenants : new List<long> { tenants.FirstOrDefault() })
            {
                using var scope = _scopes.CreateScope();
                SetSystem(scope.ServiceProvider, tenant);
                total += await job.Run(scope.ServiceProvider, tenant, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            _log.LogError(ex, "Job {Job} failed", name);
        }
        await HeartbeatAsync(name, error, sw.ElapsedMilliseconds, ct);
        if (error is not null) throw new InvalidOperationException($"Job {name} failed: {error}");
        return total;
    }

    public static void SetSystem(IServiceProvider sp, long tenantId)
    {
        var u = sp.GetRequiredService<CurrentUser>();
        u.IsSystem = true;
        u.IsAuthenticated = true;
        u.Username = "system:job";
        u.TenantId = tenantId;
    }

    private async Task HeartbeatAsync(string name, string? error, long ms, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        SetSystem(scope.ServiceProvider, 0);
        var db = scope.ServiceProvider.GetRequiredService<AmlDbContext>();
        var hb = await db.Set<JobHeartbeat>().FirstOrDefaultAsync(h => h.JobName == name, ct);
        if (hb is null) db.Set<JobHeartbeat>().Add(hb = new JobHeartbeat { JobName = name });
        hb.LastRunAt = DateTime.UtcNow;
        hb.LastStatus = error is null ? "Succeeded" : "Failed";
        hb.LastError = error;
        hb.LastDurationMs = ms;
        hb.ConsecutiveFailures = error is null ? 0 : hb.ConsecutiveFailures + 1;
        await db.SaveChangesAsync(ct);
    }
}

public sealed class JobRunnerHostedService : BackgroundService
{
    private readonly JobExecutor _executor;
    private readonly IConfiguration _config;
    private readonly ILogger<JobRunnerHostedService> _log;

    public JobRunnerHostedService(JobExecutor executor, IConfiguration config, ILogger<JobRunnerHostedService> log)
    {
        _executor = executor; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = JobCatalog.All(_config).ToDictionary(j => j.Name, _ => DateTime.UtcNow.AddSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var j in JobCatalog.All(_config))
            {
                if (DateTime.UtcNow < next[j.Name]) continue;
                try { await _executor.RunAsync(j.Name, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("{Job}: {Error}", j.Name, ex.Message); }
                next[j.Name] = DateTime.UtcNow + j.Interval;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
