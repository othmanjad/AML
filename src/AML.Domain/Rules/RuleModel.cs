using System.Text.Json;
using System.Text.Json.Serialization;

namespace AML.Domain.Rules;

/// <summary>
/// Validated JSON rule definition. Rules are data, never code: only allow-listed fields, operators and aggregate
/// kinds are accepted, and values are always treated as typed literals (no SQL or expression execution).
/// </summary>
public sealed class RuleDefinition
{
    /// <summary>Customer, Agent, Device.</summary>
    public string Subject { get; set; } = "Customer";
    public RuleScope? Scope { get; set; }
    /// <summary>Normalized statuses counted as financial activity. Default: Completed, PartiallyReversed, Reversed (netted).</summary>
    public List<string>? EligibleStatuses { get; set; }
    /// <summary>Net (subtract reversed amounts), Exclude (drop reversed transactions), Include (gross).</summary>
    public string ReversalHandling { get; set; } = "Net";
    public RuleWindow? Window { get; set; }
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
    public decimal? RiskContribution { get; set; }
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

    /// <summary>Aggregate: count, sum, distinctCounterparties, ratio, rapidMovement, dormancyDays, profileRatio, peerRatio, newCustomers, customersPerDevice, cycle, newCountries.</summary>
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

    [JsonIgnore] public bool IsGroup => Op is not null;
    [JsonIgnore] public bool IsAggregate => Aggregate is not null;
}

public static class RuleOperators
{
    public static readonly string[] All = { "eq", "neq", "gt", "gte", "lt", "lte", "between", "in", "notIn", "contains" };
    public static readonly string[] Numeric = { "eq", "neq", "gt", "gte", "lt", "lte", "between", "in", "notIn" };
    public static readonly string[] Text = { "eq", "neq", "in", "notIn", "contains" };
    public static readonly string[] Bool = { "eq", "neq" };
    public static readonly string[] Temporal = { "eq", "neq", "gt", "gte", "lt", "lte", "between" };
}

public static class RuleAggregates
{
    public static readonly string[] All =
    {
        "count", "sum", "distinctCounterparties", "ratio", "rapidMovement", "dormancyDays", "profileRatio", "peerRatio",
        "newCustomers", "customersPerDevice", "cycle", "newCountries"
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
        ["txn.currency"] = Text, ["txn.type"] = Text, ["txn.channel"] = Text, ["txn.status"] = Text,
        ["txn.sourceCountry"] = Text, ["txn.destinationCountry"] = Text, ["txn.remarks"] = Text, ["txn.copResult"] = Text,
        ["txn.direction"] = Text, ["txn.isCrossBorder"] = Bool,
        ["customer.type"] = Text, ["customer.riskLevel"] = Text, ["customer.nationality"] = Text, ["customer.residence"] = Text,
        ["customer.status"] = Text, ["customer.region"] = Text, ["customer.isPep"] = Bool,
        ["profile.expectedMonthlyVolume"] = Number, ["profile.declaredMonthlyIncome"] = Number, ["profile.expectedMonthlyCount"] = Number,
    };

    public static string? Resolve(string field, IReadOnlyDictionary<string, string>? dynamicFieldTypes)
    {
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
