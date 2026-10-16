using System.Text.Json;
using System.Text.Json.Serialization;

namespace AML.Domain.Rules;

/// <summary>
/// Validated JSON rule definition. Rules are data, never code: only allow-listed fields, operators and aggregate
/// kinds are accepted, and values are always treated as typed literals (no SQL or expression execution).
/// </summary>
public sealed class RuleDefinition
{
    /// <summary>
    /// Customer, Wallet, Agent, Device, Entity (customers linked by entity resolution — shared identity, device, contact —
    /// with confidence ≥ EntityMinConfidence are aggregated as one subject) or Household (customers sharing the household
    /// identifier from the core system; customers without one form a household of their own).
    /// </summary>
    public string Subject { get; set; } = "Customer";
    public decimal EntityMinConfidence { get; set; } = 0.9m;
    /// <summary>
    /// Customer subjects only: Ignore (default) skips transaction sides whose party is not resolved to a customer; Wallet
    /// evaluates them at wallet level (subject "wallet:{id}") so unresolved activity is still monitored.
    /// </summary>
    public string UnresolvedParties { get; set; } = "Ignore";
    public RuleScope? Scope { get; set; }
    /// <summary>Normalized statuses counted as financial activity. Default: Completed, PartiallyReversed, Reversed (netted).</summary>
    public List<string>? EligibleStatuses { get; set; }
    /// <summary>Net (subtract reversed amounts), Exclude (drop reversed transactions), Include (gross).</summary>
    public string ReversalHandling { get; set; } = "Net";
    public RuleWindow? Window { get; set; }
    /// <summary>
    /// Optional pre-condition (e.g. sufficient history). Triggers that do not meet it are not evaluated, and the subject is
    /// recorded as "not applicable" in the execution result instead of being reported as a non-match.
    /// </summary>
    public ConditionNode? Applicability { get; set; }
    public ConditionNode? Condition { get; set; }
    /// <summary>Dedup key components: rule, version, subject, day, window, evidenceGroup.</summary>
    public List<string>? DedupKey { get; set; }
    public RuleAction? Action { get; set; }
    public bool RealtimeCapable { get; set; }
    public int LookbackDays { get; set; } = 0;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    public static RuleDefinition Parse(string json) =>
        JsonSerializer.Deserialize<RuleDefinition>(json, JsonOptions) ?? throw new JsonException("Empty rule definition.");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public IReadOnlyList<string> Statuses => EligibleStatuses is { Count: > 0 } ? EligibleStatuses : new[] { "Completed", "PartiallyReversed", "Reversed" };
}

public sealed class RuleScope
{
    public List<string>? CustomerTypes { get; set; }
    public List<string>? TransactionTypes { get; set; }
    public List<string>? Channels { get; set; }
    public List<string>? Countries { get; set; }
    public List<string>? Currencies { get; set; }
}

public sealed class RuleWindow
{
    /// <summary>Rolling or Calendar.</summary>
    public string Type { get; set; } = "Rolling";
    public int? Hours { get; set; }
    public int? Days { get; set; }
    public int? Minutes { get; set; }
    /// <summary>Calendar unit: Day, Week, Month.</summary>
    public string? Unit { get; set; }

    public TimeSpan RollingDuration => TimeSpan.FromMinutes((Minutes ?? 0) + (Hours ?? 0) * 60 + (Days ?? 0) * 1440);
}

public sealed class RuleAction
{
    public string? Severity { get; set; }
    /// <summary>Optional compliance action on match (e.g. HoldTransaction) subject to action policy.</summary>
    public string? ComplianceAction { get; set; }
    /// <summary>
    /// Secondary indicators evaluated on each match (same window and subject). A met indicator is added to the alert as a flag
    /// and, when it defines a severity, raises the alert severity to at least that level.
    /// </summary>
    public List<RuleIndicator>? Indicators { get; set; }
    public decimal? RiskContribution { get; set; }
}

public sealed class RuleIndicator
{
    public string Label { get; set; } = "";
    public ConditionNode? Condition { get; set; }
    public string? Severity { get; set; }
    /// <summary>Raise (default): severity becomes at least Severity. Set: severity becomes Severity (may lower it, e.g. approved journeys).</summary>
    public string SeverityMode { get; set; } = "Raise";
}

public sealed class ConditionNode
{
    /// <summary>Group operator: and / or.</summary>
    public string? Op { get; set; }
    public List<ConditionNode>? Conditions { get; set; }

    /// <summary>Simple field condition (evaluated on the triggering transaction / subject attributes).</summary>
    public string? Field { get; set; }
    public string? Operator { get; set; }
    public JsonElement? Value { get; set; }

    /// <summary>
    /// Aggregate: count, sum, distinctCounterparties, ratio, rapidMovement, dormancyDays, profileRatio, peerRatio, newCustomers,
    /// customersPerDevice, cycle, newCountries; baseline aggregates over the subject's own history in the lookbackDays before
    /// the window: historyCount, historySum, historyRatio (window sum ÷ historical average for a period of the window's length).
    /// </summary>
    public string? Aggregate { get; set; }
    public ConditionNode? Filter { get; set; }
    public ConditionNode? Numerator { get; set; }
    public ConditionNode? Denominator { get; set; }
    /// <summary>count or amount (ratio measure).</summary>
    public string? Measure { get; set; }
    /// <summary>in, out, any (relative to subject).</summary>
    public string? Direction { get; set; }
    public int? MaxMinutes { get; set; }
    public string? ProfileField { get; set; }
    public string? PeerGroup { get; set; }
    public int? MaxDepth { get; set; }
    public string? Label { get; set; }
    /// <summary>distinctCount: channel, device, sourceCountry, destinationCountry, country, type, counterparty.</summary>
    public string? Of { get; set; }
    /// <summary>consecutiveDays: inclusive [min, max] band for each day's filtered total.</summary>
    public List<decimal>? DailyRange { get; set; }
    /// <summary>minutesSinceEvent: a signal event type (SimChange, PhoneChange, PasswordReset, BeneficiaryAdded, Login, ...) or NewDevice / NewLoginCountry.</summary>
    public string? Event { get; set; }
    /// <summary>
    /// peerPercentile / peerRatio: customer attributes that define the peer group (customer.type, customer.region, customer.residence,
    /// customer.nationality, customer.riskLevel, field:CODE). Peers share the subject's value for every attribute.
    /// </summary>
    public List<string>? PeerBy { get; set; }
    /// <summary>peerPercentile / peerRatio with peerBy: minimum peer-group size (excluding the subject); smaller groups give no value.</summary>
    public int? MinPeers { get; set; }
    /// <summary>chain: each hop must carry at least this percentage of the previous hop's amount (default 50).</summary>
    public decimal? MinHopPct { get; set; }
    /// <summary>benchmarkDeviation: industry benchmark metric code (configured per customer segment).</summary>
    public string? Benchmark { get; set; }
    /// <summary>
    /// Threshold chosen by an attribute of the trigger or subject (e.g. customer.riskLevel, txn.destinationCountryRisk): when the
    /// attribute's value has an entry, it replaces value; otherwise value applies.
    /// </summary>
    public ValueBy? ValueBy { get; set; }

    [JsonIgnore] public bool IsGroup => Op is not null;
    [JsonIgnore] public bool IsAggregate => Aggregate is not null;
}

public sealed class ValueBy
{
    public string Field { get; set; } = "";
    public Dictionary<string, decimal> Values { get; set; } = new();
}

public static class RuleOperators
{
    public static readonly string[] All = { "eq", "neq", "gt", "gte", "lt", "lte", "between", "in", "notIn", "contains", "multipleOf", "inList", "notInList" };
    public static readonly string[] Numeric = { "eq", "neq", "gt", "gte", "lt", "lte", "between", "in", "notIn", "multipleOf" };
    /// <summary>inList / notInList compare with a maintained lookup list: "CATEGORY" or "CATEGORY:ATTRIBUTE" (e.g. COUNTRY:HighRisk).</summary>
    public static readonly string[] Text = { "eq", "neq", "in", "notIn", "contains", "inList", "notInList" };
    public static readonly string[] Bool = { "eq", "neq" };
    public static readonly string[] Temporal = { "eq", "neq", "gt", "gte", "lt", "lte", "between" };
}

public static class RuleAggregates
{
    public static readonly string[] All =
    {
        "count", "sum", "distinctCounterparties", "ratio", "rapidMovement", "dormancyDays", "profileRatio", "peerRatio",
        "newCustomers", "customersPerDevice", "cycle", "newCountries", "historyCount", "historySum", "historyRatio", "distinctCount", "reciprocalCount", "consecutiveDays", "newCounterpartyShare",
        "sharedDeviceSenders", "typeShareChange", "minutesSinceEvent", "avgBalance", "minBalance", "balanceRetentionPct", "chain", "peerPercentile",
        "benchmarkDeviation"
    };
    /// <summary>Aggregates computable from pre-computed daily aggregates (allowed for real-time evaluation).</summary>
    public static readonly string[] RealtimeAllowed = { "count", "sum" };
}

/// <summary>Typed data-source registry for rule fields. Dynamic customer fields are referenced as field:CODE.</summary>
public static class RuleFieldRegistry
{
    public const string Number = "Number";
    public const string Text = "Text";
    public const string Bool = "Bool";
    public const string Date = "Date";

    public static readonly Dictionary<string, string> Fields = new(StringComparer.Ordinal)
    {
        ["txn.amount"] = Number, ["txn.baseAmount"] = Number, ["txn.fee"] = Number, ["txn.localHour"] = Number,
        ["txn.commission"] = Number, ["txn.subType"] = Text, ["txn.mcc"] = Text, ["txn.balanceBefore"] = Number, ["txn.balanceAfter"] = Number,
        ["txn.currency"] = Text, ["txn.type"] = Text, ["txn.channel"] = Text, ["txn.status"] = Text,
        ["txn.sourceCountry"] = Text, ["txn.destinationCountry"] = Text, ["txn.remarks"] = Text, ["txn.copResult"] = Text,
        ["txn.direction"] = Text, ["txn.isCrossBorder"] = Bool, ["txn.isOwnAccount"] = Bool,
        ["txn.isLinkedParty"] = Bool, ["txn.loginCountryMismatch"] = Bool,
        ["txn.sourceCountryRisk"] = Text, ["txn.destinationCountryRisk"] = Text, ["txn.balanceSharePct"] = Number,
        ["txn.invoiceAmount"] = Number, ["txn.invoiceVariancePct"] = Number, ["txn.hasInvoice"] = Bool, ["txn.goodsCategory"] = Text,
        ["balance.previousDay"] = Number, ["customer.householdId"] = Text,
        ["customer.type"] = Text, ["customer.riskLevel"] = Text, ["customer.nationality"] = Text, ["customer.residence"] = Text,
        ["customer.status"] = Text, ["customer.region"] = Text, ["customer.isPep"] = Bool, ["customer.accountAgeDays"] = Number, ["customer.age"] = Number,
        ["profile.expectedMonthlyVolume"] = Number, ["profile.declaredMonthlyIncome"] = Number, ["profile.expectedMonthlyCount"] = Number,
    };

    /// <summary>
    /// Fields of the transaction's counterparty (the other resolved customer): "counterparty.isPep", "counterparty.accountAgeDays",
    /// "counterparty.field:CODE", ... map to the corresponding customer/profile/dynamic field.
    /// </summary>
    public const string CounterpartyPrefix = "counterparty.";

    public static string MapCounterparty(string field)
    {
        var inner = field[CounterpartyPrefix.Length..];
        return inner.StartsWith("field:", StringComparison.Ordinal) || inner.StartsWith("profile.", StringComparison.Ordinal) ? inner : "customer." + inner;
    }

    public static string? Resolve(string field, IReadOnlyDictionary<string, string>? dynamicFieldTypes)
    {
        if (field.StartsWith(CounterpartyPrefix, StringComparison.Ordinal)) return field.Length > CounterpartyPrefix.Length ? Resolve(MapCounterparty(field), dynamicFieldTypes) : null;
        if (Fields.TryGetValue(field, out var t)) return t;
        if (field.StartsWith("field:", StringComparison.Ordinal) && dynamicFieldTypes is not null &&
            dynamicFieldTypes.TryGetValue(field[6..], out var dt))
        {
            return dt switch
            {
                "Integer" or "Decimal" => Number,
                "Boolean" => Bool,
                "Date" or "DateTime" => Date,
                "MultiSelect" => Text,
                _ => Text
            };
        }
        return null;
    }

    public static string[] OperatorsFor(string type) => type switch
    {
        Number => RuleOperators.Numeric,
        Bool => RuleOperators.Bool,
        Date => RuleOperators.Temporal,
        _ => RuleOperators.Text
    };
}
