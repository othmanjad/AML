using AML.Domain.Common;

namespace AML.Domain.Entities;

public class IngestionRun : TenantEntity
{
    public string SourceSystem { get; set; } = "";
    public string BatchId { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Processing, Completed, CompletedWithErrors, Failed.</summary>
    public string Status { get; set; } = "Processing";
    public int RecordCount { get; set; }
    public int ProcessedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ConflictCount { get; set; }
    public int ErrorCount { get; set; }
    public string CorrelationId { get; set; } = "";
    public string? SubmittedBy { get; set; }
}

public class IngestionRecord : TenantEntity
{
    public long RunId { get; set; }
    public string SourceSystem { get; set; } = "";
    public string? SourceTransactionId { get; set; }
    public string PayloadHash { get; set; } = "";
    /// <summary>Received, Processed, Duplicate, Conflict, Retrying, DeadLetter, Replayed.</summary>
    public string Status { get; set; } = "Received";
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? ErrorHistory { get; set; }
    public string CorrelationId { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public long? TransactionId { get; set; }
    public string? ReplayedBy { get; set; }
    public DateTime? ReplayedAt { get; set; }
}

public class DataQualityIssue : TenantEntity
{
    /// <summary>UnresolvedParty, PayloadConflict, MissingFxRate, CircularOwnership, MissingBeneficialOwner, UnlinkedReversal, FeedGap, ScreeningUnavailable, PaymentTransparency.</summary>
    public string IssueType { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string Description { get; set; } = "";
    public string Severity { get; set; } = "Medium";
    public string Status { get; set; } = "Open";
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}

public class SourceControlTotal : TenantEntity
{
    public string SourceSystem { get; set; } = "";
    public DateOnly BusinessDate { get; set; }
    public int ReportedCount { get; set; }
    public decimal ReportedAmount { get; set; }
    public string Currency { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
}

public class ReconciliationResult : TenantEntity
{
    public string SourceSystem { get; set; } = "";
    public DateOnly BusinessDate { get; set; }
    public string? TransactionType { get; set; }
    public int SourceCount { get; set; }
    public decimal SourceAmount { get; set; }
    public int ImportedCount { get; set; }
    public decimal ImportedAmount { get; set; }
    public int CountDifference { get; set; }
    public decimal AmountDifference { get; set; }
    /// <summary>Matched, Mismatch, Gap.</summary>
    public string Status { get; set; } = "Matched";
    public DateTime RunAt { get; set; }
}

public class OutboxMessage : Entity
{
    public Guid MessageId { get; set; }
    public long? TenantId { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Consumer-side record of published events (idempotent publication => exactly-once effect).</summary>
public class PublishedEvent : Entity
{
    public Guid MessageId { get; set; }
    public string EventType { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}

public class RetryQueueItem : TenantEntity
{
    /// <summary>Screening, ComplianceAction, Ingestion, Export.</summary>
    public string Kind { get; set; } = "";
    public string ReferenceId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class OperationalAlert : TenantEntity
{
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Reference { get; set; }
    public string Severity { get; set; } = "Warning";
    public string Status { get; set; } = "Open";
    public DateTime CreatedAt { get; set; }
}

public class ExportJob : TenantEntity
{
    public Guid JobId { get; set; }
    public string ReportCode { get; set; } = "";
    public string Format { get; set; } = "csv";
    public string? FiltersJson { get; set; }
    public bool IncludeSensitive { get; set; }
    public bool IncludeRestricted { get; set; }
    public string RequestedBy { get; set; } = "";
    public string RequesterPermissions { get; set; } = "";
    /// <summary>Queued, Running, Completed, Failed.</summary>
    public string Status { get; set; } = "Queued";
    public int RowCount { get; set; }
    public string? StorageKey { get; set; }
    public string? FileName { get; set; }
    public string? Sha256 { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int DownloadCount { get; set; }
}

public class JobHeartbeat : Entity
{
    public string JobName { get; set; } = "";
    public DateTime LastRunAt { get; set; }
    public string LastStatus { get; set; } = "";
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public long LastDurationMs { get; set; }
}
