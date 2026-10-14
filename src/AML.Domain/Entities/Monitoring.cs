using AML.Domain.Common;

namespace AML.Domain.Entities;

public class AmlRule : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Category { get; set; } = "";
    public string RuleType { get; set; } = "Batch";
    public string Severity { get; set; } = "Medium";
    public bool IsSample { get; set; }
    public bool IsRealtimeCapable { get; set; }
    public string? TypologyCodes { get; set; }
    public long? ActiveVersionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public List<AmlRuleVersion> Versions { get; set; } = new();
}

public class AmlRuleVersion : TenantEntity, IConcurrencyAware
{
    public long RuleId { get; set; }
    public int Version { get; set; }
    /// <summary>Draft, PendingApproval, Approved, Active, Disabled, Superseded, Retired, Rejected.</summary>
    public string Status { get; set; } = ConfigStatus.Draft;
    public string DefinitionJson { get; set; } = "{}";
    public string Severity { get; set; } = "Medium";
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? Rationale { get; set; }
    public string? TestEvidence { get; set; }
    public string? ExpectedImpact { get; set; }
    public string? ValidationResult { get; set; }
    public DateTime? ValidatedAt { get; set; }
    public DateTime? SimulatedAt { get; set; }
    public string? SimulationSummary { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? SubmittedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class AmlRuleExecution : TenantEntity
{
    public long RuleId { get; set; }
    public long RuleVersionId { get; set; }
    public int RuleVersion { get; set; }
    public string RuleCode { get; set; } = "";
    /// <summary>Scheduled, Manual, Backtest, Simulation, Realtime, Tuning.</summary>
    public string Mode { get; set; } = "Scheduled";
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public int RecordsEvaluated { get; set; }
    public int Matches { get; set; }
    public int AlertsCreated { get; set; }
    public int AlertsDeduplicated { get; set; }
    public int ErrorCount { get; set; }
    public string? Errors { get; set; }
    /// <summary>Running, Succeeded, CompletedWithErrors, Failed.</summary>
    public string Status { get; set; } = "Running";
    public string CorrelationId { get; set; } = "";
    public string? ResultJson { get; set; }
    public string? TriggeredBy { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class AlertStatuses
{
    public const string New = "New";
    public const string Assigned = "Assigned";
    public const string UnderReview = "UnderReview";
    public const string PendingInformation = "PendingInformation";
    public const string Escalated = "Escalated";
    public const string PendingClosureApproval = "PendingClosureApproval";
    public const string ConvertedToCase = "ConvertedToCase";
    public const string ClosedNoSuspicion = "Closed-NoSuspicionIdentified";
    public const string ClosedFalsePositive = "Closed-FalsePositive";
    public const string ClosedDuplicate = "Closed-Duplicate";
    public const string ClosedOther = "Closed-Other";
    public static readonly string[] Closed = { ClosedNoSuspicion, ClosedFalsePositive, ClosedDuplicate, ClosedOther };
    public static bool IsClosed(string s) => Closed.Contains(s);
}

public class AmlAlert : TenantEntity, IConcurrencyAware
{
    public string AlertNumber { get; set; } = "";
    /// <summary>Rule, Screening, Realtime, PaymentTransparency, EntityResolution, Manual.</summary>
    public string Source { get; set; } = "Rule";
    public string AlertType { get; set; } = "";
    public long? RuleId { get; set; }
    public long? RuleVersionId { get; set; }
    public int? RuleVersion { get; set; }
    public string? RuleCode { get; set; }
    public long? ExecutionId { get; set; }
    public long? PrimaryCustomerId { get; set; }
    public string? SubjectKey { get; set; }
    public string Severity { get; set; } = "Medium";
    public string Status { get; set; } = AlertStatuses.New;
    public decimal? Score { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? AssignedTo { get; set; }
    public string? AssignedTeam { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? ResolutionCode { get; set; }
    public string? ResolutionNotes { get; set; }
    public string? ChecklistJson { get; set; }
    public string? PendingClosureStatus { get; set; }
    public string? ClosureRequestedBy { get; set; }
    public string? ClosureApprovedBy { get; set; }
    public string? EscalationReason { get; set; }
    public string DeduplicationKey { get; set; } = "";
    public string Confidentiality { get; set; } = Common.Confidentiality.Standard;
    public long? CaseId { get; set; }
    public bool IsBacktest { get; set; }
    public bool IsTestData { get; set; }
    public int ReopenCount { get; set; }
    /// <summary>Immutable evidence snapshot at creation (matched conditions, window, fx).</summary>
    public string EvidenceJson { get; set; } = "{}";
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class AmlAlertTransaction : TenantEntity
{
    public long AlertId { get; set; }
    public long TransactionId { get; set; }
}

public class AmlAlertCustomer : TenantEntity
{
    public long AlertId { get; set; }
    public long CustomerId { get; set; }
    public string Role { get; set; } = "Related";
}

/// <summary>Immutable record of each rule match merged into an alert.</summary>
public class AmlAlertRuleMatch : TenantEntity
{
    public long AlertId { get; set; }
    public long? ExecutionId { get; set; }
    public long? RuleVersionId { get; set; }
    public int? RuleVersion { get; set; }
    public string MatchKey { get; set; } = "";
    public string MatchedConditionsJson { get; set; } = "[]";
    public string EvidenceJson { get; set; } = "{}";
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public DateTime MatchedAt { get; set; }
}

public class AlertStatusHistory : TenantEntity
{
    public long AlertId { get; set; }
    public string FromStatus { get; set; } = "";
    public string ToStatus { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTime At { get; set; }
    public string? Reason { get; set; }
    public string? ResolutionCode { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Configurable closure policy (four-eyes and disposition content).</summary>
public class AlertClosurePolicy : TenantEntity
{
    public string Code { get; set; } = "";
    /// <summary>Severity, CustomerRiskLevel, RuleCode, Any.</summary>
    public string ConditionType { get; set; } = "Severity";
    public string ConditionValues { get; set; } = "";
    public bool RequiresSecondApproval { get; set; } = true;
    public string? RequiredChecklistItems { get; set; }
    public int MinNarrativeLength { get; set; } = 10;
    public bool IsActive { get; set; } = true;
}

public class AlertSlaPolicy : TenantEntity
{
    public string Severity { get; set; } = "";
    public int WorkingDays { get; set; } = 3;
}
