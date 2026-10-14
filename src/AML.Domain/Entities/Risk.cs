using AML.Domain.Common;

namespace AML.Domain.Entities;

public static class AggregationMethods
{
    public const string WeightedAverage = "WeightedAverage";
    public const string WeightedSumCapped = "WeightedSumCapped";
    public const string MaxFactor = "MaxFactor";
    public const string CategoryMatrix = "CategoryMatrix";
    public static readonly string[] All = { WeightedAverage, WeightedSumCapped, MaxFactor, CategoryMatrix };
}

public class RiskMethodology : TenantEntity, IConcurrencyAware
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    /// <summary>Draft, PendingApproval, Approved (active within effective window), Superseded, Retired, Rejected.</summary>
    public string Status { get; set; } = ConfigStatus.Draft;
    public string AggregationMethod { get; set; } = AggregationMethods.WeightedAverage;
    public decimal? Cap { get; set; }
    public int ScorePrecision { get; set; } = 2;
    /// <summary>Comma separated customer types the methodology applies to; empty = all.</summary>
    public string AppliesToCustomerTypes { get; set; } = "";
    /// <summary>Final levels which require approval before the current profile is updated.</summary>
    public string ApprovalRequiredLevels { get; set; } = "";
    /// <summary>JSON for CategoryMatrix: [{ "when": {"GEOGRAPHIC":"High"}, "level":"High" }]</summary>
    public string? CategoryMatrixJson { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? Owner { get; set; }
    public string? Rationale { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? SubmittedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public long? PreviousVersionId { get; set; }
    public bool IsSample { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<RiskFactorDefinition> Factors { get; set; } = new();
    public List<RiskThreshold> Thresholds { get; set; } = new();
    public List<RiskOverridePolicy> Overrides { get; set; } = new();
    public List<RiskCategoryFloor> Floors { get; set; } = new();
}

public class RiskFactorCategory : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameAr { get; set; } = "";
    public bool IsCustom { get; set; }
}

public class RiskFactorDefinition : TenantEntity, IConcurrencyAware
{
    public long MethodologyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string CategoryCode { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Typed data-source registry code, e.g. customer.nationality or field:EXP_MONTHLY_CASH.</summary>
    public string DataSource { get; set; } = "";
    /// <summary>Categorical, Numeric, Boolean.</summary>
    public string FactorType { get; set; } = "Categorical";
    public decimal Weight { get; set; }
    public string ScoringMode { get; set; } = "Mapping";
    public string ApplicableCustomerTypes { get; set; } = "";
    /// <summary>AssignScore (score MissingDataScore) or ExcludeWithWarning.</summary>
    public string MissingDataHandling { get; set; } = "AssignScore";
    public decimal? MissingDataScore { get; set; }
    public int? FreshnessDays { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public int Version { get; set; } = 1;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public List<RiskFactorScoreMapping> Mappings { get; set; } = new();
}

public class RiskFactorScoreMapping : TenantEntity
{
    public long FactorId { get; set; }
    /// <summary>Equals, In, Range, Boolean, Default.</summary>
    public string MatchType { get; set; } = "Equals";
    public string? Value { get; set; }
    public decimal? RangeMin { get; set; }
    public decimal? RangeMax { get; set; }
    public decimal Score { get; set; }
    /// <summary>Higher number wins when several mappings match.</summary>
    public int Priority { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? Label { get; set; }
}

public class RiskThreshold : TenantEntity
{
    public long MethodologyId { get; set; }
    public string Level { get; set; } = "";
    public decimal MinScore { get; set; }
    public decimal MaxScore { get; set; }
    public int ReviewMonths { get; set; } = 12;
    public int Rank { get; set; }
    public bool IsSample { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
}

/// <summary>Mandatory overrides: minimum level when a condition holds. Applied after score calculation.</summary>
public class RiskOverridePolicy : TenantEntity
{
    public long MethodologyId { get; set; }
    public string Code { get; set; } = "";
    /// <summary>Data-source code evaluated (e.g. screening.pep, screening.sanctionsConfirmed).</summary>
    public string ConditionDataSource { get; set; } = "";
    /// <summary>eq, neq, in, gte, lte.</summary>
    public string ConditionOperator { get; set; } = "eq";
    public string ConditionValue { get; set; } = "true";
    public string MinimumLevel { get; set; } = RiskLevels.High;
    public string Reason { get; set; } = "";
    public int Priority { get; set; }
    public bool RequiresApproval { get; set; }
    /// <summary>When true the dedicated sanctions workflow applies and the level can never be lowered manually.</summary>
    public bool IsSanctionsWorkflow { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
}

/// <summary>Category floor: when a category reaches TriggerLevel, the final level cannot be below MinimumLevel.</summary>
public class RiskCategoryFloor : TenantEntity
{
    public long MethodologyId { get; set; }
    public string CategoryCode { get; set; } = "";
    public string TriggerLevel { get; set; } = RiskLevels.High;
    public string MinimumLevel { get; set; } = RiskLevels.High;
}

public class CustomerRiskAssessment : TenantEntity
{
    public long CustomerId { get; set; }
    public long MethodologyId { get; set; }
    public string MethodologyCode { get; set; } = "";
    public int MethodologyVersion { get; set; }
    public string AggregationMethod { get; set; } = "";
    /// <summary>Onboarding, FieldChange, PeriodicReview, ScreeningResult, BehaviourChange, AlertEscalation, Manual, AuthorityRequest.</summary>
    public string Trigger { get; set; } = "Manual";
    public string? TriggerEventId { get; set; }
    public string? TriggerReason { get; set; }
    public string? CorrelationId { get; set; }
    public decimal CalculatedScore { get; set; }
    public string CalculatedLevel { get; set; } = "";
    public decimal? PreviousScore { get; set; }
    public string? PreviousLevel { get; set; }
    public string? MandatoryOverrideLevel { get; set; }
    public string FinalLevel { get; set; } = "";
    public string? AppliedOverrides { get; set; }
    public string? AppliedFloors { get; set; }
    public bool SanctionsWorkflow { get; set; }
    public bool HasMissingData { get; set; }
    public string Explanation { get; set; } = "";
    /// <summary>Completed, PendingApproval, Approved, Rejected.</summary>
    public string Status { get; set; } = "Completed";
    public bool RequiresApproval { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalNote { get; set; }
    public DateTime AsOf { get; set; }
    public DateTime CalculatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    /// <summary>Full methodology snapshot used, for regulator retrieval even after configuration changes.</summary>
    public string ConfigSnapshotJson { get; set; } = "";
    public List<CustomerRiskAssessmentFactor> Factors { get; set; } = new();
}

public class CustomerRiskAssessmentFactor : TenantEntity
{
    public long AssessmentId { get; set; }
    public string FactorCode { get; set; } = "";
    public string FactorName { get; set; } = "";
    public string CategoryCode { get; set; } = "";
    public int FactorVersion { get; set; }
    public string DataSource { get; set; } = "";
    public string? DataReference { get; set; }
    public string? ObservedValue { get; set; }
    /// <summary>Present, Missing, Stale, Unavailable, NotApplicable.</summary>
    public string DataStatus { get; set; } = "Present";
    public decimal? NormalizedScore { get; set; }
    public decimal Weight { get; set; }
    public decimal Contribution { get; set; }
    public bool Included { get; set; }
    public string? MissingHandling { get; set; }
    public string Explanation { get; set; } = "";
}

public class CurrentCustomerRiskProfile : TenantEntity, IConcurrencyAware
{
    public long CustomerId { get; set; }
    public decimal Score { get; set; }
    public string Level { get; set; } = "";
    public decimal CalculatedScore { get; set; }
    public string CalculatedLevel { get; set; } = "";
    public string MonitoringLevel { get; set; } = "Standard";
    public long? LastAssessmentId { get; set; }
    public DateTime? LastAssessedAt { get; set; }
    public DateOnly? NextReviewDate { get; set; }
    public bool OverrideApplied { get; set; }
    public string? OverrideType { get; set; }
    public string? OverrideReason { get; set; }
    public bool SanctionsWorkflow { get; set; }
    public string? MethodologyCode { get; set; }
    public int? MethodologyVersion { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class RiskOverrideRequest : TenantEntity, IConcurrencyAware
{
    public long CustomerId { get; set; }
    public long AssessmentId { get; set; }
    public decimal CalculatedScore { get; set; }
    public string CalculatedLevel { get; set; } = "";
    public string RequestedLevel { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Evidence { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    /// <summary>Pending, Approved, Rejected.</summary>
    public string Status { get; set; } = "Pending";
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }
    public string? FinalLevel { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class RiskReviewTask : TenantEntity, IConcurrencyAware
{
    public long CustomerId { get; set; }
    public DateOnly DueDate { get; set; }
    public string? Owner { get; set; }
    /// <summary>Open, Escalated, Completed.</summary>
    public string Status { get; set; } = "Open";
    public string Reason { get; set; } = "PeriodicReview";
    public string? EscalatedTo { get; set; }
    public DateTime? EscalatedAt { get; set; }
    public string? CompletionEvidence { get; set; }
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long? SourceAssessmentId { get; set; }
    public long? ResultAssessmentId { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
