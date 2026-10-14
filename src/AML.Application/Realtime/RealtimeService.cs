using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AML.Application.Actions;
using AML.Application.Common;
using AML.Application.Transactions;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Rules;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AML.Application.Realtime;

public interface IEnvironmentInfo
{
    bool IsProduction { get; }
    string Name { get; }
}

public sealed class DecideRequest
{
    public string? SourceSystem { get; set; }
    public string SourceTransactionId { get; set; } = "";
    public string TransactionType { get; set; } = "";
    public string? Channel { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string? SenderWallet { get; set; }
    public string? ReceiverWallet { get; set; }
    public string? BeneficiaryName { get; set; }
    public List<string>? CounterpartyNames { get; set; }
    public string? Remarks { get; set; }
    public string? SourceCountry { get; set; }
    public string? DestinationCountry { get; set; }
    public DateTime? EventTime { get; set; }
}

public sealed record DecideResponse(Guid DecisionId, string Decision, IReadOnlyList<string> ReasonCodes, string CorrelationId, int LatencyMs, bool IsFallback, bool Idempotent);

public sealed class RealtimeService
{
    private static readonly ConcurrentDictionary<long, (DateTime Loaded, List<(WatchlistEntry Entry, bool Mandatory)> Entries)> Cache = new();
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly ICorrelationContext _corr;
    private readonly TenantSettingsService _settings;
    private readonly INameMatcher _matcher;
    private readonly FxService _fx;
    private readonly IEnvironmentInfo _env;
    private readonly ICoreSystemAdapter _core;
    private readonly IAuditService _audit;
    private readonly ILogger<RealtimeService> _log;
    private readonly IServiceScopeFactory _scopes;

    public RealtimeService(IAmlDbContext db, ICurrentUser user, IClock clock, ICorrelationContext corr, TenantSettingsService settings, INameMatcher matcher, FxService fx,
        IEnvironmentInfo env, ICoreSystemAdapter core, IAuditService audit, ILogger<RealtimeService> log, IServiceScopeFactory scopes)
    {
        _scopes = scopes;
        _db = db; _user = user; _clock = clock; _corr = corr; _settings = settings; _matcher = matcher; _fx = fx; _env = env; _core = core; _audit = audit; _log = log;
    }

    public static void InvalidateCache(long tenantId) => Cache.TryRemove(tenantId, out _);

    public async Task<DecideResponse> DecideAsync(DecideRequest r, CancellationToken ct)
    {
        var source = r.SourceSystem ?? _user.SourceSystem ?? throw new ValidationException("sourceSystem", "Source system is required.");
        if (string.IsNullOrWhiteSpace(r.SourceTransactionId)) throw new ValidationException("sourceTransactionId", "sourceTransactionId is required.");
        if (r.Amount < 0) throw new ValidationException("amount", "Amount must not be negative.");
        if (string.IsNullOrWhiteSpace(r.Currency)) throw new ValidationException("currency", "Currency is required.");
        var hash = IngestionService.Sha256(JsonSerializer.Serialize(new { r.SourceTransactionId, r.TransactionType, r.Channel, r.Amount, r.Currency, r.SenderWallet, r.ReceiverWallet, r.BeneficiaryName, r.CounterpartyNames, r.Remarks }));
        var existing = await _db.Set<RealtimeDecision>().AsNoTracking().FirstOrDefaultAsync(d => d.SourceSystem == source && d.SourceTransactionId == r.SourceTransactionId, ct);
        if (existing is not null)
        {
            if (existing.PayloadHash != hash) throw new ConflictException($"A decision already exists for {source}/{r.SourceTransactionId} with a different payload.");
            return new DecideResponse(existing.DecisionId, existing.Decision, existing.ReasonCodes.Split(',', StringSplitOptions.RemoveEmptyEntries), existing.CorrelationId, existing.LatencyMs, existing.IsFallback, true);
        }

        var sw = Stopwatch.StartNew();
        var budget = await _settings.IntSettingAsync("Realtime:LatencyBudgetMs", 300, ct: ct);
        var tenantId = _user.TenantId;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var evalTask = EvaluateAsync(r, source, tenantId, cts.Token);
        var finished = await Task.WhenAny(evalTask, Task.Delay(budget, ct));
        string decision; List<string> reasons; var fallback = false; string? fallbackMode = null; long? customerId = null; var postReview = false;
        string? rulesEvaluated = null, screeningSummary = null;
        if (finished == evalTask && evalTask.Status == TaskStatus.RanToCompletion)
        {
            (decision, reasons, customerId, rulesEvaluated, screeningSummary) = evalTask.Result;
        }
        else
        {
            cts.Cancel();
            fallback = true;
            var policies = await _db.Set<RealtimeFallbackPolicy>().AsNoTracking().Where(p => p.IsActive).OrderByDescending(p => p.Priority).ToListAsync(ct);
            var p = policies.FirstOrDefault(p => (p.TransactionType == null || p.TransactionType == r.TransactionType) && (p.Channel == null || p.Channel == r.Channel) &&
                                                 (p.MinAmount == null || r.Amount >= p.MinAmount) && (p.MaxAmount == null || r.Amount <= p.MaxAmount));
            fallbackMode = p?.Mode ?? "FailClosed";
            reasons = new List<string> { evalTask.IsFaulted ? "AML_EVALUATION_ERROR" : "AML_TIMEOUT", "FALLBACK_" + fallbackMode.ToUpperInvariant() };
            if (fallbackMode == "FailOpen") { decision = RealtimeDecisions.Allow; postReview = true; }
            else decision = p?.ClosedDecision ?? RealtimeDecisions.Hold;
            if (evalTask.IsFaulted) _log.LogError(evalTask.Exception, "Real-time evaluation failed for {Source}/{Txn}", source, r.SourceTransactionId);
            _ = evalTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
        sw.Stop();
        var d = new RealtimeDecision
        {
            TenantId = tenantId, DecisionId = Guid.NewGuid(), SourceSystem = source, SourceTransactionId = r.SourceTransactionId, PayloadHash = hash, Decision = decision,
            ReasonCodes = string.Join(",", reasons.Distinct()), CorrelationId = _corr.CorrelationId, LatencyMs = (int)sw.ElapsedMilliseconds, IsFallback = fallback,
            FallbackMode = fallbackMode, EvaluatedRules = rulesEvaluated, ScreeningSummary = screeningSummary, Amount = r.Amount, Currency = r.Currency.ToUpperInvariant(),
            TransactionType = r.TransactionType, Channel = r.Channel, CustomerId = customerId, NeedsPostReview = postReview, CreatedAt = _clock.UtcNow
        };
        _db.Set<RealtimeDecision>().Add(d);
        if (decision == RealtimeDecisions.Hold || postReview)
        {
            var sla = await _settings.IntSettingAsync("Realtime:HoldSlaMinutes", 30, ct: ct);
            _db.Set<HoldQueueItem>().Add(new HoldQueueItem
            {
                TenantId = tenantId, DecisionId = d.DecisionId, SourceSystem = source, SourceTransactionId = r.SourceTransactionId, Status = postReview ? "PostReview" : "Held",
                Reason = d.ReasonCodes, SlaDueAt = _clock.UtcNow.AddMinutes(sla), CreatedAt = _clock.UtcNow
            });
        }
        if (fallback) _audit.Record("REALTIME_FALLBACK", "RealtimeDecision", d.DecisionId.ToString(), null, new { decision, fallbackMode, d.LatencyMs, budget });
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Customers.CustomerService.IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            var winner = await _db.Set<RealtimeDecision>().AsNoTracking().FirstAsync(x => x.SourceSystem == source && x.SourceTransactionId == r.SourceTransactionId, ct);
            return new DecideResponse(winner.DecisionId, winner.Decision, winner.ReasonCodes.Split(','), winner.CorrelationId, winner.LatencyMs, winner.IsFallback, true);
        }
        return new DecideResponse(d.DecisionId, d.Decision, reasons.Distinct().ToList(), d.CorrelationId, d.LatencyMs, fallback, false);
    }

    private async Task<(string Decision, List<string> Reasons, long? CustomerId, string? Rules, string? Screening)> EvaluateAsync(DecideRequest r, string source, long tenantId, CancellationToken ct)
    {
        await Task.Yield();
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAmlDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<TenantSettingsService>();
        var fx = scope.ServiceProvider.GetRequiredService<FxService>();
        if (!_env.IsProduction)
        {
            var delay = await settings.IntSettingAsync("Realtime:SimulatedDelayMs", 0, tenantId, ct);
            if (delay > 0) await Task.Delay(delay, ct);
        }
        var reasons = new List<string>();
        var decision = RealtimeDecisions.Allow;
        var holdThreshold = await settings.DecimalSettingAsync("Realtime:HoldThreshold", 0.85m, tenantId, ct);
        var rejectThreshold = await settings.DecimalSettingAsync("Realtime:RejectThreshold", 0.95m, tenantId, ct);
        var confirmedDecision = await settings.SettingAsync("Realtime:ConfirmedMatchDecision", RealtimeDecisions.Reject, tenantId, ct);

        // 1. Name / free-text screening
        var entries = await EntriesAsync(db, tenantId, ct);
        var names = new List<(string Field, string Text)>();
        if (!string.IsNullOrWhiteSpace(r.BeneficiaryName)) names.Add(("beneficiary", r.BeneficiaryName));
        foreach (var n in r.CounterpartyNames ?? new()) if (!string.IsNullOrWhiteSpace(n)) names.Add(("counterparty", n));
        var hits = new List<string>();
        foreach (var (field, text) in names)
            foreach (var (e, mandatory) in entries)
            {
                var score = BestScore(text, e);
                if (score < holdThreshold) continue;
                hits.Add($"{field}:{e.SourceCode}/{e.EntryId}:{score:0.00}");
                if (score >= rejectThreshold && mandatory) { decision = Max(decision, confirmedDecision); reasons.Add("SANCTIONS_NAME_MATCH"); }
                else { decision = Max(decision, RealtimeDecisions.Hold); reasons.Add("POTENTIAL_LIST_MATCH"); }
            }
        if (!string.IsNullOrWhiteSpace(r.Remarks))
        {
            var tokens = ArabicNameNormalizer.Tokens(r.Remarks, removeLegalForms: true);
            foreach (var (e, _) in entries)
            {
                var n = Math.Max(1, ArabicNameNormalizer.Tokens(e.PrimaryName).Count);
                if (tokens.Count < n) continue;
                for (var i = 0; i + n <= tokens.Count; i++)
                {
                    var window = string.Join(' ', tokens.Skip(i).Take(n));
                    var score = BestScore(window, e);
                    if (score < holdThreshold) continue;
                    hits.Add($"remarks:{e.SourceCode}/{e.EntryId}:{score:0.00}");
                    decision = Max(decision, RealtimeDecisions.Hold);
                    reasons.Add("FREE_TEXT_LIST_MATCH");
                    break;
                }
            }
        }

        // 2. Real-time-capable rules using pre-computed aggregates (no historical scans inline).
        var senderId = r.SenderWallet is null ? null : await db.Set<Wallet>().AsNoTracking().Where(w => w.SourceSystem == source && w.ExternalWalletId == r.SenderWallet).Select(w => w.CustomerId).FirstOrDefaultAsync(ct);
        var tenant = await settings.TenantAsync(tenantId, ct);
        var now = r.EventTime?.ToUniversalTime() ?? _clock.UtcNow;
        var conv = await fx.ConvertAsync(tenantId, r.Amount, r.Currency.ToUpperInvariant(), tenant.BaseCurrency, now, ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, tz));
        var agg = senderId is null ? null : await db.Set<CustomerDailyAggregate>().AsNoTracking().FirstOrDefaultAsync(a => a.CustomerId == senderId && a.BusinessDate == today && a.Direction == "Out", ct);
        var rules = await (from v in db.Set<AmlRuleVersion>().AsNoTracking()
                           join rr in db.Set<AmlRule>().AsNoTracking() on v.RuleId equals rr.Id
                           where v.Status == ConfigStatus.Active && rr.IsRealtimeCapable && v.EffectiveFrom <= now && (v.EffectiveTo == null || v.EffectiveTo > now)
                           select new { rr.Code, v.Version, v.DefinitionJson }).ToListAsync(ct);
        var evaluated = new List<string>();
        foreach (var rule in rules)
        {
            var def = RuleDefinition.Parse(rule.DefinitionJson);
            if (def.Condition is null) continue;
            evaluated.Add($"{rule.Code} v{rule.Version}");
            var txn = new RuleTxn
            {
                Amount = r.Amount, Currency = r.Currency.ToUpperInvariant(), BaseAmount = conv.BaseAmount, Type = r.TransactionType, Channel = r.Channel, Status = "Pending",
                SourceCountry = r.SourceCountry, DestinationCountry = r.DestinationCountry, Remarks = r.Remarks, TimestampUtc = now, Direction = "out", FxStatus = conv.Status
            };
            if (EvalRealtime(def.Condition, txn, agg, tz)) { decision = Max(decision, RealtimeDecisions.Hold); reasons.Add($"RULE_{rule.Code}"); }
        }
        if (reasons.Count == 0) reasons.Add("NO_RISK_INDICATORS");
        return (decision, reasons, senderId, string.Join(";", evaluated), hits.Count == 0 ? null : string.Join(";", hits));
    }

    private decimal BestScore(string text, WatchlistEntry e)
    {
        var best = _matcher.Match(text, e.PrimaryName).Score;
        if (e.AliasesJson is not null)
            foreach (var a in JsonSerializer.Deserialize<List<string>>(e.AliasesJson) ?? new()) best = Math.Max(best, _matcher.Match(text, a).Score);
        return best;
    }

    private static string Max(string a, string b)
    {
        static int R(string d) => d == RealtimeDecisions.Reject ? 3 : d == RealtimeDecisions.Hold ? 2 : 1;
        return R(a) >= R(b) ? a : b;
    }

    private static bool EvalRealtime(ConditionNode n, RuleTxn t, CustomerDailyAggregate? agg, TimeZoneInfo tz)
    {
        if (n.IsGroup)
        {
            var results = n.Conditions!.Select(c => EvalRealtime(c, t, agg, tz)).ToList();
            return n.Op == "and" ? results.All(x => x) : results.Any(x => x);
        }
        if (n.IsAggregate)
        {
            decimal value = n.Aggregate == "count" ? (agg?.TxnCount ?? 0) + 1 : (agg?.BaseAmount ?? 0) + (t.BaseAmount ?? 0);
            return RuleEvaluator.Compare(value, n.Operator!, n.Value!.Value);
        }
        object? actual = n.Field switch
        {
            "txn.amount" => t.Amount, "txn.baseAmount" => t.BaseAmount, "txn.currency" => t.Currency, "txn.type" => t.Type, "txn.channel" => t.Channel,
            "txn.sourceCountry" => t.SourceCountry, "txn.destinationCountry" => t.DestinationCountry, "txn.remarks" => t.Remarks,
            "txn.localHour" => (decimal)TimeZoneInfo.ConvertTimeFromUtc(t.TimestampUtc, tz).Hour, _ => null
        };
        return RuleEvaluator.Compare(actual, n.Operator!, n.Value!.Value);
    }

    private async Task<List<(WatchlistEntry, bool)>> EntriesAsync(IAmlDbContext db, long tenantId, CancellationToken ct)
    {
        if (Cache.TryGetValue(tenantId, out var c) && c.Loaded > _clock.UtcNow.AddSeconds(-60)) return c.Entries;
        var sources = await db.Set<WatchlistSource>().IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive && s.ListType != "PEP" && s.ListType != "AdverseMedia").ToListAsync(ct);
        var ids = sources.Select(s => s.Id).ToList();
        var entries = await db.Set<WatchlistEntry>().IgnoreQueryFilters().AsNoTracking().Where(e => ids.Contains(e.SourceId) && e.Status == "Active").ToListAsync(ct);
        var list = entries.Select(e => (e, sources.First(s => s.Id == e.SourceId).IsMandatory)).ToList();
        Cache[tenantId] = (_clock.UtcNow, list);
        return list;
    }

    // ---------------- hold queue ----------------
    public async Task<List<HoldQueueItem>> HoldQueueAsync(string? status, CancellationToken ct)
    {
        var q = _db.Set<HoldQueueItem>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(h => h.Status == status);
        return await q.OrderBy(h => h.SlaDueAt).Take(500).ToListAsync(ct);
    }

    public async Task<HoldQueueItem> DecideHoldAsync(long id, bool release, string? note, CancellationToken ct)
    {
        Guard.Require(_user, Perm.HoldsWork);
        if (string.IsNullOrWhiteSpace(note)) throw new ValidationException("note", "A decision note is required.");
        var h = await _db.Set<HoldQueueItem>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Hold item", id);
        if (h.Status is not ("Held" or "PostReview")) throw new ConflictException($"Hold item is {h.Status}.");
        if (h.Status == "Held")
        {
            var cmd = release ? ActionTypes.ReleaseTransaction : ActionTypes.RejectTransaction;
            var res = await _core.SendAsync(new CoreCommand(h.TenantId, $"HOLD:{h.Id}:{cmd}", cmd, $"TXN:{h.SourceSystem}:{h.SourceTransactionId}", ComplianceActionService.NeutralCodeFor(cmd)), ct);
            if (res.Outcome != "Ack") throw new ConflictException($"Core system did not acknowledge the {cmd} command ({res.Outcome}: {res.Error}); retry later.");
            h.Status = release ? "Released" : "Rejected";
        }
        else h.Status = "Reviewed";
        h.DecidedBy = _user.Username;
        h.DecidedAt = _clock.UtcNow;
        h.DecisionNote = note;
        _audit.Record(release ? "HOLD_RELEASE" : "HOLD_REJECT", "HoldQueueItem", id.ToString(CultureInfo.InvariantCulture), new { Status = "Held" }, new { h.Status, h.SourceTransactionId }, note);
        await _db.SaveChangesAsync(ct);
        return h;
    }

    public async Task<int> EscalateHoldsAsync(long tenantId, CancellationToken ct)
    {
        var due = await _db.Set<HoldQueueItem>().IgnoreQueryFilters().Where(h => h.TenantId == tenantId && (h.Status == "Held" || h.Status == "PostReview") && !h.IsEscalated && h.SlaDueAt < _clock.UtcNow).ToListAsync(ct);
        foreach (var h in due)
        {
            h.IsEscalated = true;
            h.EscalatedAt = _clock.UtcNow;
            _db.Set<OperationalAlert>().Add(new OperationalAlert { TenantId = tenantId, Category = "HoldSlaBreach", Message = $"Held transaction {h.SourceSystem}/{h.SourceTransactionId} breached SLA ({h.SlaDueAt:O})", Reference = h.Id.ToString(CultureInfo.InvariantCulture), Severity = "Warning", CreatedAt = _clock.UtcNow });
            _audit.Record("HOLD_SLA_ESCALATION", "HoldQueueItem", h.Id.ToString(CultureInfo.InvariantCulture), null, new { h.SlaDueAt });
        }
        await _db.SaveChangesAsync(ct);
        return due.Count;
    }

    public async Task<object> DecisionsAsync(int take, CancellationToken ct) =>
        await _db.Set<RealtimeDecision>().AsNoTracking().OrderByDescending(d => d.Id).Take(Math.Clamp(take, 1, 500))
            .Select(d => new { d.DecisionId, d.SourceSystem, d.SourceTransactionId, d.Decision, d.ReasonCodes, d.LatencyMs, d.IsFallback, d.FallbackMode, d.NeedsPostReview, d.TransactionId, d.CreatedAt, d.CorrelationId })
            .ToListAsync(ct);
}
