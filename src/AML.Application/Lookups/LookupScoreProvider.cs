using AML.Application.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Lookups;

/// <summary>
/// Supplies central lookup risk scores to risk factors in LookupScore mode: each approved score effective at the assessment
/// time becomes an "In" mapping of the factor (the factor's own mappings keep precedence; for multi-value fields the highest
/// code score wins). The injected mappings are part of the assessment's configuration snapshot.
/// </summary>
public sealed class LookupScoreProvider
{
    private readonly IAmlDbContext _db;
    public LookupScoreProvider(IAmlDbContext db) => _db = db;

    public async Task InjectAsync(RiskMethodology m, DateTime asOf, CancellationToken ct)
    {
        var categories = m.Factors.Where(f => f.ScoringMode == "LookupScore" && f.LookupCategory is not null).Select(f => f.LookupCategory!).Distinct().ToList();
        if (categories.Count == 0) return;
        var scores = await _db.Set<LookupRiskScore>().IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == m.TenantId && categories.Contains(s.Category) && s.Status == "Approved" && s.EffectiveFrom <= asOf && (s.EffectiveTo == null || s.EffectiveTo > asOf))
            .ToListAsync(ct);
        foreach (var f in m.Factors.Where(f => f.ScoringMode == "LookupScore"))
            foreach (var s in scores.Where(s => s.Category == f.LookupCategory))
                f.Mappings.Add(new RiskFactorScoreMapping
                {
                    TenantId = m.TenantId, FactorId = f.Id, MatchType = "In", Value = s.Code, Score = s.Score, Priority = -1_000_000 + (int)Math.Round(s.Score * 100),
                    EffectiveFrom = s.EffectiveFrom, EffectiveTo = s.EffectiveTo, Label = $"lookup {s.Category}:{s.Code}"
                });
    }
}
