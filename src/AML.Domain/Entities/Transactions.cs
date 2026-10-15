using AML.Domain.Common;

namespace AML.Domain.Entities;

public class Wallet : TenantEntity, IConcurrencyAware
{
    public string SourceSystem { get; set; } = "";
    public string ExternalWalletId { get; set; } = "";
    public long? CustomerId { get; set; }
    public string WalletType { get; set; } = "Personal";
    public string Status { get; set; } = "Active";
    public string Currency { get; set; } = "JOD";
    public long? ParentWalletId { get; set; }
    public bool IsFrozen { get; set; }
    public bool IsTestData { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>Internal wallet mapping (clearing, settlement, fee, suspense). Such wallets are never economic parties.</summary>
public class WalletMapping : TenantEntity, IConcurrencyAware
{
    public string SourceSystem { get; set; } = "";
    public string ExternalWalletId { get; set; } = "";
    /// <summary>Clearing, Settlement, Fee, Suspense, Processing.</summary>
    public string MappingCategory { get; set; } = "Clearing";
    public string? Channel { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public int RuleVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public static class TxnStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Reversed = "Reversed";
    public const string PartiallyReversed = "PartiallyReversed";
    public const string Held = "Held";

    /// <summary>Lifecycle rank used to prevent regressions from out-of-order events.</summary>
    public static int Rank(string s) => s switch
    {
        Pending => 1,
        Held => 2,
        Completed => 3,
        Failed => 3,
        PartiallyReversed => 4,
        Reversed => 5,
        _ => 0
    };
}

public class Transaction : TenantEntity, IConcurrencyAware
{
    public string SourceSystem { get; set; } = "";
    public string SourceTransactionId { get; set; } = "";
    public string? OriginalReference { get; set; }
    public string PayloadHash { get; set; } = "";
    /// <summary>Hash of business content excluding status/event time (distinguishes status updates from conflicting payloads).</summary>
    public string ContentHash { get; set; } = "";
    public string? SenderWalletExternalId { get; set; }
    public string? ReceiverWalletExternalId { get; set; }
    public long? SenderWalletId { get; set; }
    public long? ReceiverWalletId { get; set; }
    /// <summary>Resolved economic parties.</summary>
    public long? SenderCustomerId { get; set; }
    public long? ReceiverCustomerId { get; set; }
    public string TransactionType { get; set; } = "";
    public string SourceStatus { get; set; } = "";
    public string Status { get; set; } = TxnStatuses.Pending;
    public string? Channel { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public decimal? Fee { get; set; }
    public decimal? BaseAmount { get; set; }
    public string? BaseCurrency { get; set; }
    public decimal? FxRate { get; set; }
    public DateTime? FxRateDate { get; set; }
    public long? FxRateId { get; set; }
    public int? FxRateVersion { get; set; }
    /// <summary>SameCurrency, Converted, Missing.</summary>
    public string FxStatus { get; set; } = "SameCurrency";
    public decimal ReversedAmount { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTime TimestampUtc { get; set; }
    public DateTime? LastSourceEventAt { get; set; }
    public string? OriginalSourceTransactionId { get; set; }
    public long? OriginalTransactionId { get; set; }
    /// <summary>
    /// Set on the second leg of a transfer routed through an internal (clearing/settlement) wallet: both legs resolve to the
    /// same economic transfer, which is monitored once through the leg referenced here.
    /// </summary>
    public long? EconomicLegOfId { get; set; }
    public bool IsReversal { get; set; }
    public bool IsRefund { get; set; }
    public string? SourceCountry { get; set; }
    public string? DestinationCountry { get; set; }
    public long? AgentCustomerId { get; set; }
    public long? MerchantCustomerId { get; set; }
    // Payment transparency (FATF R.16)
    public string? OriginatorName { get; set; }
    public string? OriginatorAccount { get; set; }
    public string? OriginatorAddress { get; set; }
    public string? OriginatorNationalId { get; set; }
    public DateOnly? OriginatorDateOfBirth { get; set; }
    public string? OriginatorPlaceOfBirth { get; set; }
    public string? BeneficiaryName { get; set; }
    public string? BeneficiaryAccount { get; set; }
    public string? IntermediaryInstitution { get; set; }
    public string? OrderingInstitution { get; set; }
    public string? Remarks { get; set; }
    /// <summary>Confirmation-of-payee result from the core/scheme: Match, Mismatch, PartialMatch, NotChecked.</summary>
    public string? CopResult { get; set; }
    public string? PaymentTransparencyStatus { get; set; }
    public string? PaymentTransparencyIssues { get; set; }
    // Digital context
    public string? DeviceId { get; set; }
    public string? IpAddress { get; set; }
    public DateTime IngestedAt { get; set; }
    public long? IngestionRunId { get; set; }
    public string DataQualityStatus { get; set; } = "OK";
    public Guid? RealtimeDecisionId { get; set; }
    public bool IsTestData { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public decimal NetAmount => Amount - ReversedAmount;
}

public class TransactionStatusHistory : TenantEntity
{
    public long TransactionId { get; set; }
    public string SourceStatus { get; set; } = "";
    public string NormalizedStatus { get; set; } = "";
    public DateTime? SourceEventAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public bool Applied { get; set; }
    public string? Note { get; set; }
}

public class TransactionPartyResolution : TenantEntity
{
    public long TransactionId { get; set; }
    /// <summary>Sender or Receiver.</summary>
    public string Side { get; set; } = "";
    public string? OriginalWalletExternalId { get; set; }
    public long? ResolvedCustomerId { get; set; }
    /// <summary>WalletOwner, InternalWalletCounterLeg, OriginatorData, None.</summary>
    public string Method { get; set; } = "";
    /// <summary>Resolved, InternalWallet, Unresolved.</summary>
    public string Status { get; set; } = "";
    public string? Evidence { get; set; }
    public decimal? Confidence { get; set; }
    public string RuleVersion { get; set; } = "PR-1";
    public DateTime ResolvedAt { get; set; }
}

/// <summary>Restricted raw-payload storage (requires transactions.raw permission).</summary>
public class RawTransactionPayload : TenantEntity
{
    public long IngestionRecordId { get; set; }
    public long? TransactionId { get; set; }
    public string Payload { get; set; } = "";
    public DateTime StoredAt { get; set; }
}

/// <summary>Pre-computed, incrementally maintained aggregates per customer and local business date.</summary>
public class CustomerDailyAggregate : TenantEntity
{
    public long CustomerId { get; set; }
    public DateOnly BusinessDate { get; set; }
    /// <summary>In or Out.</summary>
    public string Direction { get; set; } = "Out";
    public int TxnCount { get; set; }
    public decimal BaseAmount { get; set; }
    public DateTime UpdatedAt { get; set; }
}
