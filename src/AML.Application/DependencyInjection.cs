using AML.Application.Actions;
using AML.Application.Admin;
using AML.Application.Alerts;
using AML.Application.Cases;
using AML.Application.Common;
using AML.Application.Customers;
using AML.Application.Fields;
using AML.Application.Governance;
using AML.Application.Realtime;
using AML.Application.Reporting;
using AML.Application.Reports;
using AML.Application.Risk;
using AML.Application.Rules;
using AML.Application.Screening;
using AML.Application.Transactions;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AML.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddAmlApplication(this IServiceCollection s)
    {
        s.TryAddSingleton<IClock, SystemClock>();
        s.TryAddSingleton<IFaultInjector, NoFaultInjector>();
        s.TryAddSingleton<ITransientErrorDetector, DefaultTransientErrorDetector>();
        s.TryAddSingleton<INameMatcher, DefaultNameMatcher>();
        s.AddScoped<TenantSettingsService>();
        s.AddScoped<FxService>();
        s.AddScoped<AccessPolicy>();
        s.AddScoped<DependencyService>();
        s.AddScoped<FieldConfigService>();
        s.AddScoped<CustomerService>();
        s.AddScoped<CddService>();
        s.AddScoped<RiskDataSourceProvider>();
        s.AddScoped<RiskAssessmentService>();
        s.AddScoped<RiskConfigService>();
        s.AddScoped<AlertFactory>();
        s.AddScoped<AlertService>();
        s.AddScoped<IngestionService>();
        s.AddScoped<BalanceService>();
        s.AddScoped<TransactionQueryService>();
        s.AddScoped<WalletService>();
        s.AddScoped<RuleExecutionService>();
        s.AddScoped<RuleService>();
        s.AddScoped<ComplianceActionService>();
        s.AddScoped<ScreeningService>();
        s.AddScoped<CaseService>();
        s.AddScoped<RealtimeService>();
        s.AddScoped<RegulatoryReportService>();
        s.AddScoped<CtrService>();
        s.AddScoped<BenchmarkService>();
        s.AddScoped<AuthorityRequestService>();
        s.AddScoped<EntityResolutionService>();
        s.AddScoped<EffectivenessService>();
        s.AddScoped<QaService>();
        s.AddScoped<RetentionService>();
        s.AddScoped<JurisdictionService>();
        s.AddScoped<ReportService>();
        s.AddScoped<ExportService>();
        s.AddScoped<AdminService>();
        s.AddScoped<AuditQueryService>();
        s.AddScoped<OperationsService>();
        s.AddScoped<DashboardService>();
        s.AddScoped<OutboxPublisher>();
        return s;
    }
}

/// <summary>Reliable event publication: outbox rows written in the business transaction are published after commit; consumers dedupe by message ID.</summary>
public sealed class OutboxPublisher
{
    private readonly IAmlDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly IClock _clock;

    public OutboxPublisher(IAmlDbContext db, IEventPublisher publisher, IClock clock)
    {
        _db = db; _publisher = publisher; _clock = clock;
    }

    public async Task<int> PublishPendingAsync(CancellationToken ct)
    {
        var pending = await _db.Set<OutboxMessage>().Where(o => o.ProcessedAt == null && o.Attempts < 10).OrderBy(o => o.Id).Take(500).ToListAsync(ct);
        foreach (var m in pending)
        {
            try
            {
                await _publisher.PublishAsync(m.MessageId, m.EventType, m.PayloadJson, ct);
                m.ProcessedAt = _clock.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.Attempts++;
                m.LastError = ex.Message;
            }
            await _db.SaveChangesAsync(ct);
        }
        return pending.Count;
    }
}
