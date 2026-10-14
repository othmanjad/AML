using AML.Domain.Common;

namespace AML.Domain.Entities;

public static class ActionTypes
{
    public const string FreezeWallet = "FreezeWallet";
    public const string BlockOutgoing = "BlockOutgoing";
    public const string BlockIncoming = "BlockIncoming";
    public const string HoldTransaction = "HoldTransaction";
    public const string ReduceLimits = "ReduceLimits";
    public const string RequireKycRefresh = "RequireKycRefresh";
    public const string SuspendAgent = "SuspendAgent";
    public const string Release = "Release";
    public const string ReleaseTransaction = "ReleaseTransaction";
    public const string RejectTransaction = "RejectTransaction";
    public static readonly string[] All = { FreezeWallet, BlockOutgoing, BlockIncoming, HoldTransaction, ReduceLimits, RequireKycRefresh, SuspendAgent, Release, ReleaseTransaction, RejectTransaction };
}

public class ComplianceAction : TenantEntity, IConcurrencyAware
{
    public string ActionType { get; set; } = "";
    public long? CustomerId { get; set; }
    public long? WalletId { get; set; }
    public long? TransactionId { get; set; }
    public string? TargetReference { get; set; }
    /// <summary>PendingApproval, Queued, PendingAck, Applied, Failed, Unknown, Rejected, Cancelled.</summary>
    public string Status { get; set; } = "Queued";
    public string IdempotencyKey { get; set; } = "";
    /// <summary>The only code exposed to external systems (tipping-off protection).</summary>
    public string NeutralCode { get; set; } = "RESTRICTED_CONTACT_COMPLIANCE";
    /// <summary>Internal reason; never sent outbound.</summary>
    public string? InternalReason { get; set; }
    public bool IsAutomatic { get; set; }
    public string? TriggerType { get; set; }
    public long? ScreeningResultId { get; set; }
    public long? CaseId { get; set; }
    public long? RelatedActionId { get; set; }
    public string RequestedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? CoreReference { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public decimal? FrozenAmount { get; set; }
    public string? FrozenCurrency { get; set; }
    public DateTime? FrozenAt { get; set; }
    public string? ReconciliationStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class ComplianceActionPolicy : TenantEntity
{
    /// <summary>ConfirmedMandatoryListMatch, ConfirmedListMatch, RuleAction, Manual.</summary>
    public string TriggerType { get; set; } = "";
    /// <summary>Optional list category filter (Sanctions, Terrorism, ProliferationFinancing).</summary>
    public string? Categories { get; set; }
    public string ActionType { get; set; } = "";
    public bool IsAutomatic { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsActive { get; set; } = true;
}

public class AuthorityNotification : TenantEntity
{
    public long ActionId { get; set; }
    public long? CustomerId { get; set; }
    public string NotificationType { get; set; } = "AssetFreezeNotification";
    public string Authority { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public DateTime DueAt { get; set; }
    public string ContentJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

public class ActionReconciliation : TenantEntity
{
    public long ActionId { get; set; }
    public string LocalState { get; set; } = "";
    public string CoreState { get; set; } = "";
    public bool IsMismatch { get; set; }
    public DateTime CheckedAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>Development-only simulated core banking/wallet system state used by the mock core adapter.</summary>
public class MockCoreCommand : Entity
{
    public long TenantId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Command { get; set; } = "";
    public string Target { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string CoreReference { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public int TimesReceived { get; set; } = 1;
}

public class MockCoreTargetState : Entity
{
    public long TenantId { get; set; }
    public string Target { get; set; } = "";
    public string State { get; set; } = "Normal";
    public decimal Balance { get; set; }
    public string Currency { get; set; } = "JOD";
    public DateTime UpdatedAt { get; set; }
}

public static class RealtimeDecisions
{
    public const string Allow = "Allow";
    public const string Hold = "HoldForReview";
    public const string Reject = "Reject";
}

public class RealtimeDecision : TenantEntity
{
    public Guid DecisionId { get; set; }
    public string SourceSystem { get; set; } = "";
    public string SourceTransactionId { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string Decision { get; set; } = RealtimeDecisions.Allow;
    public string ReasonCodes { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public int LatencyMs { get; set; }
    public bool IsFallback { get; set; }
    public string? FallbackMode { get; set; }
    public string? EvaluatedRules { get; set; }
    public string? ScreeningSummary { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string? TransactionType { get; set; }
    public string? Channel { get; set; }
    public long? CustomerId { get; set; }
    public long? TransactionId { get; set; }
    public bool NeedsPostReview { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class HoldQueueItem : TenantEntity, IConcurrencyAware
{
    public Guid? DecisionId { get; set; }
    public long? TransactionId { get; set; }
    public string SourceSystem { get; set; } = "";
    public string SourceTransactionId { get; set; } = "";
    /// <summary>Held, Released, Rejected, PostReview, Reviewed.</summary>
    public string Status { get; set; } = "Held";
    public string Reason { get; set; } = "";
    public DateTime SlaDueAt { get; set; }
    public bool IsEscalated { get; set; }
    public DateTime? EscalatedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public long? CoreActionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class RealtimeFallbackPolicy : TenantEntity
{
    public int Priority { get; set; }
    public string? TransactionType { get; set; }
    public string? Channel { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    /// <summary>FailOpen or FailClosed.</summary>
    public string Mode { get; set; } = "FailClosed";
    public string ClosedDecision { get; set; } = RealtimeDecisions.Reject;
    public bool IsActive { get; set; } = true;
}

public class PaymentTransparencyRule : TenantEntity
{
    public string Code { get; set; } = "";
    /// <summary>CrossBorder or Domestic.</summary>
    public string Corridor { get; set; } = "CrossBorder";
    public string? TransactionTypes { get; set; }
    public decimal ThresholdAmount { get; set; }
    public string ThresholdCurrency { get; set; } = "USD";
    /// <summary>Fields required at/above threshold.</summary>
    public string RequiredFields { get; set; } = "";
    /// <summary>Fields required regardless of amount.</summary>
    public string AlwaysRequiredFields { get; set; } = "";
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string Status { get; set; } = ConfigStatus.Active;
    public bool IsSample { get; set; } = true;
}
