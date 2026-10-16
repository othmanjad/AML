using System.Globalization;
using System.Text.Json;

namespace AML.Domain.Rules;

/// <summary>Transaction projection used by the rule engine (direction is relative to the subject).</summary>
public sealed class RuleTxn
{
    public long Id { get; init; }
    public string SourceTransactionId { get; init; } = "";
    public DateTime TimestampUtc { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "";
    public decimal? BaseAmount { get; init; }
    public string? BaseCurrency { get; init; }
    public string FxStatus { get; init; } = "SameCurrency";
    public decimal? FxRate { get; init; }
    public DateTime? FxRateDate { get; init; }
    public int? FxRateVersion { get; init; }
    public string Type { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Channel { get; init; }
    public decimal? Fee { get; init; }
    public decimal? Commission { get; init; }
    public string? SubType { get; init; }
    public string? Mcc { get; init; }
    /// <summary>Balance of the subject's side immediately before / after the transaction, as reported by the core (transaction currency).</summary>
    public decimal? BalanceBefore { get; init; }
    public decimal? BalanceAfter { get; init; }
    public long? SenderCustomerId { get; init; }
    public long? ReceiverCustomerId { get; init; }
    public string? SenderWallet { get; init; }
    public string? ReceiverWallet { get; init; }
    public long? AgentCustomerId { get; init; }
    public string? SourceCountry { get; init; }
    public string? DestinationCountry { get; init; }
    public string? Remarks { get; init; }
    public string? DeviceId { get; init; }
    public string? CopResult { get; init; }
    public decimal? InvoiceAmount { get; init; }
    public string? InvoiceCurrency { get; init; }
    public string? GoodsCategory { get; init; }
    public decimal ReversedAmount { get; init; }
    public bool IsReversal { get; init; }
    /// <summary>in / out relative to the subject (customer subjects); "any" otherwise.</summary>
    public string Direction { get; init; } = "any";

    public string CounterpartyKey => Direction == "in"
        ? SenderCustomerId?.ToString(CultureInfo.InvariantCulture) ?? "W:" + SenderWallet
        : ReceiverCustomerId?.ToString(CultureInfo.InvariantCulture) ?? "W:" + ReceiverWallet;
}

public sealed class RuleSubjectData
{
    public string SubjectKey { get; init; } = "";
    public string SubjectType { get; init; } = "Customer";
    public long? CustomerId { get; init; }
    public string? CustomerType { get; init; }
    public string? DeviceId { get; init; }
    /// <summary>Wallet subjects: the wallet's external identifier.</summary>
    public string? WalletExternalId { get; init; }
    /// <summary>Entity / Household subjects: all member customers (CustomerId is the primary member).</summary>
    public IReadOnlyList<long>? Members { get; init; }
    /// <summary>Optional subject description written to alert evidence (e.g. entity members and the links that join them).</summary>
    public object? Context { get; init; }
    /// <summary>All subject transactions in the evaluation range plus lookback, sorted ascending by time.</summary>
    public List<RuleTxn> Transactions { get; init; } = new();
}

public interface IRuleDataProvider
{
    object? GetAttribute(long? customerId, string field, DateTime asOfUtc);
    IReadOnlyList<decimal> GetPeerValues(long subjectCustomerId, string peerGroup, DateTime fromUtc, DateTime toUtc);
    int CountNewCustomersByAgent(long agentCustomerId, DateTime fromUtc, DateTime toUtc);
    IReadOnlyList<long> CustomersOnDevice(string deviceId, DateTime fromUtc, DateTime toUtc);
    IReadOnlyList<RuleTxn> GetNetworkEdges(DateTime fromUtc, DateTime toUtc);
    /// <summary>Customers linked to the given customer by entity resolution (status Linked).</summary>
    IReadOnlySet<long> GetLinkedCustomers(long customerId) => new HashSet<long>();
    /// <summary>Device/session/contact-change signals of a customer, ascending by time.</summary>
    IReadOnlyList<RuleSignal> GetSignals(long customerId) => Array.Empty<RuleSignal>();
    /// <summary>Devices a customer has used (signals and transactions).</summary>
    IReadOnlyList<string> GetDevices(long customerId) => Array.Empty<string>();
    /// <summary>
    /// End-of-day base-currency balances per business date in [from, to] for a customer (summed over its wallets) or, when
    /// walletExternalId is given, for that wallet. Dates without a reported balance (or with a missing FX rate) are absent.
    /// </summary>
    IReadOnlyDictionary<DateOnly, decimal> GetBalances(long? customerId, string? walletExternalId, DateOnly from, DateOnly to) => new Dictionary<DateOnly, decimal>();
    /// <summary>Codes of an active lookup list: "CATEGORY" or "CATEGORY:ATTRIBUTE".</summary>
    IReadOnlySet<string> GetLookupCodes(string reference) => new HashSet<string>();
    /// <summary>Risk attribute of a country from the COUNTRY lookup (e.g. HighRisk, Elevated); "Standard" when none; null for unknown codes.</summary>
    string? GetCountryRisk(string? country) => null;
    /// <summary>The subject's peer group: a signature of the shared attribute values and the members (subject included).</summary>
    (string Signature, IReadOnlyList<long> Members)? GetPeerGroup(long customerId, IReadOnlyList<string> peerBy) => null;
    /// <summary>Transactions of the given customers in (from, to], one entry per customer side with direction relative to that customer.</summary>
    IReadOnlyList<(long CustomerId, RuleTxn Txn)> GetCustomerTransactions(IReadOnlyCollection<long> customerIds, DateTime fromUtc, DateTime toUtc) => Array.Empty<(long, RuleTxn)>();
    /// <summary>Industry benchmark of a metric for the segment the customer belongs to (as of a time).</summary>
    RuleBenchmark? GetBenchmark(string metric, long customerId, DateTime asOfUtc) => null;
}

public sealed record RuleBenchmark(string Metric, string SegmentField, string SegmentValue, decimal Value, string? Source);

/// <summary>A customer signal; IsRoaming marks logins from allow-listed carrier/roaming IP ranges (location not reliable).</summary>
public sealed record RuleSignal(string DeviceId, string? IpAddress, string? GeoCountry, string EventType, DateTime OccurredAt, bool IsRoaming = false);

public sealed record MatchedCondition(string Expression, string Actual, string Expected, bool Result);

public sealed class RuleMatch
{
    public RuleTxn Trigger { get; init; } = null!;
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
    public DateOnly LocalDay { get; init; }
    public List<MatchedCondition> Conditions { get; } = new();
    public HashSet<long> ContributingTransactionIds { get; } = new();
    public Dictionary<string, object?> Evidence { get; } = new();
    /// <summary>Contributing transaction ranges not yet materialised (aggregate node, eligible-index range).</summary>
    internal List<DeferredContribution> Deferred { get; } = new();
    internal int MergedTriggers { get; set; }
    /// <summary>Earlier triggers merged into this match (same deduplication key), oldest first; capped.</summary>
    public List<RuleMatch> MergedMatches { get; } = new();
    /// <summary>Secondary indicators met by this match (label → severity or null).</summary>
    public Dictionary<string, string?> Indicators { get; } = new(StringComparer.Ordinal);
    /// <summary>Severities set (not raised) by met indicators with severityMode Set.</summary>
    public List<string> SetSeverities { get; } = new();
    public const int MergedMatchLimit = 1000;
}

internal sealed record DeferredContribution(ConditionNode Node, int Lo, int Hi, bool RequireBase);

public sealed class RuleEvaluationResult
{
    public int RecordsEvaluated { get; set; }
    /// <summary>Applicability condition not met for this subject (expressions that failed); the subject was not evaluated.</summary>
    public string? NotApplicableReason { get; set; }
    /// <summary>Customer/profile inputs a condition needed but that had no value (condition evaluated as not met).</summary>
    public HashSet<string> MissingInputs { get; } = new(StringComparer.Ordinal);
    /// <summary>Number of triggers whose condition matched (before grouping by deduplication key).</summary>
    public int RawMatches { get; set; }
    public List<RuleMatch> Matches { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Interprets a validated <see cref="RuleDefinition"/> over in-memory transactions. Windows are evaluated in the
/// institution's time zone; storage remains UTC. Rolling windows: (t - duration, t]. Calendar windows: the local
/// day/week(Monday start)/month containing t, counting activity up to and including t.
/// "between" is inclusive of both bounds.
/// </summary>
public sealed class RuleEvaluator
{
    private readonly TimeZoneInfo _tz;

    public RuleEvaluator(TimeZoneInfo tz) => _tz = tz;

    /// <summary>Maximum number of transactions listed individually in match evidence (all remain linked to the alert).</summary>
    public const int EvidenceTransactionLimit = 5000;

    /// <param name="groupKey">
    /// Optional grouping (normally the alert deduplication key). Matches with the same key are merged into one: the latest
    /// match's state is kept, contributing transactions are unioned and the first trigger is recorded. This keeps the work
    /// per subject linear when a pattern persists across many triggers.
    /// </param>
    public RuleEvaluationResult Evaluate(RuleDefinition def, RuleSubjectData subject, DateTime rangeStartUtc, DateTime rangeEndUtc, IRuleDataProvider data,
        Func<RuleMatch, string?>? groupKey = null)
    {
        var result = new RuleEvaluationResult();
        if (def.Condition is null) return result;
        if (def.Scope?.CustomerTypes is { Count: > 0 } types && subject.CustomerType is not null && !types.Contains(subject.CustomerType, StringComparer.OrdinalIgnoreCase))
            return result;

        var eligible = subject.Transactions.Where(t => IsEligible(def, t)).OrderBy(t => t.TimestampUtc).ThenBy(t => t.Id).ToList();
        var times = eligible.Select(t => t.TimestampUtc).ToArray();
        var cache = new NodeCache(eligible) { Def = def };
        var baseCtx = new EvalContext(def, subject, null!, -1, eligible, 0, 0, default, default, data, _tz, cache);
        cache.Matcher = (n, t) => (n.Direction is null or "any" || t.Direction == n.Direction) && FilterMatches(n.Filter, t, baseCtx);
        var groups = new Dictionary<string, RuleMatch>();
        var order = new List<string>();
        for (var idx = 0; idx < eligible.Count; idx++)
        {
            var trigger = eligible[idx];
            if (trigger.TimestampUtc < rangeStartUtc || trigger.TimestampUtc >= rangeEndUtc) continue;
            result.RecordsEvaluated++;
            try
            {
                var (ws, we, bucketEnd) = WindowFor(def.Window, trigger.TimestampUtc);
                var calendar = def.Window?.Type == "Calendar";
                var lo = calendar ? LowerBound(times, ws) : UpperBound(times, ws);
                var hi = UpperBound(times, we);
                var ctx = new EvalContext(def, subject, trigger, idx, eligible, lo, Math.Max(lo, hi), ws, we, data, _tz, cache) { Result = result, Times = times, BucketEnd = calendar ? bucketEnd : we };
                var match = new RuleMatch
                {
                    Trigger = trigger,
                    WindowStart = ws,
                    WindowEnd = calendar ? bucketEnd : we,
                    LocalDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(trigger.TimestampUtc, _tz))
                };
                if (def.Applicability is not null)
                {
                    var pre = new RuleMatch { Trigger = trigger, WindowStart = ws, WindowEnd = we, LocalDay = match.LocalDay };
                    if (!EvalNode(def.Applicability, ctx, pre))
                    {
                        result.NotApplicableReason ??= string.Join("; ", pre.Conditions.Where(c => !c.Result).Select(c => $"{c.Expression} (actual {c.Actual})"));
                        continue;
                    }
                }
                if (!EvalNode(def.Condition, ctx, match)) continue;
                result.RawMatches++;
                foreach (var ind in def.Action?.Indicators ?? new())
                {
                    var scratch = new RuleMatch { Trigger = trigger, WindowStart = ws, WindowEnd = we, LocalDay = match.LocalDay };
                    if (ind.Condition is not null && EvalNode(ind.Condition, ctx, scratch))
                    {
                        if (ind.SeverityMode == "Set") { match.Indicators[ind.Label] = null; match.SetSeverities.Add(ind.Severity!); }
                        else match.Indicators[ind.Label] = ind.Severity;
                        foreach (var kv in scratch.Evidence) match.Evidence[$"indicator:{ind.Label}:{kv.Key}"] = kv.Value;
                    }
                }
                match.ContributingTransactionIds.Add(trigger.Id);
                match.Evidence["windowType"] = def.Window?.Type ?? "None";
                match.Evidence["windowStartUtc"] = match.WindowStart;
                match.Evidence["windowEndUtc"] = match.WindowEnd;
                match.Evidence["windowStartLocal"] = TimeZoneInfo.ConvertTimeFromUtc(match.WindowStart, _tz).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                match.Evidence["windowEndLocal"] = TimeZoneInfo.ConvertTimeFromUtc(match.WindowEnd, _tz).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                match.Evidence["timeZone"] = _tz.Id;
                match.Evidence["triggerTransaction"] = trigger.SourceTransactionId;
                var key = groupKey?.Invoke(match);
                if (key is null)
                {
                    if (match.Indicators.Count > 0) match.Evidence["indicators"] = match.Indicators.Keys.ToList();
                    Finalize(def, match, eligible, cache);
                    result.Matches.Add(match);
                    continue;
                }
                if (groups.TryGetValue(key, out var previous))
                {
                    match.Deferred.AddRange(previous.Deferred);
                    match.ContributingTransactionIds.UnionWith(previous.ContributingTransactionIds);
                    match.Evidence["firstTriggerTransaction"] = previous.Evidence.TryGetValue("firstTriggerTransaction", out var f) ? f : previous.Trigger.SourceTransactionId;
                    match.MergedTriggers = previous.MergedTriggers + 1;
                    foreach (var kv in previous.Indicators) match.Indicators.TryAdd(kv.Key, kv.Value);
                    if (previous.MergedMatches.Count < RuleMatch.MergedMatchLimit)
                    {
                        match.MergedMatches.AddRange(previous.MergedMatches);
                        previous.MergedMatches.Clear();
                        previous.Deferred.Clear();
                        match.MergedMatches.Add(previous);
                    }
                    else { match.MergedMatches.AddRange(previous.MergedMatches); previous.MergedMatches.Clear(); }
                }
                else order.Add(key);
                groups[key] = match;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Transaction {trigger.SourceTransactionId}: {ex.Message}");
            }
        }
        foreach (var key in order)
        {
            var m = groups[key];
            if (m.MergedTriggers > 0) m.Evidence["mergedTriggers"] = m.MergedTriggers + 1;
            if (m.Indicators.Count > 0) m.Evidence["indicators"] = m.Indicators.Keys.ToList();
            Finalize(def, m, eligible, cache);
            result.Matches.Add(m);
        }
        return result;
    }

    /// <summary>Materialises deferred contributing sets and the evidence transaction list.</summary>
    private static void Finalize(RuleDefinition def, RuleMatch match, List<RuleTxn> eligible, NodeCache cache)
    {
        foreach (var g in match.Deferred.GroupBy(d => (d.Node, d.RequireBase)))
        {
            var pred = cache.Predicate(g.Key.Node);
            var intervals = g.Select(d => (d.Lo, d.Hi)).OrderBy(x => x.Lo).ToList();
            int curLo = -1, curHi = -1;
            void Emit(int a, int b)
            {
                for (var i = a; i < b; i++)
                    if (pred[i] && (!g.Key.RequireBase || EffectiveBase(def, eligible[i]) is not null)) match.ContributingTransactionIds.Add(eligible[i].Id);
            }
            foreach (var (a, b) in intervals)
            {
                if (a > curHi) { if (curHi > curLo) Emit(curLo, curHi); curLo = a; curHi = b; }
                else curHi = Math.Max(curHi, b);
            }
            if (curHi > curLo) Emit(curLo, curHi);
        }
        match.Deferred.Clear();
        var contributing = eligible.Where(t => match.ContributingTransactionIds.Contains(t.Id)).ToList();
        match.Evidence["transactionCount"] = contributing.Count;
        if (contributing.Count > EvidenceTransactionLimit) match.Evidence["transactionsTruncatedTo"] = EvidenceTransactionLimit;
        match.Evidence["transactions"] = contributing.Take(EvidenceTransactionLimit).Select(t => new Dictionary<string, object?>
        {
            ["id"] = t.Id, ["sourceTransactionId"] = t.SourceTransactionId, ["timestampUtc"] = t.TimestampUtc,
            ["originalAmount"] = t.Amount, ["currency"] = t.Currency, ["fxRate"] = t.FxRate, ["fxRateDate"] = t.FxRateDate,
            ["fxRateVersion"] = t.FxRateVersion, ["baseAmount"] = EffectiveBase(def, t), ["baseCurrency"] = t.BaseCurrency,
            ["fxStatus"] = t.FxStatus, ["status"] = t.Status, ["reversedAmount"] = t.ReversedAmount, ["direction"] = t.Direction
        }).ToList();
    }

    private static int LowerBound(DateTime[] a, DateTime v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { var mid = (lo + hi) >> 1; if (a[mid] < v) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static int UpperBound(DateTime[] a, DateTime v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { var mid = (lo + hi) >> 1; if (a[mid] <= v) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>
    /// Per-evaluation cache of aggregate filter results over the subject's eligible transactions. Filters depend only on the
    /// transaction and the subject (never on the trigger), so they are computed once and windows use prefix sums.
    /// </summary>
    private sealed class NodeCache
    {
        private readonly List<RuleTxn> _txns;
        private readonly Dictionary<ConditionNode, bool[]> _pred = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ConditionNode, (int[] Count, decimal[] Sum, int[] Missing)> _prefix = new(ReferenceEqualityComparer.Instance);
        public NodeCache(List<RuleTxn> txns) => _txns = txns;
        public Func<ConditionNode, RuleTxn, bool>? Matcher { get; set; }
        public RuleDefinition? Def { get; set; }

        public bool[] Predicate(ConditionNode n)
        {
            if (_pred.TryGetValue(n, out var p)) return p;
            p = new bool[_txns.Count];
            for (var i = 0; i < p.Length; i++) p[i] = Matcher!(n, _txns[i]);
            return _pred[n] = p;
        }

        public (int[] Count, decimal[] Sum, int[] Missing) Prefix(ConditionNode n)
        {
            if (_prefix.TryGetValue(n, out var x)) return x;
            var p = Predicate(n);
            var c = new int[p.Length + 1]; var s = new decimal[p.Length + 1]; var m = new int[p.Length + 1];
            for (var i = 0; i < p.Length; i++)
            {
                var b = p[i] ? EffectiveBase(Def!, _txns[i]) : null;
                c[i + 1] = c[i] + (p[i] ? 1 : 0);
                s[i + 1] = s[i] + (b ?? 0);
                m[i + 1] = m[i] + (p[i] && b is null ? 1 : 0);
            }
            return _prefix[n] = (c, s, m);
        }
    }

    public bool IsEligible(RuleDefinition def, RuleTxn t)
    {
        if (t.IsReversal) return false;
        if (!def.Statuses.Contains(t.Status, StringComparer.OrdinalIgnoreCase)) return false;
        if (def.ReversalHandling == "Exclude" && t.Status is "Reversed" or "PartiallyReversed") return false;
        var s = def.Scope;
        if (s is null) return true;
        if (s.TransactionTypes is { Count: > 0 } && !s.TransactionTypes.Contains(t.Type, StringComparer.OrdinalIgnoreCase)) return false;
        if (s.Channels is { Count: > 0 } && !s.Channels.Contains(t.Channel ?? "", StringComparer.OrdinalIgnoreCase)) return false;
        if (s.Currencies is { Count: > 0 } && !s.Currencies.Contains(t.Currency, StringComparer.OrdinalIgnoreCase)) return false;
        if (s.Countries is { Count: > 0 } && !s.Countries.Contains(t.SourceCountry ?? "", StringComparer.OrdinalIgnoreCase) &&
            !s.Countries.Contains(t.DestinationCountry ?? "", StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static decimal? EffectiveBase(RuleDefinition def, RuleTxn t)
    {
        if (t.FxStatus == "Missing" || t.BaseAmount is null) return null;
        if (def.ReversalHandling == "Net" && t.ReversedAmount > 0 && t.Amount != 0)
            return Math.Round(t.BaseAmount.Value * (t.Amount - t.ReversedAmount) / t.Amount, 4);
        return t.BaseAmount;
    }

    public static decimal EffectiveAmount(RuleDefinition def, RuleTxn t) =>
        def.ReversalHandling == "Net" ? t.Amount - t.ReversedAmount : t.Amount;

    public (DateTime Start, DateTime End, DateTime BucketEnd) WindowFor(RuleWindow? w, DateTime tUtc)
    {
        if (w is null) return (tUtc, tUtc, tUtc);
        if (w.Type == "Rolling") return (tUtc - w.RollingDuration, tUtc, tUtc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(tUtc, _tz);
        DateTime startLocal, endLocal;
        switch (w.Unit)
        {
            case "Week":
                var diff = ((int)local.DayOfWeek + 6) % 7;
                startLocal = local.Date.AddDays(-diff);
                endLocal = startLocal.AddDays(7);
                break;
            case "Month":
                startLocal = new DateTime(local.Year, local.Month, 1);
                endLocal = startLocal.AddMonths(1);
                break;
            default:
                startLocal = local.Date;
                endLocal = startLocal.AddDays(1);
                break;
        }
        return (ToUtc(startLocal), tUtc, ToUtc(endLocal));
    }

    private DateTime ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (_tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, _tz);
    }

    private sealed class EvalContext
    {
        public EvalContext(RuleDefinition def, RuleSubjectData subject, RuleTxn trigger, int triggerIndex, List<RuleTxn> allEligible, int lo, int hi,
            DateTime windowStart, DateTime windowEnd, IRuleDataProvider data, TimeZoneInfo tz, NodeCache cache)
        {
            Def = def; Subject = subject; Trigger = trigger; TriggerIndex = triggerIndex; AllEligible = allEligible; Lo = lo; Hi = hi;
            WindowStart = windowStart; WindowEnd = windowEnd; Data = data; Tz = tz; Cache = cache;
        }
        public RuleDefinition Def { get; }
        public RuleSubjectData Subject { get; }
        public RuleTxn Trigger { get; }
        public int TriggerIndex { get; }
        public List<RuleTxn> AllEligible { get; }
        public int Lo { get; }
        public int Hi { get; }
        public DateTime WindowStart { get; }
        public DateTime WindowEnd { get; }
        public IRuleDataProvider Data { get; }
        public TimeZoneInfo Tz { get; }
        public NodeCache Cache { get; }
        public RuleEvaluationResult? Result { get; init; }
        public DateTime[] Times { get; init; } = Array.Empty<DateTime>();
        /// <summary>Calendar windows: end of the period containing the trigger (equals WindowEnd for rolling windows).</summary>
        public DateTime BucketEnd { get; init; }
        private List<RuleTxn>? _window;
        /// <summary>Eligible subject transactions in the window (materialised on first use).</summary>
        public List<RuleTxn> Window => _window ??= AllEligible.GetRange(Lo, Hi - Lo);
    }

    private bool EvalNode(ConditionNode n, EvalContext ctx, RuleMatch match)
    {
        if (n.IsGroup)
        {
            var results = n.Conditions!.Select(c => EvalNode(c, ctx, match)).ToList();
            return n.Op == "and" ? results.All(r => r) : results.Any(r => r);
        }
        if (n.IsAggregate) return EvalAggregate(n, ctx, match);
        var actual = FieldValue(n.Field!, ctx.Trigger, ctx);
        var ok = Cmp(actual, n, ctx.Trigger, ctx, match, n.Field!);
        // Optional inputs (e.g. "flag neq true") are satisfied by a missing value; only a failed condition is incomplete.
        if (!ok && actual is null && !n.Field!.StartsWith("txn.", StringComparison.Ordinal) && !n.Field.StartsWith(RuleFieldRegistry.CounterpartyPrefix, StringComparison.Ordinal) && ctx.Subject.CustomerId is not null) ctx.Result?.MissingInputs.Add(n.Field);
        match.Conditions.Add(new MatchedCondition($"{n.Field} {n.Operator} {Show(n.Value)}", Fmt(actual), Show(n.Value), ok));
        return ok;
    }

    private object? FieldValue(string field, RuleTxn t, EvalContext ctx) => field switch
    {
        "txn.amount" => EffectiveAmount(ctx.Def, t),
        "txn.baseAmount" => EffectiveBase(ctx.Def, t),
        "txn.fee" => t.Fee,
        "txn.commission" => t.Commission,
        "txn.subType" => t.SubType,
        "txn.mcc" => t.Mcc,
        "txn.balanceBefore" => t.BalanceBefore,
        "txn.balanceAfter" => t.BalanceAfter,
        "txn.localHour" => (decimal)TimeZoneInfo.ConvertTimeFromUtc(t.TimestampUtc, ctx.Tz).Hour,
        "txn.currency" => t.Currency,
        "txn.type" => t.Type,
        "txn.channel" => t.Channel,
        "txn.status" => t.Status,
        "txn.sourceCountry" => t.SourceCountry,
        "txn.destinationCountry" => t.DestinationCountry,
        "txn.remarks" => t.Remarks,
        "txn.copResult" => t.CopResult,
        "txn.direction" => t.Direction,
        "txn.isCrossBorder" => t.SourceCountry is not null && t.DestinationCountry is not null && t.SourceCountry != t.DestinationCountry,
        // Transfer between two wallets of the same resolved customer.
        "txn.isOwnAccount" => t.SenderCustomerId is not null && t.SenderCustomerId == t.ReceiverCustomerId,
        "txn.isLinkedParty" => ctx.Subject.CustomerId is { } lc && (t.Direction == "in" ? t.SenderCustomerId : t.ReceiverCustomerId) is { } cp && cp != lc && ctx.Data.GetLinkedCustomers(lc).Contains(cp),
        "txn.loginCountryMismatch" => LoginCountryMismatch(t, ctx),
        "txn.sourceCountryRisk" => t.SourceCountry is null ? null : ctx.Data.GetCountryRisk(t.SourceCountry),
        "txn.destinationCountryRisk" => t.DestinationCountry is null ? null : ctx.Data.GetCountryRisk(t.DestinationCountry),
        "txn.invoiceAmount" => t.InvoiceAmount,
        "txn.hasInvoice" => t.InvoiceAmount is not null,
        "txn.goodsCategory" => t.GoodsCategory,
        // Payment amount versus the declared invoice (same currency only; different currencies are not compared).
        "txn.invoiceVariancePct" => t.InvoiceAmount is > 0 && string.Equals(t.InvoiceCurrency, t.Currency, StringComparison.OrdinalIgnoreCase)
            ? Math.Round((EffectiveAmount(ctx.Def, t) - t.InvoiceAmount.Value) / t.InvoiceAmount.Value * 100, 4) : null,
        "balance.previousDay" => PreviousBalance(t, ctx)?.Balance,
        "txn.balanceSharePct" => BalanceShare(t, ctx),
        _ when field.StartsWith(RuleFieldRegistry.CounterpartyPrefix, StringComparison.Ordinal) =>
            ctx.Data.GetAttribute(t.Direction == "in" ? t.SenderCustomerId : t.ReceiverCustomerId, RuleFieldRegistry.MapCounterparty(field), t.TimestampUtc),
        _ => ctx.Data.GetAttribute(ctx.Subject.CustomerId, field, t.TimestampUtc)
    };

    /// <summary>Latest login (within 24 h before the transaction) came from a country different from the transaction's source country.</summary>
    private static bool LoginCountryMismatch(RuleTxn t, EvalContext ctx)
    {
        if (ctx.Subject.CustomerId is not { } c || t.SourceCountry is null) return false;
        var login = ctx.Data.GetSignals(c).LastOrDefault(x => x.EventType == "Login" && x.OccurredAt <= t.TimestampUtc && x.OccurredAt >= t.TimestampUtc.AddHours(-24) && x.GeoCountry is not null);
        if (login is { IsRoaming: true }) return false;
        return login is not null && !string.Equals(login.GeoCountry, t.SourceCountry, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Days before the transaction's business date searched for the latest reported end-of-day balance.</summary>
    public const int BalanceSearchDays = 7;

    private static IReadOnlyDictionary<DateOnly, decimal> SubjectBalances(EvalContext ctx, DateOnly from, DateOnly to)
    {
        if (ctx.Subject.WalletExternalId is { } w) return ctx.Data.GetBalances(null, w, from, to);
        if (ctx.Subject.Members is { Count: > 0 } members)
        {
            var total = new Dictionary<DateOnly, decimal>();
            foreach (var m in members)
                foreach (var kv in ctx.Data.GetBalances(m, null, from, to)) total[kv.Key] = total.GetValueOrDefault(kv.Key) + kv.Value;
            return total;
        }
        return ctx.Subject.CustomerId is { } c ? ctx.Data.GetBalances(c, null, from, to) : new Dictionary<DateOnly, decimal>();
    }

    /// <summary>Latest end-of-day balance before the transaction's local business date (searched back BalanceSearchDays days).</summary>
    private (DateOnly Date, decimal Balance)? PreviousBalance(RuleTxn t, EvalContext ctx)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(t.TimestampUtc, ctx.Tz));
        var b = SubjectBalances(ctx, day.AddDays(-BalanceSearchDays), day.AddDays(-1));
        if (b.Count == 0) { if (ctx.Subject.CustomerId is not null || ctx.Subject.WalletExternalId is not null) ctx.Result?.MissingInputs.Add("balance.previousDay"); return null; }
        var last = b.Keys.Max();
        return (last, b[last]);
    }

    /// <summary>
    /// Transaction amount as a percentage of the balance before it: the balance reported on the transaction when present
    /// (same currency), otherwise the previous end-of-day balance (base currency).
    /// </summary>
    private decimal? BalanceShare(RuleTxn t, EvalContext ctx)
    {
        if (t.BalanceBefore is { } before)
            return before > 0 ? Math.Round(EffectiveAmount(ctx.Def, t) / before * 100, 4) : null;
        var b = PreviousBalance(t, ctx);
        var amount = EffectiveBase(ctx.Def, t);
        if (b is not { Balance: > 0 } pb || amount is null) return null;
        return Math.Round(amount.Value / pb.Balance * 100, 4);
    }

    private bool FilterMatches(ConditionNode? filter, RuleTxn t, EvalContext ctx)
    {
        if (filter is null) return true;
        if (filter.IsGroup)
        {
            var r = filter.Conditions!.Select(c => FilterMatches(c, t, ctx));
            return filter.Op == "and" ? r.All(x => x) : r.Any(x => x);
        }
        return Cmp(FieldValue(filter.Field!, t, ctx), filter, t, ctx, null, filter.Field!);
    }

    /// <summary>
    /// Compares with the condition's value, resolving a threshold chosen by attribute (valueBy) and lookup-list references
    /// (inList / notInList). The chosen threshold is written to the evidence when a match is given.
    /// </summary>
    private bool Cmp(object? actual, ConditionNode n, RuleTxn t, EvalContext ctx, RuleMatch? match, string label)
    {
        var expected = n.Value!.Value;
        if (n.ValueBy is { } vb)
        {
            var keyRaw = FieldValue(vb.Field, t, ctx);
            var key = keyRaw is null ? null : Convert.ToString(keyRaw, CultureInfo.InvariantCulture);
            decimal? chosen = null;
            if (key is not null)
                foreach (var kv in vb.Values)
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { chosen = kv.Value; break; }
            if (chosen is { } cv) expected = JsonSerializer.SerializeToElement(cv);
            if (match is not null)
                match.Evidence[$"{label}.threshold"] = new Dictionary<string, object?>
                {
                    ["by"] = vb.Field, ["key"] = key, ["value"] = chosen ?? (expected.ValueKind == JsonValueKind.Number ? expected.GetDecimal() : null), ["default"] = chosen is null
                };
        }
        if (n.Operator is "inList" or "notInList")
        {
            var reference = expected.GetString() ?? "";
            var codes = ctx.Data.GetLookupCodes(reference);
            var vals = actual is IEnumerable<string> l ? l.ToList() : actual is null ? new List<string>() : new List<string> { Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "" };
            var any = vals.Any(v => codes.Contains(v));
            if (match is not null) match.Evidence[$"{label}.list"] = new Dictionary<string, object?> { ["reference"] = reference, ["size"] = codes.Count };
            return n.Operator == "inList" ? any : !any;
        }
        return Compare(actual, n.Operator!, expected);
    }

    private bool EvalAggregate(ConditionNode n, EvalContext ctx, RuleMatch match)
    {
        // count/sum use prefix sums over the cached filter; other aggregates materialise the filtered window.
        var set = n.Aggregate is "count" or "sum" or "historyCount" or "historySum" or "historyRatio" ? new List<RuleTxn>() : WindowSet(n, ctx);
        decimal? value = null;
        var label = n.Label ?? n.Aggregate!;
        var used = set;
        DeferredContribution? deferred = null;
        var extraDeferred = new List<DeferredContribution>();
        switch (n.Aggregate)
        {
            case "count":
                var pc = ctx.Cache.Prefix(n);
                value = pc.Count[ctx.Hi] - pc.Count[ctx.Lo];
                deferred = new DeferredContribution(n, ctx.Lo, ctx.Hi, false);
                break;
            case "sum":
                var ps = ctx.Cache.Prefix(n);
                value = ps.Sum[ctx.Hi] - ps.Sum[ctx.Lo];
                if (ps.Missing[ctx.Hi] - ps.Missing[ctx.Lo] > 0)
                    match.Evidence[$"{label}.excludedMissingFx"] = WindowSet(n, ctx).Where(t => EffectiveBase(ctx.Def, t) is null).Select(t => t.SourceTransactionId).ToList();
                deferred = new DeferredContribution(n, ctx.Lo, ctx.Hi, true);
                break;
            case "historyCount":
            case "historySum":
            case "historyRatio":
                // Baseline: the subject's own filtered activity in the lookbackDays before the window (window excluded).
                var hp = ctx.Cache.Prefix(n);
                var hLo = LowerBound(ctx.Times, ctx.WindowStart.AddDays(-ctx.Def.LookbackDays));
                var hHi = Math.Min(LowerBound(ctx.Times, ctx.WindowStart), ctx.Lo);
                var hCount = hp.Count[hHi] - hp.Count[hLo];
                var hSum = hp.Sum[hHi] - hp.Sum[hLo];
                match.Evidence[$"{label}.lookbackDays"] = ctx.Def.LookbackDays;
                match.Evidence[$"{label}.historyCount"] = hCount;
                match.Evidence[$"{label}.historySum"] = hSum;
                if (n.Aggregate == "historyCount") value = hCount;
                else if (n.Aggregate == "historySum") value = hSum;
                else
                {
                    var windowSum = hp.Sum[ctx.Hi] - hp.Sum[ctx.Lo];
                    var windowDays = ctx.Def.Window?.Type == "Rolling" ? (decimal)ctx.Def.Window.RollingDuration.TotalDays
                        : ctx.Def.Window?.Unit switch { "Week" => 7m, "Month" => 30m, _ => 1m };
                    var average = hSum * windowDays / ctx.Def.LookbackDays;
                    match.Evidence[$"{label}.windowSum"] = windowSum;
                    match.Evidence[$"{label}.historicalAverage"] = Math.Round(average, 4);
                    if (average > 0) value = Math.Round(windowSum / average, 4);
                    deferred = new DeferredContribution(n, ctx.Lo, ctx.Hi, true);
                }
                break;
            case "distinctCount":
                var vals = set.Select(t => DistinctValue(n.Of!, t)).ToList();
                var distinct = vals.Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                value = distinct.Count;
                match.Evidence[$"{label}.values"] = distinct;
                if (vals.Any(x => x is null)) match.Evidence[$"{label}.excludedMissing"] = vals.Count(x => x is null);
                break;
            case "reciprocalCount":
                var pairs = set.GroupBy(t => t.CounterpartyKey).Where(g => g.Any(t => t.Direction == "in") && g.Any(t => t.Direction == "out"))
                    .OrderByDescending(g => g.Count()).FirstOrDefault();
                value = pairs?.Count() ?? 0;
                used = pairs?.ToList() ?? new List<RuleTxn>();
                if (pairs is not null)
                {
                    match.Evidence[$"{label}.counterparty"] = pairs.Key;
                    match.Evidence[$"{label}.in"] = pairs.Count(t => t.Direction == "in");
                    match.Evidence[$"{label}.out"] = pairs.Count(t => t.Direction == "out");
                }
                break;
            case "consecutiveDays":
                // Consecutive local days, ending on the trigger's day (up to the trigger), whose filtered total is within dailyRange.
                var cp2 = ctx.Cache.Prefix(n);
                var dayWin = new RuleWindow { Type = "Calendar", Unit = "Day" };
                var (dayStart, _, dayEnd) = WindowFor(dayWin, ctx.Trigger.TimestampUtc);
                var days = 0;
                var totals = new List<decimal>();
                var limit = ctx.Trigger.TimestampUtc.AddDays(-ctx.Def.LookbackDays);
                for (var first = true; dayEnd > limit; first = false)
                {
                    var a0 = LowerBound(ctx.Times, dayStart);
                    var b0 = first ? ctx.TriggerIndex + 1 : LowerBound(ctx.Times, dayEnd);
                    var total = cp2.Sum[b0] - cp2.Sum[a0];
                    if (total < n.DailyRange![0] || total > n.DailyRange[1]) break;
                    days++;
                    totals.Add(total);
                    extraDeferred.Add(new DeferredContribution(n, a0, b0, true));
                    var prevDay = WindowFor(dayWin, dayStart.AddTicks(-1));
                    dayStart = prevDay.Start; dayEnd = prevDay.BucketEnd;
                }
                value = days;
                match.Evidence[$"{label}.dailyTotals"] = totals;
                used = new List<RuleTxn>();
                break;
            case "newCounterpartyShare":
                var pn = ctx.Cache.Predicate(n);
                var histStart = LowerBound(ctx.Times, ctx.WindowStart.AddDays(-ctx.Def.LookbackDays));
                var known = new HashSet<string>();
                for (var i = histStart; i < Math.Min(ctx.Lo, LowerBound(ctx.Times, ctx.WindowStart)); i++) if (pn[i]) known.Add(ctx.AllEligible[i].CounterpartyKey);
                var fresh = set.Where(t => !known.Contains(t.CounterpartyKey)).ToList();
                if (set.Count > 0) value = Math.Round(fresh.Count * 100m / set.Count, 4);
                match.Evidence[$"{label}.newCounterpartyTransactions"] = fresh.Count;
                match.Evidence[$"{label}.windowTransactions"] = set.Count;
                match.Evidence[$"{label}.knownCounterparties"] = known.Count;
                used = fresh;
                break;
            case "sharedDeviceSenders":
                var senders = set.Where(t => t.Direction == "in" && t.SenderCustomerId is not null && t.SenderCustomerId != ctx.Subject.CustomerId).Select(t => t.SenderCustomerId!.Value).Distinct().ToList();
                var byDevice = senders.SelectMany(c => ctx.Data.GetDevices(c).Select(d => (Device: d, Customer: c))).GroupBy(x => x.Device)
                    .Select(g => (g.Key, Customers: g.Select(x => x.Customer).Distinct().ToList())).OrderByDescending(g => g.Customers.Count).FirstOrDefault();
                value = byDevice.Customers?.Count ?? 0;
                if (byDevice.Customers is { Count: > 0 })
                {
                    match.Evidence[$"{label}.device"] = byDevice.Key;
                    match.Evidence[$"{label}.senders"] = byDevice.Customers;
                    used = set.Where(t => t.SenderCustomerId is { } sc && byDevice.Customers.Contains(sc)).ToList();
                }
                break;
            case "typeShareChange":
                var pt = ctx.Cache.Predicate(n);
                var hs = LowerBound(ctx.Times, ctx.WindowStart.AddDays(-ctx.Def.LookbackDays));
                var he = Math.Min(ctx.Lo, LowerBound(ctx.Times, ctx.WindowStart));
                var hist = new List<RuleTxn>();
                for (var i = hs; i < he; i++) if (pt[i]) hist.Add(ctx.AllEligible[i]);
                if (hist.Count > 0 && set.Count > 0)
                {
                    var types = hist.Select(t => t.Type).Concat(set.Select(t => t.Type)).Distinct().ToList();
                    var changes = types.Select(ty => (Type: ty, Before: hist.Count(t => t.Type == ty) * 100m / hist.Count, Now: set.Count(t => t.Type == ty) * 100m / set.Count))
                        .OrderByDescending(x => Math.Abs(x.Now - x.Before)).ToList();
                    value = Math.Round(Math.Abs(changes[0].Now - changes[0].Before), 4);
                    match.Evidence[$"{label}.shares"] = changes.Select(x => new { x.Type, before = Math.Round(x.Before, 2), now = Math.Round(x.Now, 2) }).ToList();
                }
                break;
            case "minutesSinceEvent":
                if (ctx.Subject.CustomerId is { } sc2)
                {
                    var sig = ctx.Data.GetSignals(sc2).Where(x => x.OccurredAt <= ctx.Trigger.TimestampUtc).ToList();
                    RuleSignal? hit = null;
                    for (var i = sig.Count - 1; i >= 0 && hit is null; i--)
                    {
                        var x = sig[i];
                        var ok0 = n.Event switch
                        {
                            "NewDevice" => !sig.Take(i).Any(y => y.DeviceId == x.DeviceId),
                            "NewLoginCountry" => x.EventType == "Login" && x.GeoCountry is not null && !sig.Take(i).Any(y => y.EventType == "Login" && y.GeoCountry == x.GeoCountry),
                            _ => x.EventType == n.Event
                        };
                        if (ok0) hit = x;
                    }
                    if (hit is not null)
                    {
                        value = (decimal)Math.Floor((ctx.Trigger.TimestampUtc - hit.OccurredAt).TotalMinutes);
                        match.Evidence[$"{label}.event"] = n.Event;
                        match.Evidence[$"{label}.eventAtUtc"] = hit.OccurredAt;
                        match.Evidence[$"{label}.device"] = hit.DeviceId;
                        if (hit.GeoCountry is not null) match.Evidence[$"{label}.country"] = hit.GeoCountry;
                    }
                }
                used = new List<RuleTxn> { ctx.Trigger };
                break;
            case "distinctCounterparties":
                var cps = set.Select(t => t.CounterpartyKey).Distinct().ToList();
                value = cps.Count;
                match.Evidence[$"{label}.counterparties"] = cps;
                break;
            case "ratio":
                // Without a denominator filter the numerator is a subset of the window; with one, numerator and denominator are
                // selected independently (e.g. cash-out out ÷ electronic funds in).
                var den = n.Denominator is null ? set : set.Where(t => FilterMatches(n.Denominator, t, ctx)).ToList();
                var num = (n.Denominator is null ? den : set).Where(t => FilterMatches(n.Numerator, t, ctx)).ToList();
                if (n.Denominator is not null) used = den.Union(num).ToList();
                decimal Measure(List<RuleTxn> l) => n.Measure == "amount" ? l.Sum(t => EffectiveBase(ctx.Def, t) ?? 0) : l.Count;
                var d = Measure(den);
                if (d != 0) value = Math.Round(Measure(num) / d * 100, 4);
                match.Evidence[$"{label}.numerator"] = Measure(num);
                match.Evidence[$"{label}.denominator"] = d;
                if (n.Denominator is null) used = den;
                break;
            case "rapidMovement":
                decimal best = 0;
                var contrib = new List<RuleTxn>();
                foreach (var i in set.Where(t => t.Direction == "in"))
                {
                    var outs = ctx.Window.Where(o => o.Direction == "out" && o.TimestampUtc >= i.TimestampUtc && o.TimestampUtc <= i.TimestampUtc.AddMinutes(n.MaxMinutes!.Value)).ToList();
                    var inAmt = EffectiveBase(ctx.Def, i) ?? 0;
                    if (inAmt <= 0 || outs.Count == 0) continue;
                    var pct = Math.Round(outs.Sum(o => EffectiveBase(ctx.Def, o) ?? 0) / inAmt * 100, 4);
                    if (pct > best) { best = pct; contrib = outs.Append(i).ToList(); }
                }
                value = best;
                used = contrib;
                match.Evidence[$"{label}.maxMinutes"] = n.MaxMinutes;
                break;
            case "dormancyDays":
                RuleTxn? prev = null;
                for (var k = ctx.TriggerIndex - 1; k >= 0; k--)
                    if (ctx.AllEligible[k].TimestampUtc < ctx.Trigger.TimestampUtc) { prev = ctx.AllEligible[k]; break; }
                if (prev is not null)
                {
                    value = (decimal)Math.Floor((ctx.Trigger.TimestampUtc - prev.TimestampUtc).TotalDays);
                    match.Evidence[$"{label}.previousActivityUtc"] = prev.TimestampUtc;
                }
                used = new List<RuleTxn> { ctx.Trigger };
                break;
            case "profileRatio":
                var profile = ctx.Data.GetAttribute(ctx.Subject.CustomerId, n.ProfileField!, ctx.Trigger.TimestampUtc);
                var sumP = set.Sum(t => EffectiveBase(ctx.Def, t) ?? 0);
                if (TryDec(profile, out var pv) && pv > 0) value = Math.Round(sumP / pv, 4);
                else if (ctx.Subject.CustomerId is not null) ctx.Result?.MissingInputs.Add(n.ProfileField!);
                match.Evidence[$"{label}.activity"] = sumP;
                match.Evidence[$"{label}.profileField"] = n.ProfileField;
                match.Evidence[$"{label}.profileValue"] = profile;
                break;
            case "peerRatio" when n.PeerBy is { Count: > 0 }:
                if (PeerValues(n, ctx, match, label) is { } pr && Median(pr.Peers) is { } pm && pm > 0) value = Math.Round(pr.Subject / pm, 4);
                break;
            case "peerRatio":
                var subjectValue = set.Sum(t => EffectiveBase(ctx.Def, t) ?? 0);
                var peers = ctx.Subject.CustomerId is { } cid ? ctx.Data.GetPeerValues(cid, n.PeerGroup ?? "typeAndRegion", ctx.WindowStart, ctx.WindowEnd) : Array.Empty<decimal>();
                var median = Median(peers);
                if (median is > 0) value = Math.Round(subjectValue / median.Value, 4);
                match.Evidence[$"{label}.subjectValue"] = subjectValue;
                match.Evidence[$"{label}.peerCount"] = peers.Count;
                match.Evidence[$"{label}.peerMedian"] = median;
                match.Evidence[$"{label}.period"] = $"{ctx.WindowStart:O}..{ctx.WindowEnd:O}";
                break;
            case "newCustomers":
                value = ctx.Subject.CustomerId is { } aid ? ctx.Data.CountNewCustomersByAgent(aid, ctx.WindowStart, ctx.WindowEnd) : 0;
                used = new List<RuleTxn>();
                break;
            case "customersPerDevice":
                var custs = ctx.Subject.DeviceId is { } dev ? ctx.Data.CustomersOnDevice(dev, ctx.WindowStart, ctx.WindowEnd) : Array.Empty<long>();
                value = custs.Count;
                match.Evidence[$"{label}.customers"] = custs;
                break;
            case "cycle":
                var path = FindCycle(ctx, n.MaxDepth ?? 4);
                value = path is null ? 0 : 1;
                if (path is not null)
                {
                    match.Evidence[$"{label}.path"] = path.Select(e => $"{e.SenderCustomerId}->{e.ReceiverCustomerId} ({e.SourceTransactionId})").ToList();
                    used = path;
                }
                break;
            case "avgBalance":
            case "minBalance":
            case "balanceRetentionPct":
                // End-of-day balances on the local business days covered by the window (days without a report are skipped).
                var d0 = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(ctx.WindowStart, ctx.Tz));
                var d1 = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(ctx.WindowEnd, ctx.Tz));
                var bal = SubjectBalances(ctx, d0, d1);
                match.Evidence[$"{label}.daysWithBalance"] = bal.Count;
                match.Evidence[$"{label}.daysInWindow"] = d1.DayNumber - d0.DayNumber + 1;
                match.Evidence[$"{label}.balances"] = bal.OrderBy(kv => kv.Key).Take(62).ToDictionary(kv => kv.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kv => kv.Value);
                if (bal.Count == 0)
                {
                    if (ctx.Subject.CustomerId is not null || ctx.Subject.WalletExternalId is not null) ctx.Result?.MissingInputs.Add("balance");
                    used = new List<RuleTxn>();
                    break;
                }
                var avg = Math.Round(bal.Values.Average(), 4);
                if (n.Aggregate == "avgBalance") { value = avg; used = new List<RuleTxn>(); }
                else if (n.Aggregate == "minBalance") { value = bal.Values.Min(); used = new List<RuleTxn>(); }
                else
                {
                    var inflow = set.Where(t => t.Direction is "in" or "any").ToList();
                    var inSum = inflow.Sum(t => EffectiveBase(ctx.Def, t) ?? 0);
                    match.Evidence[$"{label}.averageBalance"] = avg;
                    match.Evidence[$"{label}.inflow"] = inSum;
                    if (inSum > 0) value = Math.Round(avg / inSum * 100, 4);
                    used = inflow;
                }
                break;
            case "chain":
                var chain = FindChain(n, ctx);
                value = chain?.Count ?? 0;
                if (chain is not null)
                {
                    match.Evidence[$"{label}.path"] = chain.Select(e => $"{e.SenderCustomerId}->{e.ReceiverCustomerId} {e.BaseAmount} ({e.SourceTransactionId})").ToList();
                    match.Evidence[$"{label}.customers"] = chain.Select(e => e.SenderCustomerId).Append(chain[^1].ReceiverCustomerId).ToList();
                    used = chain;
                }
                match.Evidence[$"{label}.maxMinutesPerHop"] = n.MaxMinutes;
                match.Evidence[$"{label}.minHopPct"] = n.MinHopPct ?? DefaultMinHopPct;
                break;
            case "peerPercentile":
                var pp = PeerValues(n, ctx, match, label);
                if (pp is { } p0)
                {
                    var below = p0.Peers.Count(x => x < p0.Subject);
                    value = Math.Round(below * 100m / p0.Peers.Count, 2);
                }
                break;
            case "benchmarkDeviation":
                if (ctx.Subject.CustomerId is not { } bcid) break;
                decimal? metric = null;
                if (n.Numerator is not null)
                {
                    var bden = n.Denominator is null ? set : set.Where(t => FilterMatches(n.Denominator, t, ctx)).ToList();
                    var bnum = (n.Denominator is null ? bden : set).Where(t => FilterMatches(n.Numerator, t, ctx)).ToList();
                    decimal M(List<RuleTxn> l) => n.Measure == "count" ? l.Count : l.Sum(t => EffectiveBase(ctx.Def, t) ?? 0);
                    if (M(bden) != 0) metric = Math.Round(M(bnum) / M(bden) * 100, 4);
                    match.Evidence[$"{label}.numerator"] = M(bnum);
                    match.Evidence[$"{label}.denominator"] = M(bden);
                }
                else metric = n.Measure == "count" ? set.Count : set.Sum(t => EffectiveBase(ctx.Def, t) ?? 0);
                var bm = ctx.Data.GetBenchmark(n.Benchmark!, bcid, ctx.Trigger.TimestampUtc);
                match.Evidence[$"{label}.subjectMetric"] = metric;
                if (bm is null) { ctx.Result?.MissingInputs.Add($"benchmark:{n.Benchmark}"); break; }
                match.Evidence[$"{label}.benchmark"] = new Dictionary<string, object?> { ["metric"] = bm.Metric, ["segmentField"] = bm.SegmentField, ["segmentValue"] = bm.SegmentValue, ["value"] = bm.Value, ["source"] = bm.Source };
                if (metric is not null && bm.Value != 0) value = Math.Round((metric.Value - bm.Value) / bm.Value * 100, 4);
                break;
            case "newCountries":
                var lookbackStart = ctx.WindowStart.AddDays(-ctx.Def.LookbackDays);
                var history = ctx.AllEligible.Where(t => t.TimestampUtc >= lookbackStart && t.TimestampUtc <= ctx.WindowStart)
                    .Select(t => t.DestinationCountry).Where(c => c is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var residence = ctx.Data.GetAttribute(ctx.Subject.CustomerId, "customer.residence", ctx.Trigger.TimestampUtc) as string;
                var newOnes = set.Where(t => t.DestinationCountry is not null && !history.Contains(t.DestinationCountry) &&
                                             !string.Equals(t.DestinationCountry, residence, StringComparison.OrdinalIgnoreCase)).ToList();
                value = newOnes.Select(t => t.DestinationCountry).Distinct().Count();
                used = newOnes;
                match.Evidence[$"{label}.newCountries"] = newOnes.Select(t => t.DestinationCountry).Distinct().ToList();
                match.Evidence[$"{label}.historicalCountries"] = history.ToList();
                break;
        }
        var ok = value is not null && Cmp(value, n, ctx.Trigger, ctx, match, label);
        match.Conditions.Add(new MatchedCondition(
            $"{n.Aggregate}{(n.Filter is null ? "" : "[filtered]")}{(n.Direction is null ? "" : $"({n.Direction})")} within window {n.Operator} {Show(n.Value)}",
            value is null ? "undefined" : Fmt(value), Show(n.Value), ok));
        match.Evidence[label] = value;
        if (ok)
        {
            match.Deferred.AddRange(extraDeferred);
            if (deferred is not null) match.Deferred.Add(deferred);
            else foreach (var t in used) match.ContributingTransactionIds.Add(t.Id);
        }
        return ok;
    }

    private static string? DistinctValue(string of, RuleTxn t) => of switch
    {
        "channel" => t.Channel,
        "device" => t.DeviceId,
        "sourceCountry" => t.SourceCountry,
        "destinationCountry" => t.DestinationCountry,
        "country" => t.Direction == "in" ? t.SourceCountry ?? t.DestinationCountry : t.SourceCountry ?? t.DestinationCountry,
        "type" => t.Type,
        "counterparty" => t.CounterpartyKey,
        _ => null
    };

    private static List<RuleTxn> WindowSet(ConditionNode n, EvalContext ctx)
    {
        var p = ctx.Cache.Predicate(n);
        var l = new List<RuleTxn>();
        for (var i = ctx.Lo; i < ctx.Hi; i++) if (p[i]) l.Add(ctx.AllEligible[i]);
        return l;
    }

    public const decimal DefaultMinHopPct = 50m;

    /// <summary>
    /// Longest time-ordered chain of transfers between distinct customers that passes through one of the subject's transfers in
    /// the window: each hop leaves the previous receiver within maxMinutes and carries at least minHopPct of the previous hop's amount.
    /// Returns the hops (at most maxDepth) or null when the subject has no transfer in the window.
    /// </summary>
    private static List<RuleTxn>? FindChain(ConditionNode n, EvalContext ctx)
    {
        var subjects = ctx.Subject.Members is { Count: > 0 } mem ? mem.ToHashSet() : ctx.Subject.CustomerId is { } c0 ? new HashSet<long> { c0 } : null;
        if (subjects is null) return null;
        var gap = TimeSpan.FromMinutes(n.MaxMinutes ?? 1440);
        var minPct = (n.MinHopPct ?? DefaultMinHopPct) / 100m;
        var maxHops = n.MaxDepth ?? 4;
        // Later hops follow the subject's transfer, so the search extends (maxDepth − 1) × maxMinutes past the window end
        // (bounded by the data available at evaluation time). Edges are cached per whole UTC day range.
        var horizon = ctx.WindowEnd + TimeSpan.FromTicks(gap.Ticks * (maxHops - 1));
        var edges = ctx.Data.GetNetworkEdges(ctx.WindowStart.Date, horizon.Date.AddDays(1))
            .Where(e => e.TimestampUtc > ctx.WindowStart && e.TimestampUtc <= horizon && e.BaseAmount is > 0 && e.SenderCustomerId is not null &&
                        e.ReceiverCustomerId is not null && e.SenderCustomerId != e.ReceiverCustomerId)
            .OrderBy(e => e.TimestampUtc).ToList();
        var bySender = edges.GroupBy(e => e.SenderCustomerId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var byReceiver = edges.GroupBy(e => e.ReceiverCustomerId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var budget = 20000;
        List<RuleTxn> Forward(RuleTxn from, HashSet<long> visited, int left)
        {
            var best = new List<RuleTxn>();
            if (left <= 0 || --budget < 0 || !bySender.TryGetValue(from.ReceiverCustomerId!.Value, out var outs)) return best;
            foreach (var e in outs)
            {
                if (e.TimestampUtc < from.TimestampUtc || e.TimestampUtc - from.TimestampUtc > gap || e.Id == from.Id) continue;
                if (e.BaseAmount < from.BaseAmount * minPct || visited.Contains(e.ReceiverCustomerId!.Value)) continue;
                visited.Add(e.ReceiverCustomerId.Value);
                var rest = Forward(e, visited, left - 1);
                visited.Remove(e.ReceiverCustomerId.Value);
                if (rest.Count + 1 > best.Count) best = rest.Prepend(e).ToList();
                if (best.Count >= left) break;
            }
            return best;
        }
        List<RuleTxn> Backward(RuleTxn to, HashSet<long> visited, int left)
        {
            var best = new List<RuleTxn>();
            if (left <= 0 || --budget < 0 || !byReceiver.TryGetValue(to.SenderCustomerId!.Value, out var ins)) return best;
            foreach (var e in ins)
            {
                if (e.TimestampUtc > to.TimestampUtc || to.TimestampUtc - e.TimestampUtc > gap || e.Id == to.Id) continue;
                if (to.BaseAmount < e.BaseAmount * minPct || visited.Contains(e.SenderCustomerId!.Value)) continue;
                visited.Add(e.SenderCustomerId.Value);
                var rest = Backward(e, visited, left - 1);
                visited.Remove(e.SenderCustomerId.Value);
                if (rest.Count + 1 > best.Count) best = rest.Append(e).ToList();
                if (best.Count >= left) break;
            }
            return best;
        }
        List<RuleTxn>? result = null;
        foreach (var e in edges.Where(e => e.TimestampUtc <= ctx.WindowEnd && (subjects.Contains(e.SenderCustomerId!.Value) || subjects.Contains(e.ReceiverCustomerId!.Value))))
        {
            var visited = new HashSet<long> { e.SenderCustomerId!.Value, e.ReceiverCustomerId!.Value };
            var back = Backward(e, visited, maxHops - 1);
            foreach (var b in back) visited.Add(b.SenderCustomerId!.Value);
            var fwd = Forward(e, visited, maxHops - 1 - back.Count);
            var path = back.AsEnumerable().Reverse().Append(e).Concat(fwd).ToList();
            if (result is null || path.Count > result.Count) result = path;
            if (result.Count >= maxHops || budget < 0) break;
        }
        return result;
    }

    private readonly Dictionary<(ConditionNode Node, string Signature, DateTime From, DateTime To), Dictionary<long, decimal>> _peerCache = new();

    /// <summary>
    /// Period activity (filtered amount or count) of the subject and of its peer group defined by peerBy, over the calendar period
    /// containing the trigger. Null when the subject has no peer group or fewer than minPeers peers (recorded as missing input).
    /// </summary>
    private (decimal Subject, List<decimal> Peers)? PeerValues(ConditionNode n, EvalContext ctx, RuleMatch match, string label)
    {
        if (ctx.Subject.CustomerId is not { } cid) return null;
        var group = ctx.Data.GetPeerGroup(cid, n.PeerBy!);
        if (group is not { } g) { ctx.Result?.MissingInputs.Add("peerGroup"); return null; }
        var key = (n, g.Signature, ctx.WindowStart, ctx.BucketEnd);
        if (!_peerCache.TryGetValue(key, out var vals))
        {
            vals = g.Members.Distinct().ToDictionary(m => m, _ => 0m);
            var peerCtx = new Dictionary<long, EvalContext>();
            foreach (var (c, t) in ctx.Data.GetCustomerTransactions(vals.Keys.ToList(), ctx.WindowStart, ctx.BucketEnd))
            {
                if (!vals.ContainsKey(c) || !IsEligible(ctx.Def, t) || t.TimestampUtc >= ctx.BucketEnd) continue;
                if (n.Direction is "in" or "out" && t.Direction != n.Direction) continue;
                if (!peerCtx.TryGetValue(c, out var pc))
                    peerCtx[c] = pc = new EvalContext(ctx.Def, new RuleSubjectData { SubjectKey = $"customer:{c}", CustomerId = c }, t, -1, ctx.AllEligible, 0, 0,
                        ctx.WindowStart, ctx.BucketEnd, ctx.Data, ctx.Tz, ctx.Cache);
                if (!FilterMatches(n.Filter, t, pc)) continue;
                vals[c] += n.Measure == "count" ? 1 : EffectiveBase(ctx.Def, t) ?? 0;
            }
            _peerCache[key] = vals;
        }
        var subject = vals.GetValueOrDefault(cid);
        var peers = vals.Where(kv => kv.Key != cid).Select(kv => kv.Value).ToList();
        var minPeers = n.MinPeers ?? 10;
        match.Evidence[$"{label}.peerBy"] = n.PeerBy;
        match.Evidence[$"{label}.peerGroup"] = g.Signature;
        match.Evidence[$"{label}.peerCount"] = peers.Count;
        match.Evidence[$"{label}.subjectValue"] = subject;
        match.Evidence[$"{label}.period"] = $"{ctx.WindowStart:O}..{ctx.BucketEnd:O}";
        if (peers.Count < minPeers)
        {
            match.Evidence[$"{label}.insufficientPeers"] = $"{peers.Count} < {minPeers}";
            ctx.Result?.MissingInputs.Add($"peerGroup (fewer than {minPeers} peers)");
            return null;
        }
        match.Evidence[$"{label}.peerMedian"] = Median(peers);
        var sorted = peers.OrderBy(x => x).ToList();
        match.Evidence[$"{label}.peerP90"] = sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.9))];
        return (subject, peers);
    }

    private static List<RuleTxn>? FindCycle(EvalContext ctx, int maxDepth)
    {
        if (ctx.Subject.CustomerId is not { } start) return null;
        var edges = ctx.Data.GetNetworkEdges(ctx.WindowStart, ctx.WindowEnd)
            .Where(e => e.SenderCustomerId is not null && e.ReceiverCustomerId is not null && e.SenderCustomerId != e.ReceiverCustomerId)
            .OrderBy(e => e.TimestampUtc).ToList();
        var bySender = edges.GroupBy(e => e.SenderCustomerId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var path = new List<RuleTxn>();
        var visited = new HashSet<long> { start };
        var budget = 5000;
        bool Dfs(long node, DateTime after, int depth)
        {
            if (--budget < 0 || depth > maxDepth || !bySender.TryGetValue(node, out var outs)) return false;
            foreach (var e in outs.Where(e => e.TimestampUtc >= after))
            {
                var next = e.ReceiverCustomerId!.Value;
                path.Add(e);
                if (next == start && path.Count >= 2) return true;
                if (next != start && visited.Add(next))
                {
                    if (Dfs(next, e.TimestampUtc, depth + 1)) return true;
                    visited.Remove(next);
                }
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
        return Dfs(start, DateTime.MinValue, 1) ? path : null;
    }

    private static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var s = values.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    public static bool Compare(object? actual, string op, JsonElement expected)
    {
        if (actual is null) return op == "neq" || op == "notIn";
        if (actual is bool b)
        {
            var e = expected.ValueKind == JsonValueKind.True;
            return op == "eq" ? b == e : b != e;
        }
        if (TryDec(actual, out var a))
        {
            switch (op)
            {
                case "eq": return expected.ValueKind == JsonValueKind.Number && a == expected.GetDecimal();
                case "neq": return expected.ValueKind != JsonValueKind.Number || a != expected.GetDecimal();
                case "gt": return a > expected.GetDecimal();
                case "gte": return a >= expected.GetDecimal();
                case "lt": return a < expected.GetDecimal();
                case "lte": return a <= expected.GetDecimal();
                case "between": return a >= expected[0].GetDecimal() && a <= expected[1].GetDecimal();
                case "multipleOf": var m = expected.GetDecimal(); return m > 0 && a % m == 0;
                case "in": return expected.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Number && x.GetDecimal() == a);
                case "notIn": return expected.EnumerateArray().All(x => x.ValueKind != JsonValueKind.Number || x.GetDecimal() != a);
                default: return false;
            }
        }
        if (actual is DateTime dt)
        {
            static DateTime P(JsonElement e) => DateTime.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            return op switch
            {
                "eq" => dt == P(expected), "neq" => dt != P(expected), "gt" => dt > P(expected), "gte" => dt >= P(expected),
                "lt" => dt < P(expected), "lte" => dt <= P(expected), "between" => dt >= P(expected[0]) && dt <= P(expected[1]),
                _ => false
            };
        }
        var values = actual is IEnumerable<string> list ? list.ToList() : new List<string> { Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "" };
        bool Eq(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            "eq" => values.Any(v => Eq(v, expected.GetString() ?? "")),
            "neq" => values.All(v => !Eq(v, expected.GetString() ?? "")),
            "in" => values.Any(v => expected.EnumerateArray().Any(x => Eq(v, x.GetString() ?? ""))),
            "notIn" => values.All(v => expected.EnumerateArray().All(x => !Eq(v, x.GetString() ?? ""))),
            "contains" => values.Any(v => TextContains(v, expected.GetString() ?? "")),
            _ => false
        };
    }

    /// <summary>
    /// Keyword containment tolerant of obfuscation: case, Latin accents and Arabic diacritics/letter variants are
    /// normalised, and for keywords of 4+ letters spacing and punctuation inserted between letters are ignored.
    /// </summary>
    public static bool TextContains(string text, string keyword)
    {
        if (keyword.Length == 0 || text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        static string N(string x) => Services.ArabicNameNormalizer.NormalizeLatin(Services.ArabicNameNormalizer.NormalizeArabicLetters(x));
        var t = N(text); var k = N(keyword).Trim();
        if (k.Length > 0 && t.Contains(k, StringComparison.Ordinal)) return true;
        static string C(string x) => new(x.Where(char.IsLetterOrDigit).ToArray());
        var ck = C(k);
        return ck.Length >= 4 && C(t).Contains(ck, StringComparison.Ordinal);
    }

    private static bool TryDec(object? v, out decimal d)
    {
        switch (v)
        {
            case decimal x: d = x; return true;
            case int i: d = i; return true;
            case long l: d = l; return true;
            case double db: d = (decimal)db; return true;
            default: d = 0; return false;
        }
    }

    private static string Show(JsonElement? v) => v is null ? "" : v.Value.GetRawText();
    private static string Fmt(object? v) => v switch
    {
        null => "null",
        decimal d => d.ToString("0.####", CultureInfo.InvariantCulture),
        IEnumerable<string> l => string.Join(",", l),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };
}
