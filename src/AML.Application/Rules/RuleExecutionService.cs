using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AML.Application.Actions;
using AML.Application.Alerts;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AML.Application.Rules;

public sealed record RuleRunOptions(string Mode, bool CreateAlerts, string TriggeredBy, string? DefinitionOverrideJson = null, int SampleLimit = 200);

public sealed record RuleRunResult(long ExecutionId, string Status, int RecordsEvaluated, int Matches, int AlertsCreated, int AlertsDeduplicated, int Errors,
    IReadOnlyList<object> MatchSummaries, string CorrelationId);

public sealed class RuleExecutionService
{
    private readonly IAmlDbContext _db;
    private readonly IClock _clock;
    private readonly ICorrelationContext _corr;
    private readonly TenantSettingsService _settings;
    private readonly AlertFactory _alerts;
    private readonly ComplianceActionService _actions;
    private readonly IAuditService _audit;
    private readonly ILogger<RuleExecutionService> _log;

    public RuleExecutionService(IAmlDbContext db, IClock clock, ICorrelationContext corr, TenantSettingsService settings, AlertFactory alerts,
        ComplianceActionService actions, IAuditService audit, ILogger<RuleExecutionService> log)
    {
        _db = db; _clock = clock; _corr = corr; _settings = settings; _alerts = alerts; _actions = actions; _audit = audit; _log = log;
    }

    public async Task<RuleRunResult> RunAsync(long ruleVersionId, DateTime fromUtc, DateTime toUtc, RuleRunOptions opt, CancellationToken ct)
    {
        var v = await _db.Set<AmlRuleVersion>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == ruleVersionId, ct) ?? throw new NotFoundException("Rule version", ruleVersionId);
        var rule = await _db.Set<AmlRule>().IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.Id == v.RuleId, ct);
        var def = RuleDefinition.Parse(opt.DefinitionOverrideJson ?? v.DefinitionJson);
        var exec = new AmlRuleExecution
        {
            TenantId = v.TenantId, RuleId = rule.Id, RuleVersionId = v.Id, RuleVersion = v.Version, RuleCode = rule.Code, Mode = opt.Mode, WindowStart = fromUtc,
            WindowEnd = toUtc, CorrelationId = _corr.CorrelationId, TriggeredBy = opt.TriggeredBy, StartedAt = _clock.UtcNow, Status = "Running"
        };
        _db.Set<AmlRuleExecution>().Add(exec);
        await _db.SaveChangesAsync(ct);

        var summaries = new List<object>();
        var errors = new List<string>();
        try
        {
            var tz = await _settings.TimeZoneAsync(v.TenantId, ct);
            var evaluator = new RuleEvaluator(tz);
            var data = new RuleDataProvider(_db, v.TenantId);
            var windowSpan = def.Window?.Type == "Calendar" ? TimeSpan.FromDays(def.Window.Unit == "Month" ? 32 : def.Window.Unit == "Week" ? 8 : 2) : def.Window?.RollingDuration ?? TimeSpan.Zero;
            var loadFrom = fromUtc - windowSpan - TimeSpan.FromDays(def.LookbackDays);
            var txns = await _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.TenantId == v.TenantId && t.TimestampUtc >= loadFrom && t.TimestampUtc < toUtc).ToListAsync(ct);
            var subjects = BuildSubjects(def, txns, await CustomerTypesAsync(txns, v.TenantId, ct));
            foreach (var s in subjects)
            {
                var r = evaluator.Evaluate(def, s, fromUtc, toUtc, data);
                exec.RecordsEvaluated += r.RecordsEvaluated;
                errors.AddRange(r.Errors);
                foreach (var m in r.Matches)
                {
                    exec.Matches++;
                    var dedup = DedupKey(rule, v, def, s, m, exec, opt);
                    if (summaries.Count < opt.SampleLimit)
                        summaries.Add(new
                        {
                            subject = s.SubjectKey, customerId = s.CustomerId, trigger = m.Trigger.SourceTransactionId, m.WindowStart, m.WindowEnd,
                            conditions = m.Conditions, transactions = m.ContributingTransactionIds, deduplicationKey = dedup
                        });
                    if (!opt.CreateAlerts) continue;
                    var evidence = JsonSerializer.Serialize(new
                    {
                        rule = new { rule.Code, rule.Name, version = v.Version, ruleVersionId = v.Id, definitionHash = Hash(v.DefinitionJson), definition = JsonDocument.Parse(opt.DefinitionOverrideJson ?? v.DefinitionJson).RootElement },
                        executionId = exec.Id, mode = opt.Mode, subject = s.SubjectKey, matchedConditions = m.Conditions, evidence = m.Evidence
                    });
                    var res = await _alerts.UpsertAsync(new AlertDraft
                    {
                        TenantId = v.TenantId, Source = "Rule", AlertType = rule.Category, RuleId = rule.Id, RuleVersionId = v.Id, RuleVersion = v.Version, RuleCode = rule.Code,
                        ExecutionId = exec.Id, PrimaryCustomerId = s.CustomerId, SubjectKey = s.SubjectKey, Severity = def.Action?.Severity ?? v.Severity,
                        Title = $"{rule.Code}: {rule.Name}", Description = string.Join("; ", m.Conditions.Where(c => c.Result).Select(c => $"{c.Expression} (actual {c.Actual})")),
                        DeduplicationKey = dedup, EvidenceJson = evidence, MatchKey = $"trigger:{m.Trigger.Id}", MatchedConditionsJson = JsonSerializer.Serialize(m.Conditions),
                        WindowStart = m.WindowStart, WindowEnd = m.WindowEnd, TransactionIds = m.ContributingTransactionIds.ToList(),
                        CustomerIds = txns.Where(t => m.ContributingTransactionIds.Contains(t.Id)).SelectMany(t => new[] { t.SenderCustomerId, t.ReceiverCustomerId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList(),
                        IsBacktest = opt.Mode == "Backtest", IsTestData = rule.IsSample
                    }, ct);
                    if (res.Created) exec.AlertsCreated++; else exec.AlertsDeduplicated++;
                    if (opt.Mode is "Scheduled" or "Manual" && def.Action?.ComplianceAction is { } action && res.NewMatch)
                    {
                        await _actions.RequestFromRuleAsync(action, m.Trigger.Id, s.CustomerId, res.Alert.Id, $"Rule {rule.Code} v{v.Version}", v.TenantId, ct);
                    }
                }
            }
            exec.ErrorCount = errors.Count;
            exec.Errors = errors.Count == 0 ? null : string.Join("\n", errors.Take(100));
            exec.Status = errors.Count == 0 ? "Succeeded" : "CompletedWithErrors";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rule execution {ExecutionId} failed (correlation {CorrelationId})", exec.Id, _corr.CorrelationId);
            _db.ChangeTracker.Clear();
            exec = await _db.Set<AmlRuleExecution>().IgnoreQueryFilters().FirstAsync(e => e.Id == exec.Id, ct);
            exec.Status = "Failed";
            exec.ErrorCount++;
            exec.Errors = ex.Message;
        }
        var tracked = await _db.Set<AmlRuleExecution>().IgnoreQueryFilters().FirstAsync(e => e.Id == exec.Id, ct);
        if (!ReferenceEquals(tracked, exec))
        {
            tracked.RecordsEvaluated = exec.RecordsEvaluated; tracked.Matches = exec.Matches; tracked.AlertsCreated = exec.AlertsCreated;
            tracked.AlertsDeduplicated = exec.AlertsDeduplicated; tracked.ErrorCount = exec.ErrorCount; tracked.Errors = exec.Errors; tracked.Status = exec.Status;
        }
        tracked.CompletedAt = _clock.UtcNow;
        tracked.ResultJson = JsonSerializer.Serialize(new { matches = summaries.Take(50) });
        await _db.SaveChangesAsync(ct);
        return new RuleRunResult(tracked.Id, tracked.Status, tracked.RecordsEvaluated, tracked.Matches, tracked.AlertsCreated, tracked.AlertsDeduplicated, tracked.ErrorCount, summaries, tracked.CorrelationId);
    }

    private async Task<Dictionary<long, string>> CustomerTypesAsync(List<Transaction> txns, long tenantId, CancellationToken ct)
    {
        var ids = txns.SelectMany(t => new[] { t.SenderCustomerId, t.ReceiverCustomerId, t.AgentCustomerId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        var map = new Dictionary<long, string>();
        foreach (var chunk in ids.Chunk(1000))
            foreach (var c in await _db.Set<Customer>().IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId && chunk.Contains(c.Id)).Select(c => new { c.Id, c.CustomerType }).ToListAsync(ct))
                map[c.Id] = c.CustomerType;
        return map;
    }

    public static List<RuleSubjectData> BuildSubjects(RuleDefinition def, List<Transaction> txns, Dictionary<long, string> types)
    {
        switch (def.Subject)
        {
            case "Agent":
                return txns.Where(t => t.AgentCustomerId != null).GroupBy(t => t.AgentCustomerId!.Value)
                    .Select(g => new RuleSubjectData { SubjectKey = $"agent:{g.Key}", SubjectType = "Agent", CustomerId = g.Key, CustomerType = types.GetValueOrDefault(g.Key), Transactions = g.Select(t => RuleDataProvider.Project(t, "any")).OrderBy(t => t.TimestampUtc).ToList() }).ToList();
            case "Device":
                return txns.Where(t => t.DeviceId != null).GroupBy(t => t.DeviceId!)
                    .Select(g => new RuleSubjectData { SubjectKey = $"device:{g.Key}", SubjectType = "Device", DeviceId = g.Key, Transactions = g.Select(t => RuleDataProvider.Project(t, "any")).OrderBy(t => t.TimestampUtc).ToList() }).ToList();
            default:
                var dict = new Dictionary<long, List<RuleTxn>>();
                foreach (var t in txns)
                {
                    if (t.SenderCustomerId is { } s) (dict.TryGetValue(s, out var l) ? l : dict[s] = new()).Add(RuleDataProvider.Project(t, "out"));
                    if (t.ReceiverCustomerId is { } r && r != t.SenderCustomerId) (dict.TryGetValue(r, out var l2) ? l2 : dict[r] = new()).Add(RuleDataProvider.Project(t, "in"));
                }
                return dict.Select(kv => new RuleSubjectData { SubjectKey = $"customer:{kv.Key}", SubjectType = "Customer", CustomerId = kv.Key, CustomerType = types.GetValueOrDefault(kv.Key), Transactions = kv.Value.OrderBy(t => t.TimestampUtc).ToList() }).ToList();
        }
    }

    public static string DedupKey(AmlRule rule, AmlRuleVersion v, RuleDefinition def, RuleSubjectData s, RuleMatch m, AmlRuleExecution exec, RuleRunOptions opt)
    {
        var parts = def.DedupKey is { Count: > 0 } ? def.DedupKey : new List<string> { "rule", "version", "subject", "day" };
        var sb = new StringBuilder();
        if (opt.Mode == "Backtest") sb.Append($"BT{exec.Id}|");
        foreach (var p in parts)
        {
            sb.Append(p switch
            {
                "rule" => rule.Code,
                "version" => "v" + v.Version,
                "subject" => s.SubjectKey,
                "day" => m.LocalDay.ToString("yyyy-MM-dd"),
                "window" => $"{m.WindowStart:yyyyMMddHHmm}-{m.WindowEnd:yyyyMMddHHmm}",
                "evidenceGroup" => Hash(string.Join(",", m.ContributingTransactionIds.OrderBy(x => x)))[..16],
                _ => p
            }).Append('|');
        }
        var key = sb.ToString().TrimEnd('|');
        return key.Length > 380 ? key[..300] + "#" + Hash(key)[..32] : key;
    }

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>Runs every active, effective rule version for the window since its last scheduled run.</summary>
    public async Task<int> RunScheduledAsync(long tenantId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var versions = await _db.Set<AmlRuleVersion>().IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.Status == ConfigStatus.Active && v.EffectiveFrom <= now && (v.EffectiveTo == null || v.EffectiveTo > now)).ToListAsync(ct);
        foreach (var v in versions)
        {
            var last = await _db.Set<AmlRuleExecution>().IgnoreQueryFilters().Where(e => e.RuleVersionId == v.Id && e.Mode == "Scheduled" && e.Status != "Failed")
                .OrderByDescending(e => e.WindowEnd).Select(e => (DateTime?)e.WindowEnd).FirstOrDefaultAsync(ct);
            var from = last ?? now.AddDays(-1);
            if (from < v.EffectiveFrom) from = v.EffectiveFrom;
            await RunAsync(v.Id, from, now, new RuleRunOptions("Scheduled", true, "scheduler"), ct);
        }
        return versions.Count;
    }
}
