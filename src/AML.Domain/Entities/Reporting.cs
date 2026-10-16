using AML.Domain.Common;

namespace AML.Domain.Entities;

public static class ReportStatuses
{
    public const string Draft = "Draft";
    public const string UnderReview = "UnderReview";
    public const string Approved = "Approved";
    public const string Submitted = "Submitted";
    public const string Acknowledged = "Acknowledged";
    public const string RejectedByFiu = "RejectedByFiu";
    public const string Amended = "Amended";
    public const string Withdrawn = "Withdrawn";
    public static bool IsFiled(string s) => s is Submitted or Acknowledged or RejectedByFiu or Amended;
}

public class RegulatoryReport : TenantEntity, IConcurrencyAware
{
    public string ReportNumber { get; set; } = "";
    /// <summary>STR, SAR, ContinuingActivity, Amendment.</summary>
    public string ReportType { get; set; } = "STR";
    public long CaseId { get; set; }
    public string Status { get; set; } = ReportStatuses.Draft;
    public string Confidentiality { get; set; } = Common.Confidentiality.StrictlyConfidential;
    public string? Narrative { get; set; }
    public string? IndicatorCodes { get; set; }
    public string SubjectsJson { get; set; } = "[]";
    public string TransactionsJson { get; set; } = "[]";
    public string? AccountsJson { get; set; }
    public string PreparedBy { get; set; } = "";
    public string? ReviewedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ValidationErrors { get; set; }
    public DateTime? ValidatedAt { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public string? SubmissionChannel { get; set; }
    public string? PayloadXml { get; set; }
    public string? PayloadHash { get; set; }
    public string? FiuReference { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgementDetails { get; set; }
    public string? ManualSubmissionEvidence { get; set; }
    public long? PreviousReportId { get; set; }
    public DateTime? SuspicionFormedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? ReminderSentAt { get; set; }
    public bool IsOverdue { get; set; }
    public DateTime? EscalatedAt { get; set; }
    public bool IsTestData { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class AuthorityRequest : TenantEntity, IConcurrencyAware
{
    public string Reference { get; set; } = "";
    public string Authority { get; set; } = "";
    public string RequestType { get; set; } = "InformationRequest";
    public long? CustomerId { get; set; }
    public string? SubjectsJson { get; set; }
    public string Scope { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime DueDate { get; set; }
    public string? AssignedOfficer { get; set; }
    /// <summary>Open, InProgress, Responded, Closed.</summary>
    public string Status { get; set; } = "Open";
    public string Confidentiality { get; set; } = Common.Confidentiality.StrictlyConfidential;
    public string? Response { get; set; }
    public string? ResponseEvidence { get; set; }
    public DateTime? RespondedAt { get; set; }
    public bool IsOverdue { get; set; }
    public long? TriggeredAssessmentId { get; set; }
    public long? CaseId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public static class CtrStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Exported = "Exported";
    /// <summary>Submitted manually to the FIU outside the system (reference recorded); never sent automatically.</summary>
    public const string Submitted = "Submitted";
    public const string Rejected = "Rejected";
}

/// <summary>
/// Currency (cash) transaction report: one per customer and business date whose eligible cash transactions total at least the
/// configured threshold (Reporting:CtrThreshold, base currency). Generated as a draft, approved, exported and submitted manually.
/// </summary>
public class CurrencyTransactionReport : TenantEntity, IConcurrencyAware
{
    public string ReportNumber { get; set; } = "";
    public long CustomerId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public int TransactionCount { get; set; }
    public decimal TotalBaseAmount { get; set; }
    public decimal CashInBaseAmount { get; set; }
    public decimal CashOutBaseAmount { get; set; }
    public string BaseCurrency { get; set; } = "";
    public decimal Threshold { get; set; }
    public string TransactionIdsJson { get; set; } = "[]";
    public string Status { get; set; } = CtrStatuses.Draft;
    public DateOnly DueDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ExportedAt { get; set; }
    public string? SubmissionReference { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public string? SubmittedBy { get; set; }
    public string? Notes { get; set; }
    public bool IsTestData { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
