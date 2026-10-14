using AML.Domain.Common;

namespace AML.Domain.Entities;

public class Typology : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string RiskRating { get; set; } = "Medium";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class TypologyControl : TenantEntity
{
    public string TypologyCode { get; set; } = "";
    /// <summary>Rule or Control.</summary>
    public string ControlType { get; set; } = "Rule";
    public string ControlReference { get; set; } = "";
}

public class TuningRun : TenantEntity
{
    public long RuleId { get; set; }
    public long RuleVersionId { get; set; }
    /// <summary>ATL or BTL.</summary>
    public string RunType { get; set; } = "ATL";
    public string ParametersJson { get; set; } = "{}";
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int CurrentMatches { get; set; }
    public int CandidateMatches { get; set; }
    public string? SampleJson { get; set; }
    public string? ResultJson { get; set; }
    public string? ReviewNotes { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? ApprovedBy { get; set; }
}

public class QaSamplingConfig : TenantEntity
{
    public string Code { get; set; } = "";
    /// <summary>Resolution status to sample (e.g. Closed-FalsePositive) or * for all closures.</summary>
    public string Outcome { get; set; } = "*";
    public string? RuleCode { get; set; }
    public string? Analyst { get; set; }
    public string? RiskLevel { get; set; }
    public decimal Percentage { get; set; } = 10;
    public int Seed { get; set; } = 42;
    public bool IsActive { get; set; } = true;
}

public class QaSample : TenantEntity
{
    public long AlertId { get; set; }
    public string ConfigCode { get; set; } = "";
    public string Stratum { get; set; } = "";
    public string Analyst { get; set; } = "";
    public DateTime SampledAt { get; set; }
    /// <summary>Pending, Passed, ErrorFound.</summary>
    public string Status { get; set; } = "Pending";
    public string? Reviewer { get; set; }
    public string? ErrorCategory { get; set; }
    public string? Findings { get; set; }
    public bool Reopened { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

public class RetentionPolicy : TenantEntity
{
    public string RecordClass { get; set; } = "";
    public int RetentionMonths { get; set; }
    /// <summary>RelationshipEnd, TransactionDate, CaseClosure, Creation.</summary>
    public string ClockEvent { get; set; } = "RelationshipEnd";
    /// <summary>Delete or Anonymise.</summary>
    public string Disposal { get; set; } = "Anonymise";
    public string? LegalSource { get; set; }
    public string Status { get; set; } = "ToBeConfirmed";
    public bool IsActive { get; set; } = true;
}

public class LegalHold : TenantEntity
{
    public string EntityType { get; set; } = "Customer";
    public long EntityId { get; set; }
    public string Reason { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
}

public class RetentionRun : TenantEntity
{
    public bool DryRun { get; set; } = true;
    /// <summary>DryRunCompleted, PendingApproval, Approved, Executed, Rejected.</summary>
    public string Status { get; set; } = "DryRunCompleted";
    public DateTime AsOf { get; set; }
    public string CandidatesJson { get; set; } = "[]";
    public string SkippedJson { get; set; } = "[]";
    public int CandidateCount { get; set; }
    public int SkippedCount { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public int ProcessedCount { get; set; }
}

public class InstitutionalRiskAssessment : TenantEntity
{
    public string Period { get; set; } = "";
    /// <summary>Product, Channel, Geography, CustomerSegment, AgentNetwork.</summary>
    public string Dimension { get; set; } = "";
    public string Item { get; set; } = "";
    public string InherentRisk { get; set; } = "Medium";
    public string ControlEffectiveness { get; set; } = "Adequate";
    public string ResidualRisk { get; set; } = "Medium";
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Placeholder governance record for future ML models (none are used in the initial phases).</summary>
public class ModelGovernanceRecord : TenantEntity
{
    public string ModelCode { get; set; } = "";
    public string Version { get; set; } = "";
    public string Status { get; set; } = ConfigStatus.Draft;
    public string InputsSchema { get; set; } = "";
    public string OutputsSchema { get; set; } = "";
    public string Explainability { get; set; } = "";
    public string DriftMonitoring { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
