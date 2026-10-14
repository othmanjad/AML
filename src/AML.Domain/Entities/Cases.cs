using AML.Domain.Common;

namespace AML.Domain.Entities;

public static class CaseStatuses
{
    public const string Open = "Open";
    public const string UnderInvestigation = "UnderInvestigation";
    public const string PendingApproval = "PendingApproval";
    public const string Escalated = "Escalated";
    public const string Closed = "Closed";
}

public class AmlCase : TenantEntity, IConcurrencyAware
{
    public string CaseNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string CaseType { get; set; } = "Investigation";
    public string Priority { get; set; } = "Medium";
    public string Status { get; set; } = CaseStatuses.Open;
    public string Confidentiality { get; set; } = Common.Confidentiality.Standard;
    public long? PrimaryCustomerId { get; set; }
    public string? AssignedInvestigator { get; set; }
    public string? Reviewer { get; set; }
    public string? Team { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Summary { get; set; }
    public string? Findings { get; set; }
    /// <summary>NoFurtherAction, EnhancedMonitoring, ExitRelationship, FileReport.</summary>
    public string? RecommendedAction { get; set; }
    public string? FinalDecision { get; set; }
    public string? ClosureReason { get; set; }
    public DateTime? SuspicionFormedAt { get; set; }
    /// <summary>None, Pending, Approved, Rejected.</summary>
    public string ApprovalStatus { get; set; } = "None";
    public string? ApprovalRequestedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public bool IsTestData { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class AmlCaseAlert : TenantEntity { public long CaseId { get; set; } public long AlertId { get; set; } public DateTime LinkedAt { get; set; } public string? LinkedBy { get; set; } }
public class AmlCaseCustomer : TenantEntity { public long CaseId { get; set; } public long CustomerId { get; set; } public string Role { get; set; } = "Related"; public DateTime LinkedAt { get; set; } }
public class AmlCaseTransaction : TenantEntity { public long CaseId { get; set; } public long TransactionId { get; set; } public DateTime LinkedAt { get; set; } }
public class AmlCaseTeamMember : TenantEntity { public long CaseId { get; set; } public string Username { get; set; } = ""; }

/// <summary>Immutable note. Corrections create a new note that supersedes the old one.</summary>
public class AmlCaseNote : TenantEntity
{
    public long CaseId { get; set; }
    public string Author { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Text { get; set; } = "";
    public long? SupersedesNoteId { get; set; }
}

public class AmlCaseDocument : TenantEntity
{
    public long CaseId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ScanStatus { get; set; } = "Clean";
    public string UploadedBy { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

public class AmlCaseHistory : TenantEntity
{
    public long CaseId { get; set; }
    public string FromStatus { get; set; } = "";
    public string ToStatus { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTime At { get; set; }
    public string? Comment { get; set; }
}

public class AmlCaseTask : TenantEntity
{
    public long CaseId { get; set; }
    public string Title { get; set; } = "";
    public string? Assignee { get; set; }
    public DateTime? DueDate { get; set; }
    public string Status { get; set; } = "Open";
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Allowed case status transitions (configurable workflow).</summary>
public class CaseStatusTransition : TenantEntity
{
    public string FromStatus { get; set; } = "";
    public string ToStatus { get; set; } = "";
}
