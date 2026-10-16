using System.Globalization;
using System.Text;
using AML.Domain.Entities;

namespace AML.Domain.Services;

public sealed class FactorInput
{
    /// <summary>Present, Missing, Unavailable.</summary>
    public string Status { get; init; } = "Present";
    public object? Value { get; init; }
    /// <summary>When the value was declared/observed (used for freshness rules).</summary>
    public DateTime? ObservedAt { get; init; }
    /// <summary>Reference to the source, e.g. "field:EXP_MONTHLY_CASH v2".</summary>
    public string? Reference { get; init; }
    /// <summary>Extra explanation detail (e.g. names of PEP beneficial owners).</summary>
    public string? Detail { get; init; }

    public static FactorInput Missing(string? reference = null) => new() { Status = "Missing", Reference = reference };
    public static FactorInput Unavailable(string reason) => new() { Status = "Unavailable", Detail = reason };
    public static FactorInput Of(object? value, DateTime? observedAt = null, string? reference = null, string? detail = null) =>
        value is null ? Missing(reference) : new() { Value = value, ObservedAt = observedAt, Reference = reference, Detail = detail };
}

public sealed class RiskEngineResult
{
    public decimal CalculatedScore { get; set; }
    public string CalculatedLevel { get; set; } = "";
    public string FinalLevel { get; set; } = "";
    public string? MandatoryOverrideLevel { get; set; }
    public List<string> AppliedOverrides { get; } = new();
    public List<string> AppliedFloors { get; } = new();
    public bool SanctionsWorkflow { get; set; }
    public bool RequiresApproval { get; set; }
    public bool HasMissingData { get; set; }
    public List<string> Warnings { get; } = new();
    public List<CustomerRiskAssessmentFactor> Factors { get; } = new();
    public Dictionary<string, decimal> CategoryScores { get; } = new();
    public string Explanation { get; set; } = "";
}

/// <summary>
/// Pure, deterministic risk scoring engine. Configuration is supplied as a methodology version graph; no thresholds,
/// weights or factors are hardcoded. Missing data is never silently treated as zero risk.
/// </summary>
public sealed class RiskScoringEngine
{
    public RiskEngineResult Evaluate(RiskMethodology m, string customerType, IReadOnlyDictionary<string, FactorInput> inputs, DateTime asOf)
    {
        var result = new RiskEngineResult();
        var thresholds = m.Thresholds.Where(t => IsEffective(t.EffectiveFrom, t.EffectiveTo, asOf)).OrderBy(t => t.MinScore).ToList();
        if (thresholds.Count == 0) throw new InvalidOperationException($"Methodology {m.Code} v{m.Version} has no effective thresholds.");

        var included = new List<(CustomerRiskAssessmentFactor F, decimal Score)>();
        foreach (var f in m.Factors.Where(f => f.IsActive && IsEffective(f.EffectiveFrom, f.EffectiveTo, asOf)).OrderBy(f => f.CategoryCode).ThenBy(f => f.Code))
        {
            var row = new CustomerRiskAssessmentFactor
            {
                FactorCode = f.Code, FactorName = f.Name, CategoryCode = f.CategoryCode, FactorVersion = f.Version,
                DataSource = f.DataSource, Weight = f.Weight
            };
            result.Factors.Add(row);

            if (!AppliesTo(f.ApplicableCustomerTypes, customerType))
            {
                row.DataStatus = "NotApplicable";
                row.Included = false;
                row.Explanation = $"Not applicable to customer type {customerType} (applies to {f.ApplicableCustomerTypes}); excluded from aggregation.";
                continue;
            }

            inputs.TryGetValue(f.DataSource, out var input);
            input ??= FactorInput.Missing();
            row.DataReference = input.Reference;
            row.ObservedValue = FormatValue(input.Value);

            var status = input.Status;
            if (status == "Present" && f.FreshnessDays is > 0 && input.ObservedAt is { } observed && observed < asOf.AddDays(-f.FreshnessDays.Value))
            {
                status = "Stale";
            }

            decimal? score = null;
            if (status == "Present")
            {
                var mapping = SelectMapping(f, input.Value, asOf);
                if (mapping is null)
                {
                    status = "Unmapped";
                }
                else
                {
                    score = mapping.Score;
                    row.Explanation = $"{f.Name}: value '{row.ObservedValue}' matched mapping {Describe(mapping)} => score {mapping.Score:0.##}.";
                    if (!string.IsNullOrEmpty(input.Detail)) row.Explanation += $" Detail: {input.Detail}.";
                }
            }

            if (status != "Present")
            {
                row.DataStatus = status == "Unmapped" ? "Present" : status;
                result.HasMissingData |= status != "Unmapped";
                var why = status switch
                {
                    "Stale" => $"value older than freshness limit of {f.FreshnessDays} days (observed {input.ObservedAt:yyyy-MM-dd}); handled as missing",
                    "Unavailable" => $"data source unavailable ({input.Detail}); handled as missing",
                    "Unmapped" => $"value '{row.ObservedValue}' has no configured score mapping; handled as missing",
                    _ => "no value available; handled as missing"
                };
                if (status == "Unmapped") result.Warnings.Add($"{f.Code}: unmapped value");
                if (f.MissingDataHandling == "ExcludeWithWarning")
                {
                    row.MissingHandling = "ExcludeWithWarning";
                    row.Included = false;
                    row.Explanation = $"{f.Name}: {why}. Excluded from aggregation with explicit missing-data warning (not treated as zero risk).";
                    result.Warnings.Add($"{f.Code}: {status} data excluded");
                    continue;
                }
                score = f.MissingDataScore ?? 100m;
                row.MissingHandling = $"AssignScore({score:0.##})";
                row.Explanation = $"{f.Name}: {why}. Configured missing-data score {score:0.##} applied (not zero).";
                result.Warnings.Add($"{f.Code}: {status} data scored {score:0.##}");
            }

            var s = score!.Value;
            if (s is < 0 or > 100)
            {
                result.Warnings.Add($"{f.Code}: mapped score {s} outside 0-100 clamped");
                s = Math.Clamp(s, 0, 100);
            }
            row.NormalizedScore = s;
            row.Included = true;
            included.Add((row, s));
        }

        decimal total;
        var precision = m.ScorePrecision;
        switch (m.AggregationMethod)
        {
            case AggregationMethods.MaxFactor:
                total = included.Count == 0 ? 0 : included.Max(x => x.Score);
                foreach (var (f, sc) in included) f.Contribution = sc == total ? sc : 0;
                break;
            case AggregationMethods.WeightedSumCapped:
                total = 0;
                foreach (var (f, sc) in included) { f.Contribution = Math.Round(sc * f.Weight, 6); total += f.Contribution; }
                var cap = m.Cap ?? 100m;
                if (total > cap) { result.Warnings.Add($"Weighted sum {total:0.##} capped at {cap:0.##}"); total = cap; }
                break;
            default: // WeightedAverage and CategoryMatrix (base score)
                var wsum = included.Sum(x => x.F.Weight);
                total = 0;
                if (wsum > 0)
                    foreach (var (f, sc) in included) { f.Contribution = Math.Round(sc * f.Weight / wsum, 6); total += f.Contribution; }
                break;
        }
        total = Math.Round(Math.Clamp(total, 0, 100), precision, MidpointRounding.AwayFromZero);
        result.CalculatedScore = total;
        result.CalculatedLevel = MapLevel(thresholds, total);
        var levelRank = Rank(thresholds, result.CalculatedLevel);
        var finalLevel = result.CalculatedLevel;

        // Category scores (weighted average within category)
        foreach (var g in included.GroupBy(x => x.F.CategoryCode))
        {
            var w = g.Sum(x => x.F.Weight);
            var cs = w > 0 ? g.Sum(x => x.Score * x.F.Weight) / w : g.Max(x => x.Score);
            result.CategoryScores[g.Key] = Math.Round(cs, precision, MidpointRounding.AwayFromZero);
        }

        if (m.AggregationMethod == AggregationMethods.CategoryMatrix && !string.IsNullOrWhiteSpace(m.CategoryMatrixJson))
        {
            var cells = System.Text.Json.JsonSerializer.Deserialize<List<MatrixCell>>(m.CategoryMatrixJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            foreach (var cell in cells)
            {
                var ok = cell.When.All(kv =>
                    result.CategoryScores.TryGetValue(kv.Key, out var cs) && Rank(thresholds, MapLevel(thresholds, cs)) >= Rank(thresholds, kv.Value));
                if (!ok) continue;
                finalLevel = cell.Level;
                result.CalculatedLevel = cell.Level;
                levelRank = Rank(thresholds, cell.Level);
                result.AppliedFloors.Add($"Matrix cell [{string.Join(", ", cell.When.Select(k => $"{k.Key}>={k.Value}"))}] => {cell.Level}");
                break;
            }
        }

        foreach (var floor in m.Floors)
        {
            if (!result.CategoryScores.TryGetValue(floor.CategoryCode, out var cs)) continue;
            var catLevel = MapLevel(thresholds, cs);
            if (Rank(thresholds, catLevel) >= Rank(thresholds, floor.TriggerLevel) && Rank(thresholds, floor.MinimumLevel) > Rank(thresholds, finalLevel))
            {
                finalLevel = floor.MinimumLevel;
                result.AppliedFloors.Add($"Category floor {floor.CategoryCode}: category level {catLevel} (score {cs:0.##}) >= {floor.TriggerLevel} => minimum {floor.MinimumLevel}");
            }
        }

        foreach (var o in m.Overrides.Where(o => o.IsActive && IsEffective(o.EffectiveFrom, o.EffectiveTo, asOf)).OrderByDescending(o => o.Priority))
        {
            inputs.TryGetValue(o.ConditionDataSource, out var input);
            if (input is null || input.Status != "Present" || !ConditionHolds(input.Value, o.ConditionOperator, o.ConditionValue)) continue;
            result.AppliedOverrides.Add($"{o.Code}: {o.Reason} => minimum {o.MinimumLevel}" + (string.IsNullOrEmpty(input.Detail) ? "" : $" ({input.Detail})"));
            if (o.IsSanctionsWorkflow) result.SanctionsWorkflow = true;
            if (o.RequiresApproval) result.RequiresApproval = true;
            if (Rank(thresholds, o.MinimumLevel) > Rank(thresholds, finalLevel))
            {
                finalLevel = o.MinimumLevel;
                result.MandatoryOverrideLevel = o.MinimumLevel;
            }
        }

        result.FinalLevel = finalLevel;
        var approvalLevels = Split(m.ApprovalRequiredLevels);
        if (approvalLevels.Contains(finalLevel, StringComparer.OrdinalIgnoreCase)) result.RequiresApproval = true;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Methodology {m.Code} v{m.Version} ({m.AggregationMethod}); calculated score {total.ToString("0.00", CultureInfo.InvariantCulture)} => {result.CalculatedLevel}");
        sb.Append(CultureInfo.InvariantCulture, $" (thresholds: {string.Join(", ", thresholds.Select(t => $"{t.Level} {t.MinScore:0.##}-{t.MaxScore:0.##}"))}).");
        if (result.AppliedFloors.Count > 0) sb.Append(" Floors/matrix: ").Append(string.Join("; ", result.AppliedFloors)).Append('.');
        if (result.AppliedOverrides.Count > 0) sb.Append(" Mandatory overrides: ").Append(string.Join("; ", result.AppliedOverrides)).Append('.');
        if (result.HasMissingData) sb.Append(" Missing/stale data present: ").Append(string.Join("; ", result.Warnings)).Append('.');
        sb.Append(CultureInfo.InvariantCulture, $" Final level: {finalLevel}.");
        if (result.SanctionsWorkflow) sb.Append(" Confirmed sanctions match: dedicated sanctions workflow applies; level cannot be reduced by score or manual override.");
        result.Explanation = sb.ToString();
        _ = levelRank;
        return result;
    }

    private sealed class MatrixCell
    {
        public Dictionary<string, string> When { get; set; } = new();
        public string Level { get; set; } = "";
    }

    public static string MapLevel(IReadOnlyList<RiskThreshold> thresholds, decimal score)
    {
        foreach (var t in thresholds.OrderBy(t => t.MinScore))
            if (score >= t.MinScore && score <= t.MaxScore) return t.Level;
        // Scores between ranges cannot occur after validation; fall back to the highest range below the score.
        return thresholds.Where(t => t.MinScore <= score).OrderByDescending(t => t.MinScore).Select(t => t.Level).FirstOrDefault()
               ?? thresholds.OrderBy(t => t.MinScore).First().Level;
    }

    public static int Rank(IReadOnlyList<RiskThreshold> thresholds, string level)
    {
        var ordered = thresholds.OrderBy(t => t.MinScore).Select(t => t.Level).ToList();
        var i = ordered.FindIndex(l => string.Equals(l, level, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? -1 : i;
    }

    public static RiskFactorScoreMapping? SelectMapping(RiskFactorDefinition f, object? value, DateTime asOf)
    {
        var candidates = f.Mappings.Where(mp => IsEffective(mp.EffectiveFrom, mp.EffectiveTo, asOf))
            .Where(mp => mp.MatchType != "Default" && Matches(mp, value))
            .OrderByDescending(mp => mp.Priority).ToList();
        if (candidates.Count > 0) return candidates[0];
        return f.Mappings.Where(mp => mp.MatchType == "Default" && IsEffective(mp.EffectiveFrom, mp.EffectiveTo, asOf))
            .OrderByDescending(mp => mp.Priority).FirstOrDefault();
    }

    public static bool Matches(RiskFactorScoreMapping mp, object? value)
    {
        if (value is null) return false;
        switch (mp.MatchType)
        {
            case "Equals":
                return string.Equals(FormatValue(value), mp.Value, StringComparison.OrdinalIgnoreCase);
            case "In":
                var opts = Split(mp.Value);
                if (value is IEnumerable<string> many) return many.Any(v => opts.Contains(v, StringComparer.OrdinalIgnoreCase));
                return opts.Contains(FormatValue(value) ?? "", StringComparer.OrdinalIgnoreCase);
            case "Boolean":
                var b = value is bool bb ? bb : bool.TryParse(FormatValue(value), out var pb) && pb;
                return bool.TryParse(mp.Value, out var expected) && expected == b;
            case "Range":
                if (!TryDecimal(value, out var d)) return false;
                return (mp.RangeMin is null || d >= mp.RangeMin) && (mp.RangeMax is null || d <= mp.RangeMax);
            default:
                return false;
        }
    }

    public static bool ConditionHolds(object? value, string op, string expected)
    {
        var v = FormatValue(value) ?? "";
        switch (op)
        {
            case "eq": return string.Equals(v, expected, StringComparison.OrdinalIgnoreCase);
            case "neq": return !string.Equals(v, expected, StringComparison.OrdinalIgnoreCase);
            case "in": return Split(expected).Contains(v, StringComparer.OrdinalIgnoreCase);
            case "gte": return TryDecimal(value, out var a) && decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var b1) && a >= b1;
            case "lte": return TryDecimal(value, out var c) && decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var b2) && c <= b2;
            default: return false;
        }
    }

    public static bool TryDecimal(object? value, out decimal d)
    {
        switch (value)
        {
            case decimal dd: d = dd; return true;
            case int i: d = i; return true;
            case long l: d = l; return true;
            case double db: d = (decimal)db; return true;
            case string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p): d = p; return true;
            default: d = 0; return false;
        }
    }

    public static string? FormatValue(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        decimal d => d.ToString("0.####", CultureInfo.InvariantCulture),
        IEnumerable<string> list => string.Join(",", list),
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string Describe(RiskFactorScoreMapping mp) => mp.MatchType switch
    {
        "Range" => $"[{mp.RangeMin?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-inf"}..{mp.RangeMax?.ToString("0.##", CultureInfo.InvariantCulture) ?? "+inf"}]",
        "Default" => "(default)",
        _ => $"{mp.MatchType} '{mp.Value}'"
    } + (mp.Label is null ? "" : $" '{mp.Label}'");

    public static bool IsEffective(DateTime from, DateTime? to, DateTime at) => from <= at && (to is null || at < to);

    public static bool AppliesTo(string list, string customerType) =>
        string.IsNullOrWhiteSpace(list) || Split(list).Contains(customerType, StringComparer.OrdinalIgnoreCase);

    public static string[] Split(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Validation of a methodology before publication.</summary>
public static class RiskMethodologyValidator
{
    public static List<string> Validate(RiskMethodology m)
    {
        var errors = new List<string>();
        if (!AggregationMethods.All.Contains(m.AggregationMethod)) errors.Add($"Unknown aggregation method '{m.AggregationMethod}'.");
        if (m.AggregationMethod == AggregationMethods.WeightedSumCapped && (m.Cap is null or <= 0 or > 100)) errors.Add("Weighted sum with cap requires a cap between 0 and 100.");
        if (m.ScorePrecision is < 0 or > 4) errors.Add("Score precision must be between 0 and 4 decimals.");

        var th = m.Thresholds.OrderBy(t => t.MinScore).ToList();
        if (th.Count == 0) errors.Add("At least one threshold is required.");
        else
        {
            if (th[0].MinScore != 0) errors.Add($"Thresholds must start at 0 (first range starts at {th[0].MinScore}).");
            if (th[^1].MaxScore != 100) errors.Add($"Thresholds must end at 100 (last range ends at {th[^1].MaxScore}).");
            var step = 1m / (decimal)Math.Pow(10, m.ScorePrecision);
            for (var i = 0; i < th.Count; i++)
            {
                if (th[i].MinScore > th[i].MaxScore) errors.Add($"Threshold {th[i].Level} has min greater than max.");
                if (i == 0) continue;
                var prev = th[i - 1];
                if (th[i].MinScore <= prev.MaxScore) errors.Add($"Threshold overlap between {prev.Level} ({prev.MinScore}-{prev.MaxScore}) and {th[i].Level} ({th[i].MinScore}-{th[i].MaxScore}).");
                else if (th[i].MinScore - prev.MaxScore > step) errors.Add($"Threshold gap between {prev.Level} (ends {prev.MaxScore}) and {th[i].Level} (starts {th[i].MinScore}): scores {prev.MaxScore + step}-{th[i].MinScore - step} are unmapped.");
            }
            if (th.Select(t => t.Level).Distinct(StringComparer.OrdinalIgnoreCase).Count() != th.Count) errors.Add("Threshold levels must be unique.");
        }
        var levels = th.Select(t => t.Level).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var active = m.Factors.Where(f => f.IsActive).ToList();
        if (active.Count == 0) errors.Add("At least one active factor is required.");
        if (m.AggregationMethod is AggregationMethods.WeightedAverage or AggregationMethods.CategoryMatrix && active.Sum(f => f.Weight) <= 0)
            errors.Add("Weights must sum to a positive value for weighted-average aggregation.");
        if (m.AggregationMethod == AggregationMethods.WeightedSumCapped && active.Sum(f => f.Weight) <= 0)
            errors.Add("Weights must sum to a positive value for weighted-sum aggregation.");
        foreach (var f in active)
        {
            if (f.Weight < 0) errors.Add($"Factor {f.Code}: weight cannot be negative.");
            if (string.IsNullOrWhiteSpace(f.DataSource)) errors.Add($"Factor {f.Code}: data source is required.");
            if (f.MissingDataScore is < 0 or > 100) errors.Add($"Factor {f.Code}: missing-data score must be within 0-100.");
            if (f.ScoringMode == "LookupScore")
            {
                if (string.IsNullOrWhiteSpace(f.LookupCategory)) errors.Add($"Factor {f.Code}: LookupScore mode requires a lookup category.");
            }
            else if (f.ScoringMode != "Mapping") errors.Add($"Factor {f.Code}: scoring mode must be Mapping or LookupScore.");
            else if (f.Mappings.Count == 0) errors.Add($"Factor {f.Code}: at least one score mapping is required.");
            foreach (var mp in f.Mappings)
                if (mp.Score is < 0 or > 100) errors.Add($"Factor {f.Code}: mapping score {mp.Score} is outside the normalized 0-100 range.");
            errors.AddRange(ValidateMappings(f));
        }
        foreach (var o in m.Overrides)
            if (!levels.Contains(o.MinimumLevel)) errors.Add($"Override {o.Code}: minimum level '{o.MinimumLevel}' is not a configured threshold level.");
        foreach (var fl in m.Floors)
            if (!levels.Contains(fl.MinimumLevel) || !levels.Contains(fl.TriggerLevel)) errors.Add($"Floor {fl.CategoryCode}: levels must be configured threshold levels.");
        foreach (var l in RiskScoringEngine.Split(m.ApprovalRequiredLevels))
            if (!levels.Contains(l)) errors.Add($"Approval level '{l}' is not a configured threshold level.");
        return errors;
    }

    public static List<string> ValidateMappings(RiskFactorDefinition f)
    {
        var errors = new List<string>();
        var ranges = f.Mappings.Where(m => m.MatchType == "Range").ToList();
        for (var i = 0; i < ranges.Count; i++)
        for (var j = i + 1; j < ranges.Count; j++)
        {
            var a = ranges[i];
            var b = ranges[j];
            if (a.Priority != b.Priority) continue;
            var aMin = a.RangeMin ?? decimal.MinValue; var aMax = a.RangeMax ?? decimal.MaxValue;
            var bMin = b.RangeMin ?? decimal.MinValue; var bMax = b.RangeMax ?? decimal.MaxValue;
            if (aMin <= bMax && bMin <= aMax)
                errors.Add($"Factor {f.Code}: overlapping ranges [{a.RangeMin}..{a.RangeMax}] and [{b.RangeMin}..{b.RangeMax}] with the same priority {a.Priority}.");
        }
        var equals = f.Mappings.Where(m => m.MatchType == "Equals").GroupBy(m => (m.Value?.ToUpperInvariant(), m.Priority)).Where(g => g.Count() > 1);
        foreach (var g in equals) errors.Add($"Factor {f.Code}: duplicate mapping for value '{g.Key.Item1}' with the same priority.");
        return errors;
    }
}
