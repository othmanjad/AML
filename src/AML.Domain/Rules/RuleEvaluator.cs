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
}

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
}

public sealed class RuleEvaluationResult
{
    public int RecordsEvaluated { get; set; }
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

    public RuleEvaluationResult Evaluate(RuleDefinition def, RuleSubjectData subject, DateTime rangeStartUtc, DateTime rangeEndUtc, IRuleDataProvider data)
    {
        var result = new RuleEvaluationResult();
        if (def.Condition is null) return result;
        if (def.Scope?.CustomerTypes is { Count: > 0 } types && subject.CustomerType is not null && !types.Contains(subject.CustomerType, StringComparer.OrdinalIgnoreCase))
            return result;

        var eligible = subject.Transactions.Where(t => IsEligible(def, t)).OrderBy(t => t.TimestampUtc).ThenBy(t => t.Id).ToList();
        foreach (var trigger in eligible.Where(t => t.TimestampUtc >= rangeStartUtc && t.TimestampUtc < rangeEndUtc))
        {
            result.RecordsEvaluated++;
            try
            {
                var (ws, we, bucketEnd) = WindowFor(def.Window, trigger.TimestampUtc);
                var window = eligible.Where(t => t.TimestampUtc > ws && t.TimestampUtc <= we || t.TimestampUtc == ws && def.Window?.Type == "Calendar").ToList();
                var ctx = new EvalContext(def, subject, trigger, window, eligible, ws, we, data, _tz);
                var match = new RuleMatch
                {
                    Trigger = trigger,
                    WindowStart = ws,
                    WindowEnd = def.Window?.Type == "Calendar" ? bucketEnd : we,
                    LocalDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(trigger.TimestampUtc, _tz))
                };
                if (!EvalNode(def.Condition, ctx, match)) continue;
                match.ContributingTransactionIds.Add(trigger.Id);
                match.Evidence["windowType"] = def.Window?.Type ?? "None";
                match.Evidence["windowStartUtc"] = match.WindowStart;
                match.Evidence["windowEndUtc"] = match.WindowEnd;
                match.Evidence["windowStartLocal"] = TimeZoneInfo.ConvertTimeFromUtc(match.WindowStart, _tz).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                match.Evidence["windowEndLocal"] = TimeZoneInfo.ConvertTimeFromUtc(match.WindowEnd, _tz).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                match.Evidence["timeZone"] = _tz.Id;
                match.Evidence["triggerTransaction"] = trigger.SourceTransactionId;
                var contributing = eligible.Where(t => match.ContributingTransactionIds.Contains(t.Id)).ToList();
                match.Evidence["transactions"] = contributing.Select(t => new Dictionary<string, object?>
                {
                    ["id"] = t.Id, ["sourceTransactionId"] = t.SourceTransactionId, ["timestampUtc"] = t.TimestampUtc,
                    ["originalAmount"] = t.Amount, ["currency"] = t.Currency, ["fxRate"] = t.FxRate, ["fxRateDate"] = t.FxRateDate,
                    ["fxRateVersion"] = t.FxRateVersion, ["baseAmount"] = EffectiveBase(def, t), ["baseCurrency"] = t.BaseCurrency,
                    ["fxStatus"] = t.FxStatus, ["status"] = t.Status, ["reversedAmount"] = t.ReversedAmount, ["direction"] = t.Direction
                }).ToList();
                result.Matches.Add(match);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Transaction {trigger.SourceTransactionId}: {ex.Message}");
            }
        }
        return result;
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

    private sealed record EvalContext(RuleDefinition Def, RuleSubjectData Subject, RuleTxn Trigger, List<RuleTxn> Window, List<RuleTxn> AllEligible,
        DateTime WindowStart, DateTime WindowEnd, IRuleDataProvider Data, TimeZoneInfo Tz);

    private bool EvalNode(ConditionNode n, EvalContext ctx, RuleMatch match)
    {
        if (n.IsGroup)
        {
            var results = n.Conditions!.Select(c => EvalNode(c, ctx, match)).ToList();
            return n.Op == "and" ? results.All(r => r) : results.Any(r => r);
        }
        if (n.IsAggregate) return EvalAggregate(n, ctx, match);
        var actual = FieldValue(n.Field!, ctx.Trigger, ctx);
        var ok = Compare(actual, n.Operator!, n.Value!.Value);
        match.Conditions.Add(new MatchedCondition($"{n.Field} {n.Operator} {Show(n.Value)}", Fmt(actual), Show(n.Value), ok));
        return ok;
    }

    private object? FieldValue(string field, RuleTxn t, EvalContext ctx) => field switch
    {
        "txn.amount" => EffectiveAmount(ctx.Def, t),
        "txn.baseAmount" => EffectiveBase(ctx.Def, t),
        "txn.fee" => t.Fee,
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
        _ => ctx.Data.GetAttribute(ctx.Subject.CustomerId, field, t.TimestampUtc)
    };

    private bool FilterMatches(ConditionNode? filter, RuleTxn t, EvalContext ctx)
    {
        if (filter is null) return true;
        if (filter.IsGroup)
        {
            var r = filter.Conditions!.Select(c => FilterMatches(c, t, ctx));
            return filter.Op == "and" ? r.All(x => x) : r.Any(x => x);
        }
        return Compare(FieldValue(filter.Field!, t, ctx), filter.Operator!, filter.Value!.Value);
    }

    private bool EvalAggregate(ConditionNode n, EvalContext ctx, RuleMatch match)
    {
        var set = ctx.Window.Where(t => (n.Direction is null or "any" || t.Direction == n.Direction) && FilterMatches(n.Filter, t, ctx)).ToList();
        decimal? value = null;
        var label = n.Label ?? n.Aggregate!;
        var used = set;
        switch (n.Aggregate)
        {
            case "count":
                value = set.Count;
                break;
            case "sum":
                var missing = set.Where(t => EffectiveBase(ctx.Def, t) is null).ToList();
                used = set.Except(missing).ToList();
                value = used.Sum(t => EffectiveBase(ctx.Def, t)!.Value);
                if (missing.Count > 0) match.Evidence[$"{label}.excludedMissingFx"] = missing.Select(t => t.SourceTransactionId).ToList();
                break;
            case "distinctCounterparties":
                var cps = set.Select(t => t.CounterpartyKey).Distinct().ToList();
                value = cps.Count;
                match.Evidence[$"{label}.counterparties"] = cps;
                break;
            case "ratio":
                var den = n.Denominator is null ? set : set.Where(t => FilterMatches(n.Denominator, t, ctx)).ToList();
                var num = den.Where(t => FilterMatches(n.Numerator, t, ctx)).ToList();
                decimal Measure(List<RuleTxn> l) => n.Measure == "amount" ? l.Sum(t => EffectiveBase(ctx.Def, t) ?? 0) : l.Count;
                var d = Measure(den);
                if (d != 0) value = Math.Round(Measure(num) / d * 100, 4);
                match.Evidence[$"{label}.numerator"] = Measure(num);
                match.Evidence[$"{label}.denominator"] = d;
                used = den;
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
                var prev = ctx.AllEligible.Where(t => t.TimestampUtc < ctx.Trigger.TimestampUtc && t.Id != ctx.Trigger.Id).OrderByDescending(t => t.TimestampUtc).FirstOrDefault();
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
                match.Evidence[$"{label}.activity"] = sumP;
                match.Evidence[$"{label}.profileField"] = n.ProfileField;
                match.Evidence[$"{label}.profileValue"] = profile;
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
        var ok = value is not null && Compare(value, n.Operator!, n.Value!.Value);
        match.Conditions.Add(new MatchedCondition(
            $"{n.Aggregate}{(n.Filter is null ? "" : "[filtered]")}{(n.Direction is null ? "" : $"({n.Direction})")} within window {n.Operator} {Show(n.Value)}",
            value is null ? "undefined" : Fmt(value), Show(n.Value), ok));
        match.Evidence[label] = value;
        if (ok) foreach (var t in used) match.ContributingTransactionIds.Add(t.Id);
        return ok;
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
            "contains" => values.Any(v => v.Contains(expected.GetString() ?? "", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
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
