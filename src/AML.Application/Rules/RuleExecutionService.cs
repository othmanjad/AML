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
        var missingInputs = new List<(long CustomerId, List<string> Fields)>();
        var notApplicable = new List<object>();
        var notApplicableCount = 0;
        var errors = new List<string>();
        try
        {
            var tz = await _settings.TimeZoneAsync(v.TenantId, ct);
            var evaluator = new RuleEvaluator(tz);
            var data = new RuleDataProvider(_db, v.TenantId);
            var windowSpan = def.Window?.Type == "Calendar" ? TimeSpan.FromDays(def.Window.Unit == "Month" ? 32 : def.Window.Unit == "Week" ? 8 : 2) : def.Window?.RollingDuration ?? TimeSpan.Zero;
            var loadFrom = fromUtc - windowSpan - TimeSpan.FromDays(def.LookbackDays);
            var txns = await _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.TenantId == v.TenantId && t.TimestampUtc >= loadFrom && t.TimestampUtc < toUtc && t.EconomicLegOfId == null).ToListAsync(ct);
            var links = def.Subject == "Entity"
                ? await _db.Set<EntityLink>().IgnoreQueryFilters().AsNoTracking().Where(l => l.TenantId == v.TenantId && l.Status == "Linked" && l.Confidence >= def.EntityMinConfidence).ToListAsync(ct)
                : null;
            var subjects = BuildSubjects(def, txns, await CustomerTypesAsync(txns, v.TenantId, ct), links);
            foreach (var s in subjects)
            {
                // Matches sharing a deduplication key end up in the same alert, so they are merged during evaluation.
                Func<RuleMatch, string?>? group = def.DedupKey?.Contains("evidenceGroup") == true ? null : m => DedupKey(rule, v, def, s, m, exec, opt);
                var r = evaluator.Evaluate(def, s, fromUtc, toUtc, data, group);
                exec.RecordsEvaluated += r.RecordsEvaluated;
                exec.Matches += r.RawMatches;
                errors.AddRange(r.Errors);
                if (r.NotApplicableReason is { } na)
                {
                    notApplicableCount++;
                    if (notApplicable.Count < 500) notApplicable.Add(new { subject = s.SubjectKey, customerId = s.CustomerId, reason = na });
                }
                if (r.MissingInputs.Count > 0 && s.CustomerId is { } missingFor) missingInputs.Add((missingFor, r.MissingInputs.ToList()));
                foreach (var m in r.Matches)
                {
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
                        executionId = exec.Id, mode = opt.Mode, subject = s.SubjectKey, subjectContext = s.Context, matchedConditions = m.Conditions, evidence = m.Evidence,
                        linkedParties = await LinkedPartiesAsync(v.TenantId, s.CustomerId, txns, m, ct)
                    });
                    var res = await _alerts.UpsertAsync(new AlertDraft
                    {
                        TenantId = v.TenantId, Source = "Rule", AlertType = rule.Category, RuleId = rule.Id, RuleVersionId = v.Id, RuleVersion = v.Version, RuleCode = rule.Code,
                        ExecutionId = exec.Id, PrimaryCustomerId = s.CustomerId, SubjectKey = s.SubjectKey, Severity = EscalatedSeverity(m.SetSeverities.Count > 0 ? m.SetSeverities.OrderBy(x => Array.IndexOf(SeverityOrder, x)).Last() : def.Action?.Severity ?? v.Severity, m.Indicators.Values),
                        Title = $"{rule.Code}: {rule.Name}", Description = string.Join("; ", m.Conditions.Where(c => c.Result).Select(c => $"{c.Expression} (actual {c.Actual})")),
                        DeduplicationKey = dedup, EvidenceJson = evidence, MatchKey = $"trigger:{m.Trigger.Id}", MatchedConditionsJson = JsonSerializer.Serialize(m.Conditions),
                        WindowStart = m.WindowStart, WindowEnd = m.WindowEnd, TransactionIds = m.ContributingTransactionIds.ToList(),
                        CustomerIds = txns.Where(t => m.ContributingTransactionIds.Contains(t.Id)).SelectMany(t => new[] { t.SenderCustomerId, t.ReceiverCustomerId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList(),
                        IsBacktest = opt.Mode == "Backtest", IsTestData = rule.IsSample,
                        AdditionalMatches = m.MergedMatches.Select(x => new AlertMatchDraft($"trigger:{x.Trigger.Id}", JsonSerializer.Serialize(x.Conditions),
                            JsonSerializer.Serialize(new { triggerTransaction = x.Trigger.SourceTransactionId, mergedIntoTrigger = m.Trigger.SourceTransactionId, x.WindowStart, x.WindowEnd }),
                            x.WindowStart, x.WindowEnd)).ToList()
                    }, ct);
                    if (res.Created) exec.AlertsCreated++; else exec.AlertsDeduplicated++;
                    if (opt.Mode is "Scheduled" or "Manual" && def.Action?.ComplianceAction is { } action && res.NewMatch)
                    {
                        await _actions.RequestFromRuleAsync(action, m.Trigger.Id, s.CustomerId, res.Alert.Id, $"Rule {rule.Code} v{v.Version}", v.TenantId, ct);
                    }
                }
            }
            if (missingInputs.Count > 0) await RecordMissingInputsAsync(rule, v, missingInputs, opt, ct);
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
        tracked.ResultJson = JsonSerializer.Serialize(new { matches = summaries.Take(50), notApplicableCount, notApplicable });
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

    public static List<RuleSubjectData> BuildSubjects(RuleDefinition def, List<Transaction> txns, Dictionary<long, string> types, List<EntityLink>? links = null)
    {
        switch (def.Subject)
        {
            case "Entity":
                // Union-find over high-confidence entity-resolution links; each cluster is one subject.
                var parent = new Dictionary<long, long>();
                long Find(long x) { while (parent.TryGetValue(x, out var p) && p != x) x = parent[x] = parent.GetValueOrDefault(p, p); return x; }
                void Union(long a, long b) { parent.TryAdd(a, a); parent.TryAdd(b, b); var ra = Find(a); var rb = Find(b); if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb); }
                foreach (var l in links ?? new()) Union(l.CustomerAId, l.CustomerBId);
                long Root(long c) => parent.ContainsKey(c) ? Find(c) : c;
                var byEntity = new Dictionary<long, List<RuleTxn>>();
                foreach (var t in txns)
                {
                    long? sr = t.SenderCustomerId is { } s0 ? Root(s0) : null, rr = t.ReceiverCustomerId is { } r0 ? Root(r0) : null;
                    if (sr is { } se) (byEntity.TryGetValue(se, out var l) ? l : byEntity[se] = new()).Add(RuleDataProvider.Project(t, "out"));
                    if (rr is { } re && re != sr) (byEntity.TryGetValue(re, out var l2) ? l2 : byEntity[re] = new()).Add(RuleDataProvider.Project(t, "in"));
                }
                return byEntity.Select(kv =>
                {
                    var members = parent.Keys.Where(c => Find(c) == kv.Key).DefaultIfEmpty(kv.Key).Distinct().OrderBy(x => x).ToList();
                    var memberLinks = (links ?? new()).Where(l => members.Contains(l.CustomerAId) && members.Contains(l.CustomerBId))
                        .Select(l => new { l.CustomerAId, l.CustomerBId, l.LinkType, l.Confidence, l.Evidence, l.RuleVersion }).ToList();
                    return new RuleSubjectData
                    {
                        SubjectKey = $"entity:{kv.Key}", SubjectType = "Entity", CustomerId = kv.Key, CustomerType = types.GetValueOrDefault(kv.Key),
                        Context = new { members, links = memberLinks, minConfidence = def.EntityMinConfidence }, Transactions = kv.Value.OrderBy(t => t.TimestampUtc).ToList()
                    };
                }).ToList();
            case "Agent":
                return txns.Where(t => t.AgentCustomerId != null).GroupBy(t => t.AgentCustomerId!.Value)
                    .Select(g => new RuleSubjectData { SubjectKey = $"agent:{g.Key}", SubjectType = "Agent", CustomerId = g.Key, CustomerType = types.GetValueOrDefault(g.Key), Transactions = g.Select(t => RuleDataProvider.Project(t, "any")).OrderBy(t => t.TimestampUtc).ToList() }).ToList();
            case "Device":
                return txns.Where(t => t.DeviceId != null).GroupBy(t => t.DeviceId!)
                    .Select(g => new RuleSubjectData { SubjectKey = $"device:{g.Key}", SubjectType = "Device", DeviceId = g.Key, Transactions = g.Select(t => RuleDataProvider.Project(t, "any")).OrderBy(t => t.TimestampUtc).ToList() }).ToList();
            case "Wallet":
                var byWallet = new Dictionary<string, (long? Owner, List<RuleTxn> Txns)>();
                void AddW(string? w, long? owner, Transaction t, string dir)
                {
                    if (w is null) return;
                    if (!byWallet.TryGetValue(w, out var e)) byWallet[w] = e = (owner, new());
                    e.Txns.Add(RuleDataProvider.Project(t, dir));
                }
                foreach (var t in txns)
                {
                    AddW(t.SenderWalletExternalId, t.SenderCustomerId, t, "out");
                    if (t.ReceiverWalletExternalId != t.SenderWalletExternalId) AddW(t.ReceiverWalletExternalId, t.ReceiverCustomerId, t, "in");
                }
                return byWallet.Select(kv => new RuleSubjectData
                {
                    SubjectKey = $"wallet:{kv.Key}", SubjectType = "Wallet", CustomerId = kv.Value.Owner,
                    CustomerType = kv.Value.Owner is { } o ? types.GetValueOrDefault(o) : null, Transactions = kv.Value.Txns.OrderBy(t => t.TimestampUtc).ToList()
                }).ToList();
            default:
                var dict = new Dictionary<long, List<RuleTxn>>();
                var unresolved = new Dictionary<string, List<RuleTxn>>();
                var walletFallback = def.UnresolvedParties == "Wallet";
                foreach (var t in txns)
                {
                    if (t.SenderCustomerId is { } s) (dict.TryGetValue(s, out var l) ? l : dict[s] = new()).Add(RuleDataProvider.Project(t, "out"));
                    else if (walletFallback && t.SenderWalletExternalId is { } sw) (unresolved.TryGetValue(sw, out var u) ? u : unresolved[sw] = new()).Add(RuleDataProvider.Project(t, "out"));
                    if (t.ReceiverCustomerId is { } r && r != t.SenderCustomerId) (dict.TryGetValue(r, out var l2) ? l2 : dict[r] = new()).Add(RuleDataProvider.Project(t, "in"));
                    else if (t.ReceiverCustomerId is null && walletFallback && t.ReceiverWalletExternalId is { } rw && rw != t.SenderWalletExternalId)
                        (unresolved.TryGetValue(rw, out var u2) ? u2 : unresolved[rw] = new()).Add(RuleDataProvider.Project(t, "in"));
                }
                return dict.Select(kv => new RuleSubjectData { SubjectKey = $"customer:{kv.Key}", SubjectType = "Customer", CustomerId = kv.Key, CustomerType = types.GetValueOrDefault(kv.Key), Transactions = kv.Value.OrderBy(t => t.TimestampUtc).ToList() })
                    .Concat(unresolved.Select(kv => new RuleSubjectData { SubjectKey = $"wallet:{kv.Key}", SubjectType = "Wallet", Transactions = kv.Value.OrderBy(t => t.TimestampUtc).ToList() }))
                    .ToList();
        }
    }

    public static string DedupKey(AmlRule rule, AmlRuleVersion v, RuleDefinition def, RuleSubjectData s, RuleMatch m, AmlRuleExecution exec, RuleRunOptions opt) =>
        DedupKey(rule.Code, v.Version, def, s.SubjectKey, m.LocalDay, m.WindowStart, m.WindowEnd, () => string.Join(",", m.ContributingTransactionIds.OrderBy(x => x)),
            opt.Mode == "Backtest" ? $"BT{exec.Id}" : null);

    private static readonly string[] SeverityOrder = { "Low", "Medium", "High", "Critical" };

    /// <summary>Raises the base severity to the highest severity defined by the indicators the match met.</summary>
    public static string EscalatedSeverity(string baseSeverity, IEnumerable<string?> indicatorSeverities)
    {
        var rank = Array.IndexOf(SeverityOrder, baseSeverity);
        foreach (var s in indicatorSeverities)
            if (s is not null && Array.IndexOf(SeverityOrder, s) > rank) { rank = Array.IndexOf(SeverityOrder, s); baseSeverity = s; }
        return baseSeverity;
    }

    /// <summary>Entity-resolution links among the parties of a match (subject and counterparties), so investigators see related parties.</summary>
    private async Task<object?> LinkedPartiesAsync(long tenantId, long? subject, List<Transaction> txns, RuleMatch m, CancellationToken ct)
    {
        var parties = txns.Where(t => m.ContributingTransactionIds.Contains(t.Id)).SelectMany(t => new[] { t.SenderCustomerId, t.ReceiverCustomerId })
            .Append(subject).Where(x => x != null).Select(x => x!.Value).Distinct().Take(1000).ToList();
        if (parties.Count < 2) return null;
        var found = await _db.Set<EntityLink>().IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == tenantId && parties.Contains(l.CustomerAId) && parties.Contains(l.CustomerBId))
            .Select(l => new { l.CustomerAId, l.CustomerBId, l.LinkType, l.Confidence, l.Status }).ToListAsync(ct);
        return found.Count == 0 ? null : found;
    }

    /// <summary>
    /// A condition whose customer/profile input is missing cannot be evaluated: it is treated as not met, and for operational
    /// runs one open data-quality item per customer, rule and field records that the customer was not fully evaluated.
    /// </summary>
    private async Task RecordMissingInputsAsync(AmlRule rule, AmlRuleVersion v, List<(long CustomerId, List<string> Fields)> missing, RuleRunOptions opt, CancellationToken ct)
    {
        if (opt.Mode is not ("Manual" or "Scheduled")) return;
        foreach (var (customerId, fields) in missing)
        {
            var id = customerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var f in fields)
            {
                var marker = $"[{rule.Code}/{f}]";
                if (await _db.Set<DataQualityIssue>().IgnoreQueryFilters().AnyAsync(d => d.TenantId == v.TenantId && d.IssueType == "RuleInputMissing" && d.EntityId == id && d.Status == "Open" && d.Description.StartsWith(marker), ct)) continue;
                _db.Set<DataQualityIssue>().Add(new DataQualityIssue
                {
                    TenantId = v.TenantId, IssueType = "RuleInputMissing", EntityType = "Customer", EntityId = id, Severity = "Medium", CreatedAt = _clock.UtcNow,
                    Description = $"{marker} Rule {rule.Code} v{v.Version} could not evaluate '{f}' for customer {id}: value missing; condition treated as not met (result incomplete). Complete the customer data."
                });
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Deduplication key from its components; also used by the real-time path so inline and batch hits share one alert.</summary>
    public static string DedupKey(string ruleCode, int version, RuleDefinition def, string subjectKey, DateOnly localDay, DateTime windowStart, DateTime windowEnd,
        Func<string>? evidenceGroup, string? prefix = null)
    {
        var parts = def.DedupKey is { Count: > 0 } ? def.DedupKey : new List<string> { "rule", "version", "subject", "day" };
        var sb = new StringBuilder();
        if (prefix is not null) sb.Append(prefix).Append('|');
        foreach (var p in parts)
        {
            sb.Append(p switch
            {
                "rule" => ruleCode,
                "version" => "v" + version,
                "subject" => subjectKey,
                "day" => localDay.ToString("yyyy-MM-dd"),
                "window" => $"{windowStart:yyyyMMddHHmm}-{windowEnd:yyyyMMddHHmm}",
                "evidenceGroup" => Hash(evidenceGroup?.Invoke() ?? "")[..16],
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
        var lookbackDays = await _settings.IntSettingAsync("Rules:LateArrivalLookbackDays", 7, tenantId, ct);
        foreach (var v in versions)
        {
            var last = await _db.Set<AmlRuleExecution>().IgnoreQueryFilters()
                .Where(e => e.RuleVersionId == v.Id && e.Mode == "Scheduled" && e.Status != "Failed" && e.TriggeredBy == "scheduler")
                .OrderByDescending(e => e.WindowEnd).Select(e => new { e.WindowEnd, e.StartedAt }).FirstOrDefaultAsync(ct);
            var from = last?.WindowEnd ?? now.AddDays(-1);
            if (from < v.EffectiveFrom) from = v.EffectiveFrom;
            await RunAsync(v.Id, from, now, new RuleRunOptions("Scheduled", true, "scheduler"), ct);
            if (last is not null) await EvaluateLateArrivalsAsync(v, last.StartedAt, last.WindowEnd, lookbackDays, now, ct);
        }
        return versions.Count;
    }

    /// <summary>
    /// Transactions ingested after the previous scheduled run started but dated before that run's window end were never
    /// evaluated. Within the configured lookback the affected period is re-evaluated (alerts deduplicate into existing
    /// ones); older late arrivals are recorded as data-quality items so operations can see they were not evaluated.
    /// </summary>
    private async Task EvaluateLateArrivalsAsync(AmlRuleVersion v, DateTime previousRunStarted, DateTime previousWindowEnd, int lookbackDays, DateTime now, CancellationToken ct)
    {
        var late = await _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == v.TenantId && t.IngestedAt >= previousRunStarted && t.TimestampUtc < previousWindowEnd)
            .Select(t => new { t.Id, t.SourceSystem, t.SourceTransactionId, t.TimestampUtc }).ToListAsync(ct);
        if (late.Count == 0) return;
        var limit = now.AddDays(-lookbackDays);
        var tooOld = late.Where(t => t.TimestampUtc < limit).ToList();
        foreach (var t in tooOld)
        {
            var id = t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (await _db.Set<DataQualityIssue>().IgnoreQueryFilters().AnyAsync(d => d.TenantId == v.TenantId && d.IssueType == "LateArrivalNotEvaluated" && d.EntityId == id, ct)) continue;
            _db.Set<DataQualityIssue>().Add(new DataQualityIssue
            {
                TenantId = v.TenantId, IssueType = "LateArrivalNotEvaluated", EntityType = "Transaction", EntityId = id, Severity = "Medium", CreatedAt = now,
                Description = $"{t.SourceSystem}/{t.SourceTransactionId} dated {t.TimestampUtc:O} arrived after its monitoring window was evaluated and is older than the {lookbackDays}-day re-evaluation lookback; not evaluated by scheduled rules."
            });
        }
        if (tooOld.Count > 0) await _db.SaveChangesAsync(ct);
        var toEvaluate = late.Where(t => t.TimestampUtc >= limit && t.TimestampUtc >= v.EffectiveFrom).ToList();
        if (toEvaluate.Count == 0) return;
        var start = toEvaluate.Min(t => t.TimestampUtc);
        // Triggers after the late transaction (up to the previous window end) see it in their windows, so they are re-evaluated too.
        var end = previousWindowEnd;
        _log.LogInformation("Re-evaluating {Count} late-arriving transactions for rule version {Version} over {From:O}..{To:O}", toEvaluate.Count, v.Id, start, end);
        await RunAsync(v.Id, start, end, new RuleRunOptions("Scheduled", true, "scheduler:late-arrival"), ct);
    }
}
