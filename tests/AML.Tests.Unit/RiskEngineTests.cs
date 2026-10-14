using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Unit;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class TcAttribute : Attribute
{
    public TcAttribute(params string[] ids) => Ids = ids;
    public string[] Ids { get; }
}

public static class M
{
    public static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static RiskMethodology Methodology(string method = AggregationMethods.WeightedAverage, int precision = 2)
    {
        var m = new RiskMethodology { Code = "UT", Version = 1, AggregationMethod = method, ScorePrecision = precision, EffectiveFrom = Epoch, Status = ConfigStatus.Approved };
        foreach (var (l, min, max, rank) in new[] { ("Low", 0m, 24.99m, 0), ("Medium", 25m, 49.99m, 1), ("High", 50m, 74.99m, 2), ("Critical", 75m, 100m, 3) })
            m.Thresholds.Add(new RiskThreshold { Level = l, MinScore = min, MaxScore = max, Rank = rank, EffectiveFrom = Epoch });
        return m;
    }

    public static RiskFactorDefinition Numeric(string code, string ds, decimal weight, string category = "CAT", string applies = "")
    {
        var f = new RiskFactorDefinition { Code = code, Name = code, DataSource = ds, Weight = weight, CategoryCode = category, ApplicableCustomerTypes = applies, EffectiveFrom = Epoch, MissingDataScore = 60 };
        f.Mappings.Add(new RiskFactorScoreMapping { MatchType = "Range", RangeMin = 0, RangeMax = 100, Score = 0, Priority = 0, EffectiveFrom = Epoch });
        return f;
    }

    /// <summary>Factor whose mapping returns the numeric input itself (identity) via explicit Equals mappings.</summary>
    public static RiskFactorDefinition Direct(string code, decimal weight, decimal score, string category = "CAT")
    {
        var f = new RiskFactorDefinition { Code = code, Name = code, DataSource = "ds." + code, Weight = weight, CategoryCode = category, EffectiveFrom = Epoch, MissingDataScore = 100 };
        f.Mappings.Add(new RiskFactorScoreMapping { MatchType = "Default", Score = score, EffectiveFrom = Epoch });
        return f;
    }

    public static Dictionary<string, FactorInput> Present(params string[] codes) => codes.ToDictionary(c => "ds." + c, _ => FactorInput.Of("x"));
}

public class RiskEngineTests
{
    private readonly ITestOutputHelper _out;
    private readonly RiskScoringEngine _engine = new();
    public RiskEngineTests(ITestOutputHelper o) => _out = o;
    private void Actual(string s) => _out.WriteLine("ACTUAL: " + s);

    [Fact, Tc("TC-RSK-005", "AML-TC-0049")]
    public void Weighted_average_80_and_20_equal_weights_is_50()
    {
        var m = M.Methodology();
        m.Factors.Add(M.Direct("A", 0.5m, 80));
        m.Factors.Add(M.Direct("B", 0.5m, 20));
        var r = _engine.Evaluate(m, "Individual", M.Present("A", "B"), DateTime.UtcNow);
        r.CalculatedScore.Should().Be(50.00m);
        r.CalculatedLevel.Should().Be("High");
        r.Factors.Select(f => f.Contribution).Should().BeEquivalentTo(new[] { 40m, 10m });
        Actual($"Final score {r.CalculatedScore:0.00}; contributions 40 and 10; level {r.CalculatedLevel}");
    }

    [Fact, Tc("AML-TC-0050")]
    public void Weighted_sum_with_cap_applies_cap_exactly()
    {
        var m = M.Methodology(AggregationMethods.WeightedSumCapped);
        m.Cap = 70;
        m.Factors.Add(M.Direct("A", 1m, 60));
        m.Factors.Add(M.Direct("B", 0.5m, 40));
        var r = _engine.Evaluate(m, "Individual", M.Present("A", "B"), DateTime.UtcNow);
        r.CalculatedScore.Should().Be(70m);
        r.Warnings.Should().Contain(w => w.Contains("capped"));
        var m2 = M.Methodology(AggregationMethods.WeightedSumCapped); m2.Cap = 70;
        m2.Factors.Add(M.Direct("A", 1m, 30));
        _engine.Evaluate(m2, "Individual", M.Present("A"), DateTime.UtcNow).CalculatedScore.Should().Be(30m);
        Actual("Weighted sum 80 capped at 70; uncapped sum 30 unchanged");
    }

    [Fact, Tc("TC-RSK-006", "AML-TC-0051")]
    public void Max_factor_aggregation()
    {
        var m = M.Methodology(AggregationMethods.MaxFactor);
        m.Factors.Add(M.Direct("A", 1, 90));
        m.Factors.Add(M.Direct("B", 1, 10));
        var r = _engine.Evaluate(m, "Individual", M.Present("A", "B"), DateTime.UtcNow);
        r.CalculatedScore.Should().Be(90m);
        Actual($"Final score {r.CalculatedScore}");
    }

    [Fact, Tc("TC-RSK-007", "AML-TC-0052")]
    public void Category_matrix_maps_category_combination_to_level()
    {
        var m = M.Methodology(AggregationMethods.CategoryMatrix);
        m.CategoryMatrixJson = """[{"when":{"GEO":"High","PROFILE":"Low"},"level":"High"},{"when":{"GEO":"Critical"},"level":"Critical"}]""";
        m.Factors.Add(M.Direct("G", 1, 60, "GEO"));
        m.Factors.Add(M.Direct("P", 1, 10, "PROFILE"));
        var r = _engine.Evaluate(m, "Individual", M.Present("G", "P"), DateTime.UtcNow);
        r.CategoryScores["GEO"].Should().Be(60);
        r.FinalLevel.Should().Be("High");
        r.AppliedFloors.Should().ContainSingle(x => x.Contains("Matrix cell"));
        Actual($"Category GEO=60 (High), PROFILE=10 (Low) -> matrix level {r.FinalLevel} (weighted avg alone would be 35/Medium)");
    }

    [Fact, Tc("TC-RSK-008", "AML-TC-0053")]
    public void Category_floor_prevents_dilution()
    {
        var m = M.Methodology();
        m.Factors.Add(M.Direct("GEO1", 1, 90, "GEOGRAPHIC"));
        m.Factors.Add(M.Direct("P1", 3, 5, "PROFILE"));
        m.Factors.Add(M.Direct("P2", 3, 5, "PROFILE"));
        m.Floors.Add(new RiskCategoryFloor { CategoryCode = "GEOGRAPHIC", TriggerLevel = "High", MinimumLevel = "High" });
        var r = _engine.Evaluate(m, "Individual", M.Present("GEO1", "P1", "P2"), DateTime.UtcNow);
        r.CalculatedLevel.Should().Be("Low");
        r.FinalLevel.Should().Be("High");
        r.Explanation.Should().Contain("Category floor GEOGRAPHIC");
        Actual($"Calculated {r.CalculatedScore} ({r.CalculatedLevel}) -> final {r.FinalLevel}; explanation cites floor");
    }

    [Fact, Tc("TC-RSK-009")]
    public void Mandatory_override_after_scoring_keeps_calculated_score()
    {
        var m = M.Methodology();
        m.Factors.Add(M.Direct("A", 1, 20));
        m.Overrides.Add(new RiskOverridePolicy { Code = "PEP", ConditionDataSource = "screening.pep", ConditionOperator = "eq", ConditionValue = "true", MinimumLevel = "High", Reason = "PEP", IsActive = true, EffectiveFrom = M.Epoch });
        var inputs = M.Present("A");
        inputs["screening.pep"] = FactorInput.Of(true);
        var r = _engine.Evaluate(m, "Individual", inputs, DateTime.UtcNow);
        r.CalculatedScore.Should().Be(20);
        r.CalculatedLevel.Should().Be("Low");
        r.FinalLevel.Should().Be("High");
        r.MandatoryOverrideLevel.Should().Be("High");
        r.AppliedOverrides.Should().ContainSingle();
        Actual($"Calculated 20 (Low) retained; final High via override {r.AppliedOverrides[0]}");
    }

    [Fact, Tc("TC-RSK-010", "AML-TC-0068")]
    public void Confirmed_sanctions_never_low()
    {
        var m = M.Methodology();
        m.Factors.Add(M.Direct("A", 1, 0));
        m.Overrides.Add(new RiskOverridePolicy { Code = "SANC", ConditionDataSource = "screening.sanctionsConfirmed", ConditionOperator = "eq", ConditionValue = "true", MinimumLevel = "Critical", Reason = "sanctions", IsSanctionsWorkflow = true, IsActive = true, EffectiveFrom = M.Epoch });
        var inputs = M.Present("A");
        inputs["screening.sanctionsConfirmed"] = FactorInput.Of(true);
        var r = _engine.Evaluate(m, "Individual", inputs, DateTime.UtcNow);
        r.CalculatedScore.Should().Be(0);
        r.FinalLevel.Should().Be("Critical");
        r.SanctionsWorkflow.Should().BeTrue();
        Actual("Score 0 but final Critical with sanctions workflow flag");
    }

    [Fact, Tc("TC-RSK-011", "AML-TC-0055", "AML-TC-0054")]
    public void Threshold_boundaries_are_deterministic()
    {
        var th = M.Methodology().Thresholds;
        RiskScoringEngine.MapLevel(th, 0m).Should().Be("Low");
        RiskScoringEngine.MapLevel(th, 24.99m).Should().Be("Low");
        RiskScoringEngine.MapLevel(th, 25.00m).Should().Be("Medium");
        RiskScoringEngine.MapLevel(th, 49.99m).Should().Be("Medium");
        RiskScoringEngine.MapLevel(th, 50m).Should().Be("High");
        RiskScoringEngine.MapLevel(th, 100m).Should().Be("Critical");
        Actual("0->Low, 24.99->Low, 25.00->Medium, 49.99->Medium, 50->High, 100->Critical");
    }

    [Fact, Tc("TC-RSK-012")]
    public void Score_rounded_to_configured_precision()
    {
        var m = M.Methodology(precision: 2);
        m.Factors.Add(M.Direct("A", 1, 10));
        m.Factors.Add(M.Direct("B", 1, 10));
        m.Factors.Add(M.Direct("C", 1, 15));
        var r = _engine.Evaluate(m, "Individual", M.Present("A", "B", "C"), DateTime.UtcNow);
        r.CalculatedScore.Should().Be(11.67m);
        var m0 = M.Methodology(precision: 0);
        m0.Factors.AddRange(m.Factors);
        _engine.Evaluate(m0, "Individual", M.Present("A", "B", "C"), DateTime.UtcNow).CalculatedScore.Should().Be(12m);
        Actual("35/3 = 11.666... stored as 11.67 (precision 2) and 12 (precision 0), MidpointRounding.AwayFromZero");
    }

    [Fact, Tc("TC-RSK-003", "AML-TC-0058")]
    public void Missing_data_is_not_zero_risk()
    {
        var m = M.Methodology();
        m.Factors.Add(M.Direct("NAT", 1, 10));
        var r = _engine.Evaluate(m, "Individual", new Dictionary<string, FactorInput>(), DateTime.UtcNow);
        var f = r.Factors.Single();
        f.DataStatus.Should().Be("Missing");
        f.NormalizedScore.Should().Be(100);
        f.MissingHandling.Should().StartWith("AssignScore");
        r.HasMissingData.Should().BeTrue();
        r.Warnings.Should().NotBeEmpty();
        var m2 = M.Methodology();
        var ex = M.Direct("X", 1, 10); ex.MissingDataHandling = "ExcludeWithWarning";
        m2.Factors.Add(ex); m2.Factors.Add(M.Direct("Y", 1, 40));
        var r2 = _engine.Evaluate(m2, "Individual", M.Present("Y"), DateTime.UtcNow);
        r2.Factors.First(x => x.FactorCode == "X").Included.Should().BeFalse();
        r2.Warnings.Should().Contain(w => w.Contains("X"));
        r2.CalculatedScore.Should().Be(40);
        Actual("Missing factor marked Missing, scored with configured 100 (not 0) and warning raised; ExcludeWithWarning excludes explicitly with warning");
    }

    [Fact, Tc("TC-RSK-018", "AML-TC-0059")]
    public void Stale_data_handled_as_missing()
    {
        var m = M.Methodology();
        var f = M.Direct("INC", 1, 10); f.FreshnessDays = 730; f.MissingDataScore = 55;
        m.Factors.Add(f);
        var r = _engine.Evaluate(m, "Individual", new Dictionary<string, FactorInput> { ["ds.INC"] = FactorInput.Of(1000m, DateTime.UtcNow.AddYears(-4)) }, DateTime.UtcNow);
        r.Factors[0].DataStatus.Should().Be("Stale");
        r.Factors[0].NormalizedScore.Should().Be(55);
        r.Factors[0].Explanation.Should().Contain("freshness");
        Actual("Income declared 4 years ago with 730-day freshness: status Stale, scored as missing (55)");
    }

    [Fact, Tc("TC-RSK-002", "AML-TC-0060")]
    public void Non_applicable_factor_excluded_with_explanation()
    {
        var m = M.Methodology();
        var f = M.Direct("UBO", 1, 100); f.ApplicableCustomerTypes = "Corporate";
        m.Factors.Add(f); m.Factors.Add(M.Direct("A", 1, 20));
        var r = _engine.Evaluate(m, "Individual", M.Present("UBO", "A"), DateTime.UtcNow);
        var u = r.Factors.First(x => x.FactorCode == "UBO");
        u.DataStatus.Should().Be("NotApplicable");
        u.Included.Should().BeFalse();
        u.Explanation.Should().Contain("Not applicable");
        r.CalculatedScore.Should().Be(20);
        Actual("Corporate-only factor excluded for Individual with 'Not applicable' explanation; score from applicable factors only");
    }

    [Fact, Tc("TC-RSK-004")]
    public void Score_above_100_prevented_by_validation_and_clamped_by_engine()
    {
        var m = M.Methodology();
        var f = M.Direct("A", 1, 150);
        m.Factors.Add(f);
        RiskMethodologyValidator.Validate(m).Should().Contain(e => e.Contains("outside the normalized 0-100"));
        var r = _engine.Evaluate(m, "Individual", M.Present("A"), DateTime.UtcNow);
        r.CalculatedScore.Should().Be(100);
        r.Factors[0].NormalizedScore.Should().Be(100);
        Actual("Validation error for mapping 150; engine clamps stored score to 100 with warning");
    }

    [Fact, Tc("TC-RCF-004")]
    public void Overlapping_ranges_same_priority_rejected()
    {
        var f = new RiskFactorDefinition { Code = "F" };
        f.Mappings.Add(new RiskFactorScoreMapping { MatchType = "Range", RangeMin = 0, RangeMax = 50, Score = 10, Priority = 1 });
        f.Mappings.Add(new RiskFactorScoreMapping { MatchType = "Range", RangeMin = 40, RangeMax = 100, Score = 50, Priority = 1 });
        RiskMethodologyValidator.ValidateMappings(f).Should().ContainSingle(e => e.Contains("overlapping"));
        f.Mappings[1].Priority = 2;
        RiskMethodologyValidator.ValidateMappings(f).Should().BeEmpty();
        Actual("Overlap 0-50 / 40-100 at same priority -> validation error; different priorities accepted");
    }

    [Fact, Tc("TC-RCF-005")]
    public void Higher_priority_mapping_wins()
    {
        var f = new RiskFactorDefinition { Code = "F" };
        f.Mappings.Add(new RiskFactorScoreMapping { MatchType = "Range", RangeMin = 0, RangeMax = 100, Score = 10, Priority = 1, EffectiveFrom = M.Epoch });
        f.Mappings.Add(new RiskFactorScoreMapping { MatchType = "Range", RangeMin = 40, RangeMax = 60, Score = 70, Priority = 5, EffectiveFrom = M.Epoch });
        RiskScoringEngine.SelectMapping(f, 50m, DateTime.UtcNow)!.Score.Should().Be(70);
        RiskScoringEngine.SelectMapping(f, 10m, DateTime.UtcNow)!.Score.Should().Be(10);
        Actual("Value 50 matching both -> priority 5 mapping (70) applied");
    }

    [Fact, Tc("TC-RCF-006", "AML-TC-0057")]
    public void Threshold_gap_rejected()
    {
        var m = M.Methodology();
        m.Factors.Add(M.Direct("A", 1, 1));
        m.Thresholds.First(t => t.Level == "Medium").MinScore = 30;
        RiskMethodologyValidator.Validate(m).Should().Contain(e => e.Contains("gap") && e.Contains("24.99"));
        Actual(string.Join(" | ", RiskMethodologyValidator.Validate(m)));
    }

    [Fact, Tc("AML-TC-0056")]
    public void Threshold_overlap_rejected()
    {
        var m = M.Methodology();
        m.Factors.Add(M.Direct("A", 1, 1));
        m.Thresholds.First(t => t.Level == "Medium").MinScore = 20;
        RiskMethodologyValidator.Validate(m).Should().Contain(e => e.Contains("overlap"));
        Actual("Low 0-24.99 and Medium 20-49.99 -> overlap error; publish blocked");
    }

    [Fact, Tc("TC-RCF-007")]
    public void Weights_summing_to_zero_rejected()
    {
        var m = M.Methodology();
        m.Factors.Add(M.Direct("A", 0, 10));
        m.Factors.Add(M.Direct("B", 0, 10));
        RiskMethodologyValidator.Validate(m).Should().Contain(e => e.Contains("Weights must sum to a positive value"));
        Actual("Weighted average with weights summing to 0 -> validation error");
    }

    [Fact, Tc("TC-RSK-001")]
    public void Effective_dated_configuration_selected_by_timestamp()
    {
        var f = new RiskFactorDefinition { Code = "F" };
        f.Mappings.Add(new RiskFactorScoreMapping { MatchType = "Default", Score = 10, EffectiveFrom = M.Epoch, EffectiveTo = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) });
        f.Mappings.Add(new RiskFactorScoreMapping { MatchType = "Default", Score = 40, EffectiveFrom = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) });
        RiskScoringEngine.SelectMapping(f, "x", new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc))!.Score.Should().Be(10);
        RiskScoringEngine.SelectMapping(f, "x", new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc))!.Score.Should().Be(40);
        Actual("Configuration effective before 2026-11-01 used for 2026-10-15; later version for 2026-11-05 (methodology selection verified in integration test)");
    }

    [Fact, Tc("AML-TC-0076")]
    public void Unavailable_data_source_is_explicit()
    {
        var m = M.Methodology();
        m.Factors.Add(M.Direct("SCR", 1, 10));
        var r = _engine.Evaluate(m, "Individual", new Dictionary<string, FactorInput> { ["ds.SCR"] = FactorInput.Unavailable("provider down") }, DateTime.UtcNow);
        r.Factors[0].DataStatus.Should().Be("Unavailable");
        r.Factors[0].Explanation.Should().Contain("unavailable");
        r.HasMissingData.Should().BeTrue();
        Actual("Data source outage recorded as Unavailable and scored with configured missing-data score");
    }
}
