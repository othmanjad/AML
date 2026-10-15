using System.Text.Json;

namespace AML.Domain.Rules;

public sealed record RuleValidationError(string Path, string Message);

public static class RuleValidator
{
    public const int MaxDepth = 6;
    public const int MaxNodes = 50;
    private static readonly string[] SignalEvents = { "Registration", "Login", "SimChange", "PhoneChange", "EmailChange", "AddressChange", "PasswordReset", "BeneficiaryAdded", "DeviceChangeApproved" };
    private static readonly string[] Subjects = { "Customer", "Wallet", "Agent", "Device", "Entity" };
    private static readonly string[] DedupComponents = { "rule", "version", "subject", "day", "window", "evidenceGroup" };
    private static readonly string[] Severities = { "Low", "Medium", "High", "Critical" };
    private static readonly string[] Statuses = { "Pending", "Completed", "Failed", "Reversed", "PartiallyReversed", "Held" };
    private static readonly string[] AllowedRootProperties =
    {
        "subject", "scope", "eligibleStatuses", "reversalHandling", "window", "condition", "dedupKey", "action", "realtimeCapable", "lookbackDays", "unresolvedParties", "entityMinConfidence", "applicability"
    };
    private static readonly string[] AllowedNodeProperties =
    {
        "op", "conditions", "field", "operator", "value", "aggregate", "filter", "numerator", "denominator", "measure", "direction",
        "maxMinutes", "profileField", "peerGroup", "maxDepth", "label", "of", "dailyRange", "event"
    };

    /// <summary>Validates raw JSON (structure, unknown properties) and the typed model.</summary>
    public static (RuleDefinition? Definition, List<RuleValidationError> Errors) ValidateJson(string json, IReadOnlyDictionary<string, string>? dynamicFieldTypes, IReadOnlyCollection<string>? actionTypes = null)
    {
        var errors = new List<RuleValidationError>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            errors.Add(new RuleValidationError($"$ (line {ex.LineNumber}, position {ex.BytePositionInLine})", "Malformed JSON: " + ex.Message.Split('.')[0]));
            return (null, errors);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new RuleValidationError("$", "Rule definition must be a JSON object."));
                return (null, errors);
            }
            foreach (var p in doc.RootElement.EnumerateObject())
                if (!AllowedRootProperties.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                    errors.Add(new RuleValidationError($"$.{p.Name}", $"Unknown property '{p.Name}'."));
            if (doc.RootElement.TryGetProperty("condition", out var c)) CheckNodeProperties(c, "$.condition", errors, 0);
            if (doc.RootElement.TryGetProperty("applicability", out var ap)) CheckNodeProperties(ap, "$.applicability", errors, 0);
            if (doc.RootElement.TryGetProperty("action", out var act) && act.ValueKind == JsonValueKind.Object && act.TryGetProperty("indicators", out var inds) && inds.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var ind in inds.EnumerateArray())
                {
                    if (ind.ValueKind == JsonValueKind.Object && ind.TryGetProperty("condition", out var ic)) CheckNodeProperties(ic, $"$.action.indicators[{i}].condition", errors, 0);
                    i++;
                }
            }
        }

        RuleDefinition? def;
        try
        {
            def = RuleDefinition.Parse(json);
        }
        catch (JsonException ex)
        {
            errors.Add(new RuleValidationError(ex.Path ?? "$", "Invalid value type: " + ex.Message.Split('.')[0]));
            return (null, errors);
        }
        errors.AddRange(Validate(def, dynamicFieldTypes, actionTypes));
        return (def, errors);
    }

    private static void CheckNodeProperties(JsonElement node, string path, List<RuleValidationError> errors, int depth)
    {
        if (depth > 40) return;
        if (node.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new RuleValidationError(path, "Condition must be an object."));
            return;
        }
        foreach (var p in node.EnumerateObject())
        {
            if (!AllowedNodeProperties.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                errors.Add(new RuleValidationError($"{path}.{p.Name}", $"Unknown property '{p.Name}'."));
            if (p.NameEquals("conditions") && p.Value.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var child in p.Value.EnumerateArray()) CheckNodeProperties(child, $"{path}.conditions[{i++}]", errors, depth + 1);
            }
            if ((p.NameEquals("filter") || p.NameEquals("numerator") || p.NameEquals("denominator")) && p.Value.ValueKind == JsonValueKind.Object)
                CheckNodeProperties(p.Value, $"{path}.{p.Name}", errors, depth + 1);
        }
    }

    public static List<RuleValidationError> Validate(RuleDefinition def, IReadOnlyDictionary<string, string>? dynamicFieldTypes, IReadOnlyCollection<string>? actionTypes = null)
    {
        var errors = new List<RuleValidationError>();
        if (!Subjects.Contains(def.Subject)) errors.Add(new("$.subject", $"Unsupported subject '{def.Subject}'. Allowed: {string.Join(", ", Subjects)}."));
        if (def.EntityMinConfidence is < 0.5m or > 1m) errors.Add(new("$.entityMinConfidence", "entityMinConfidence must be between 0.5 and 1."));
        if (def.UnresolvedParties is not ("Ignore" or "Wallet")) errors.Add(new("$.unresolvedParties", "Allowed values: Ignore, Wallet."));
        if (def.ReversalHandling is not ("Net" or "Exclude" or "Include")) errors.Add(new("$.reversalHandling", "Allowed values: Net, Exclude, Include."));
        foreach (var s in def.EligibleStatuses ?? new())
            if (!Statuses.Contains(s)) errors.Add(new("$.eligibleStatuses", $"Unknown status '{s}'."));
        if (def.Window is { } w)
        {
            if (w.Type == "Rolling")
            {
                if (w.RollingDuration <= TimeSpan.Zero || w.RollingDuration > TimeSpan.FromDays(400)) errors.Add(new("$.window", "Rolling window requires a duration between 1 minute and 400 days."));
            }
            else if (w.Type == "Calendar")
            {
                if (w.Unit is not ("Day" or "Week" or "Month")) errors.Add(new("$.window.unit", "Calendar window unit must be Day, Week or Month."));
            }
            else errors.Add(new("$.window.type", "Window type must be Rolling or Calendar."));
        }
        if (def.LookbackDays is < 0 or > 730) errors.Add(new("$.lookbackDays", "Lookback must be between 0 and 730 days."));
        foreach (var k in def.DedupKey ?? new())
            if (!DedupComponents.Contains(k)) errors.Add(new("$.dedupKey", $"Unknown dedup component '{k}'."));
        if (def.Action?.Severity is { } sev && !Severities.Contains(sev)) errors.Add(new("$.action.severity", $"Unknown severity '{sev}'."));
        if (def.Action?.ComplianceAction is { } ca && actionTypes is not null && !actionTypes.Contains(ca))
            errors.Add(new("$.action.complianceAction", $"Unknown compliance action '{ca}'."));

        for (var i = 0; i < (def.Action?.Indicators?.Count ?? 0); i++)
        {
            var ind = def.Action!.Indicators![i];
            var path = $"$.action.indicators[{i}]";
            if (string.IsNullOrWhiteSpace(ind.Label) || ind.Label.Length > 100) errors.Add(new(path + ".label", "Indicator label is required (max 100 characters)."));
            if (ind.Severity is { } isev && !Severities.Contains(isev)) errors.Add(new(path + ".severity", $"Unknown severity '{isev}'."));
            if (ind.SeverityMode is not ("Raise" or "Set")) errors.Add(new(path + ".severityMode", "severityMode must be Raise or Set."));
            if (ind.SeverityMode == "Set" && ind.Severity is null) errors.Add(new(path + ".severity", "severityMode Set requires a severity."));
            if (ind.Condition is null) { errors.Add(new(path + ".condition", "Indicator condition is required.")); continue; }
            var c = 0; var agg = false;
            ValidateNode(ind.Condition, path + ".condition", 1, dynamicFieldTypes, errors, ref c, ref agg, def, inFilter: false);
        }
        if (def.Applicability is not null)
        {
            var c = 0; var agg = false;
            ValidateNode(def.Applicability, "$.applicability", 1, dynamicFieldTypes, errors, ref c, ref agg, def, inFilter: false);
        }
        if (def.Condition is null) errors.Add(new("$.condition", "A condition is required."));
        else
        {
            var count = 0;
            var hasAggregate = false;
            ValidateNode(def.Condition, "$.condition", 1, dynamicFieldTypes, errors, ref count, ref hasAggregate, def, inFilter: false);
            if (count > MaxNodes) errors.Add(new("$.condition", $"Rule too complex: {count} conditions exceeds the maximum of {MaxNodes}."));
            if (hasAggregate && def.Window is null) errors.Add(new("$.window", "Aggregate conditions require a time window."));
        }

        if (def.RealtimeCapable)
        {
            if (def.Subject != "Customer") errors.Add(new("$.realtimeCapable", "Real-time rules must use the Customer subject."));
            if (def.Window is { } rw && !(rw.Type == "Calendar" && rw.Unit == "Day")) errors.Add(new("$.realtimeCapable", "Real-time rules may only use calendar-day windows backed by pre-computed aggregates."));
            if (def.Condition is not null && UsesHeavyAggregates(def.Condition)) errors.Add(new("$.realtimeCapable", "Real-time rules may only use unfiltered count/sum aggregates backed by pre-computed daily aggregates (no heavy historical queries inline)."));
        }
        return errors;
    }

    private static bool UsesHeavyAggregates(ConditionNode n) =>
        (n.Aggregate is not null && (!RuleAggregates.RealtimeAllowed.Contains(n.Aggregate) || n.Filter is not null))
        || (n.Conditions?.Any(UsesHeavyAggregates) ?? false);

    private static void ValidateNode(ConditionNode n, string path, int depth, IReadOnlyDictionary<string, string>? dyn,
        List<RuleValidationError> errors, ref int count, ref bool hasAggregate, RuleDefinition def, bool inFilter)
    {
        count++;
        if (depth > MaxDepth)
        {
            errors.Add(new(path, $"Rule too complex: nesting depth exceeds the maximum of {MaxDepth}."));
            return;
        }
        if (n.IsGroup)
        {
            if (n.Op is not ("and" or "or")) errors.Add(new(path + ".op", $"Unknown group operator '{n.Op}'. Allowed: and, or."));
            if (n.Conditions is null || n.Conditions.Count == 0) errors.Add(new(path + ".conditions", "A group requires at least one condition."));
            else
                for (var i = 0; i < n.Conditions.Count; i++)
                    ValidateNode(n.Conditions[i], $"{path}.conditions[{i}]", depth + 1, dyn, errors, ref count, ref hasAggregate, def, inFilter);
            return;
        }
        if (n.IsAggregate)
        {
            if (inFilter) { errors.Add(new(path, "Aggregates are not allowed inside a filter.")); return; }
            hasAggregate = true;
            if (!RuleAggregates.All.Contains(n.Aggregate)) { errors.Add(new(path + ".aggregate", $"Unknown aggregate '{n.Aggregate}'. Allowed: {string.Join(", ", RuleAggregates.All)}.")); return; }
            if (n.Operator is null || !RuleOperators.Numeric.Contains(n.Operator) || n.Operator is "in" or "notIn")
                errors.Add(new(path + ".operator", $"Aggregate '{n.Aggregate}' requires a numeric comparison operator (eq, neq, gt, gte, lt, lte, between)."));
            else ValidateValue(n, path, RuleFieldRegistry.Number, errors);
            if (n.Filter is not null) ValidateNode(n.Filter, path + ".filter", depth + 1, dyn, errors, ref count, ref hasAggregate, def, inFilter: true);
            if (n.Direction is not null && n.Direction is not ("in" or "out" or "any")) errors.Add(new(path + ".direction", "Direction must be in, out or any."));
            switch (n.Aggregate)
            {
                case "ratio":
                    if (n.Numerator is null) errors.Add(new(path + ".numerator", "Ratio requires a numerator filter."));
                    else ValidateNode(n.Numerator, path + ".numerator", depth + 1, dyn, errors, ref count, ref hasAggregate, def, inFilter: true);
                    if (n.Denominator is not null) ValidateNode(n.Denominator, path + ".denominator", depth + 1, dyn, errors, ref count, ref hasAggregate, def, inFilter: true);
                    if (n.Measure is not null && n.Measure is not ("count" or "amount")) errors.Add(new(path + ".measure", "Measure must be count or amount."));
                    break;
                case "rapidMovement":
                    if (n.MaxMinutes is null or <= 0 or > 10080) errors.Add(new(path + ".maxMinutes", "rapidMovement requires maxMinutes between 1 and 10080."));
                    break;
                case "profileRatio":
                    if (n.ProfileField is null || RuleFieldRegistry.Resolve(n.ProfileField, dyn) != RuleFieldRegistry.Number)
                        errors.Add(new(path + ".profileField", $"profileRatio requires a numeric profile field; '{n.ProfileField}' is unknown or not numeric."));
                    break;
                case "peerRatio":
                    if (n.PeerGroup is not (null or "typeAndRegion" or "type")) errors.Add(new(path + ".peerGroup", "peerGroup must be typeAndRegion or type."));
                    break;
                case "cycle":
                    if (n.MaxDepth is null or < 2 or > 6) errors.Add(new(path + ".maxDepth", "cycle requires maxDepth between 2 and 6."));
                    break;
                case "dormancyDays":
                case "newCountries":
                case "historyCount":
                case "historySum":
                case "historyRatio":
                case "newCounterpartyShare":
                case "typeShareChange":
                    if (def.LookbackDays <= 0) errors.Add(new("$.lookbackDays", $"Aggregate '{n.Aggregate}' requires lookbackDays > 0."));
                    break;
                case "distinctCount":
                    if (n.Of is not ("channel" or "device" or "sourceCountry" or "destinationCountry" or "country" or "type" or "counterparty"))
                        errors.Add(new(path + ".of", "distinctCount requires of: channel, device, sourceCountry, destinationCountry, country, type or counterparty."));
                    break;
                case "consecutiveDays":
                    if (n.DailyRange is not { Count: 2 } r || r[0] > r[1]) errors.Add(new(path + ".dailyRange", "consecutiveDays requires dailyRange [min, max]."));
                    if (def.LookbackDays <= 0) errors.Add(new("$.lookbackDays", "consecutiveDays requires lookbackDays > 0."));
                    break;
                case "minutesSinceEvent":
                    if (n.Event is null || !(n.Event is "NewDevice" or "NewLoginCountry" || SignalEvents.Contains(n.Event)))
                        errors.Add(new(path + ".event", $"minutesSinceEvent requires event: NewDevice, NewLoginCountry or one of {string.Join(", ", SignalEvents)}."));
                    break;
                case "customersPerDevice":
                    if (def.Subject != "Device") errors.Add(new(path, "customersPerDevice requires subject Device."));
                    break;
                case "newCustomers":
                    if (def.Subject != "Agent") errors.Add(new(path, "newCustomers requires subject Agent."));
                    break;
            }
            return;
        }
        if (n.Field is null) { errors.Add(new(path, "Condition must define a group (op), a field or an aggregate.")); return; }
        var type = RuleFieldRegistry.Resolve(n.Field, dyn);
        if (type is null) { errors.Add(new(path + ".field", $"Unknown field '{n.Field}': not present in the data-source registry.")); return; }
        if (n.Operator is null || !RuleOperators.All.Contains(n.Operator))
        {
            errors.Add(new(path + ".operator", $"Unknown operator '{n.Operator}'. Allowed: {string.Join(", ", RuleOperators.All)}."));
            return;
        }
        if (!RuleFieldRegistry.OperatorsFor(type).Contains(n.Operator))
        {
            errors.Add(new(path + ".operator", $"Operator '{n.Operator}' is not compatible with {type} field '{n.Field}'."));
            return;
        }
        ValidateValue(n, path, type, errors);
    }

    private static void ValidateValue(ConditionNode n, string path, string type, List<RuleValidationError> errors)
    {
        if (n.Value is not { } v || v.ValueKind == JsonValueKind.Undefined || v.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new(path + ".value", "A value is required."));
            return;
        }
        if (n.Operator is "between")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 2) { errors.Add(new(path + ".value", "between requires [low, high].")); return; }
            var a = v[0]; var b = v[1];
            if (type == RuleFieldRegistry.Number)
            {
                if (a.ValueKind != JsonValueKind.Number || b.ValueKind != JsonValueKind.Number) errors.Add(new(path + ".value", "between bounds must be numbers."));
                else if (a.GetDecimal() > b.GetDecimal()) errors.Add(new(path + ".value", "between lower bound must not exceed upper bound."));
            }
            return;
        }
        if (n.Operator is "in" or "notIn")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0) { errors.Add(new(path + ".value", $"{n.Operator} requires a non-empty list.")); return; }
            if (v.GetArrayLength() > 500) errors.Add(new(path + ".value", "List exceeds 500 values."));
            foreach (var item in v.EnumerateArray())
                if (type == RuleFieldRegistry.Number ? item.ValueKind != JsonValueKind.Number : item.ValueKind != JsonValueKind.String)
                { errors.Add(new(path + ".value", $"List values must be {(type == RuleFieldRegistry.Number ? "numbers" : "strings")}.")); break; }
            return;
        }
        switch (type)
        {
            case RuleFieldRegistry.Number when v.ValueKind != JsonValueKind.Number:
                errors.Add(new(path + ".value", "Value must be a number."));
                break;
            case RuleFieldRegistry.Bool when v.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                errors.Add(new(path + ".value", "Value must be true or false."));
                break;
            case RuleFieldRegistry.Text when v.ValueKind != JsonValueKind.String:
                errors.Add(new(path + ".value", "Value must be a string."));
                break;
            case RuleFieldRegistry.Date when v.ValueKind != JsonValueKind.String || !DateTime.TryParse(v.GetString(), out _):
                errors.Add(new(path + ".value", "Value must be an ISO date string."));
                break;
        }
        if (v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 500) errors.Add(new(path + ".value", "Value exceeds 500 characters."));
    }

    /// <summary>Collects every field reference (for dependency / where-used analysis).</summary>
    public static IEnumerable<string> ReferencedFields(RuleDefinition def)
    {
        if (def.Condition is null) yield break;
        foreach (var f in Walk(def.Condition)) yield return f;
    }

    private static IEnumerable<string> Walk(ConditionNode n)
    {
        if (n.Field is not null) yield return n.Field;
        if (n.ProfileField is not null) yield return n.ProfileField;
        foreach (var c in n.Conditions ?? new()) foreach (var f in Walk(c)) yield return f;
        foreach (var c in new[] { n.Filter, n.Numerator, n.Denominator })
            if (c is not null) foreach (var f in Walk(c)) yield return f;
    }

    /// <summary>Collects literal string values compared against a field (for option where-used analysis).</summary>
    public static IEnumerable<(string Field, string Value)> ReferencedValues(RuleDefinition def)
    {
        if (def.Condition is null) yield break;
        foreach (var x in WalkValues(def.Condition)) yield return x;
    }

    private static IEnumerable<(string, string)> WalkValues(ConditionNode n)
    {
        if (n.Field is not null && n.Value is { } v)
        {
            if (v.ValueKind == JsonValueKind.String) yield return (n.Field, v.GetString()!);
            if (v.ValueKind == JsonValueKind.Array)
                foreach (var i in v.EnumerateArray())
                    if (i.ValueKind == JsonValueKind.String) yield return (n.Field, i.GetString()!);
        }
        foreach (var c in n.Conditions ?? new()) foreach (var x in WalkValues(c)) yield return x;
        foreach (var c in new[] { n.Filter, n.Numerator, n.Denominator })
            if (c is not null) foreach (var x in WalkValues(c)) yield return x;
    }
}
