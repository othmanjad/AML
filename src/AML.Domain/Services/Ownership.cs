namespace AML.Domain.Services;

public sealed record OwnershipEdge(string OwnerKey, string OwnerName, string OwnerType, string OwnedKey, decimal? Percentage, string ControlType, bool OwnerIsPep = false);

public sealed class BeneficialOwnerResult
{
    public string OwnerKey { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public decimal EffectivePercentage { get; set; }
    public List<string> ControlTypes { get; } = new();
    public List<string> Paths { get; } = new();
    public bool IsBeneficialOwner { get; set; }
    public string Basis { get; set; } = "";
    public bool IsPep { get; set; }
}

public sealed class OwnershipAnalysis
{
    public List<BeneficialOwnerResult> Owners { get; } = new();
    public List<string> Cycles { get; } = new();
    public List<string> Unresolved { get; } = new();
    public int MaxDepth { get; set; }
    public bool HasCycle => Cycles.Count > 0;
}

/// <summary>
/// Computes indirect ownership through multi-level chains (product of percentages along each path, summed across
/// paths). Threshold is configurable (owner is beneficial when effective % >= threshold). Natural persons with
/// control by other means / senior managing official are beneficial owners regardless of percentage.
/// Cycles are detected and reported; traversal always terminates.
/// </summary>
public static class OwnershipCalculator
{
    private static readonly string[] ControlRegardless = { "ControlOtherMeans", "SeniorManagingOfficial", "VotingRights" };
    private static readonly string[] OwnershipTypes = { "Ownership", "VotingRights" };

    public static OwnershipAnalysis Analyze(string rootKey, IReadOnlyCollection<OwnershipEdge> edges, decimal thresholdPercent, int maxDepth = 10)
    {
        var analysis = new OwnershipAnalysis();
        var byOwned = edges.GroupBy(e => e.OwnedKey).ToDictionary(g => g.Key, g => g.ToList());
        var results = new Dictionary<string, BeneficialOwnerResult>();

        void Walk(string node, decimal factor, List<string> path, int depth)
        {
            analysis.MaxDepth = Math.Max(analysis.MaxDepth, depth);
            if (!byOwned.TryGetValue(node, out var owners))
            {
                if (depth > 0 && path.Count > 0 && !IsPersonNode(node, edges))
                    analysis.Unresolved.Add($"Entity {node} has no recorded owners (path {string.Join(" <- ", path)})");
                return;
            }
            foreach (var e in owners)
            {
                if (path.Contains(e.OwnerKey) || e.OwnerKey == rootKey)
                {
                    analysis.Cycles.Add($"Circular ownership: {string.Join(" <- ", path.Append(e.OwnerKey))}");
                    continue;
                }
                if (depth >= maxDepth)
                {
                    analysis.Unresolved.Add($"Maximum depth {maxDepth} reached at {e.OwnerKey}");
                    continue;
                }
                var pct = OwnershipTypes.Contains(e.ControlType) && e.Percentage is { } p ? factor * p / 100m : 0m;
                if (e.OwnerType == "Person")
                {
                    if (!results.TryGetValue(e.OwnerKey, out var r))
                        results[e.OwnerKey] = r = new BeneficialOwnerResult { OwnerKey = e.OwnerKey, OwnerName = e.OwnerName };
                    if (e.ControlType == "Ownership") r.EffectivePercentage += pct * 100m;
                    if (!r.ControlTypes.Contains(e.ControlType)) r.ControlTypes.Add(e.ControlType);
                    r.Paths.Add(string.Join(" <- ", path.Append(e.OwnerKey)) + (e.Percentage is null ? "" : $" ({e.Percentage}%)"));
                    r.IsPep |= e.OwnerIsPep;
                }
                else if (OwnershipTypes.Contains(e.ControlType))
                {
                    Walk(e.OwnerKey, pct, path.Append(e.OwnerKey).ToList(), depth + 1);
                }
            }
        }

        Walk(rootKey, 1m, new List<string> { rootKey }, 0);
        foreach (var r in results.Values)
        {
            r.EffectivePercentage = Math.Round(r.EffectivePercentage, 4);
            if (r.EffectivePercentage >= thresholdPercent)
            {
                r.IsBeneficialOwner = true;
                r.Basis = $"Ownership {r.EffectivePercentage:0.####}% >= threshold {thresholdPercent:0.##}%";
            }
            else if (r.ControlTypes.Any(c => ControlRegardless.Contains(c)))
            {
                r.IsBeneficialOwner = true;
                r.Basis = $"Control by {string.Join("/", r.ControlTypes.Where(c => ControlRegardless.Contains(c)))} (regardless of percentage)";
            }
            else r.Basis = r.ControlTypes.Contains("Ownership")
                ? $"Ownership {r.EffectivePercentage:0.####}% below threshold {thresholdPercent:0.##}%"
                : $"{string.Join("/", r.ControlTypes)} (not an ownership interest)";
            analysis.Owners.Add(r);
        }
        analysis.Owners.Sort((a, b) => b.EffectivePercentage.CompareTo(a.EffectivePercentage));
        return analysis;
    }

    private static bool IsPersonNode(string node, IEnumerable<OwnershipEdge> edges) => edges.Any(e => e.OwnerKey == node && e.OwnerType == "Person");
}
