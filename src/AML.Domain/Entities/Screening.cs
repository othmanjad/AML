using AML.Domain.Common;

namespace AML.Domain.Entities;

public static class ScreeningStatuses
{
    public const string PotentialMatch = "PotentialMatch";
    public const string UnderReview = "UnderReview";
    public const string ConfirmedMatch = "ConfirmedMatch";
    public const string FalsePositive = "FalsePositive";
    public const string UnableToDetermine = "UnableToDetermine";
    public const string Escalated = "Escalated";

    public static readonly Dictionary<string, string[]> Transitions = new()
    {
        [PotentialMatch] = new[] { UnderReview, Escalated, ConfirmedMatch, FalsePositive, UnableToDetermine },
        [UnderReview] = new[] { ConfirmedMatch, FalsePositive, UnableToDetermine, Escalated },
        [Escalated] = new[] { ConfirmedMatch, FalsePositive, UnableToDetermine, UnderReview },
        [UnableToDetermine] = new[] { UnderReview, Escalated, ConfirmedMatch, FalsePositive },
        [FalsePositive] = new[] { UnderReview },
        [ConfirmedMatch] = new[] { UnderReview },
    };
}

public class WatchlistSource : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Sanctions, PEP, AdverseMedia, Internal, ProliferationFinancing.</summary>
    public string ListType { get; set; } = "Sanctions";
    /// <summary>Mandatory lists (UN / national) trigger configured automatic actions on confirmed match.</summary>
    public bool IsMandatory { get; set; }
    public string? CurrentVersion { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsTestData { get; set; }
}

public class WatchlistVersion : TenantEntity
{
    public long SourceId { get; set; }
    public string SourceCode { get; set; } = "";
    public string VersionLabel { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public DateTime EffectiveAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int Additions { get; set; }
    public int Amendments { get; set; }
    public int Removals { get; set; }
    public DateTime? RescreenStartedAt { get; set; }
    public DateTime? RescreenCompletedAt { get; set; }
    public long? TimeToScreenMs { get; set; }
    public int SubjectsScreened { get; set; }
    public int MatchesRaised { get; set; }
    public string? ChangeSetJson { get; set; }
}

public class WatchlistEntry : TenantEntity
{
    public long SourceId { get; set; }
    public string SourceCode { get; set; } = "";
    public string EntryId { get; set; } = "";
    public string PrimaryName { get; set; } = "";
    public string? AliasesJson { get; set; }
    public string NameSearchKey { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string? Nationality { get; set; }
    /// <summary>Sanctions, Terrorism, ProliferationFinancing, PEP, AdverseMedia, Internal.</summary>
    public string Category { get; set; } = "Sanctions";
    public string Status { get; set; } = "Active";
    public string VersionAdded { get; set; } = "";
    public string? LastAmendedVersion { get; set; }
    public string EntryHash { get; set; } = "";
    public bool IsTestData { get; set; }
}

public class ScreeningRequest : TenantEntity
{
    public string IdempotencyKey { get; set; } = "";
    /// <summary>Customer, BeneficialOwner, Signatory, Counterparty, Transaction.</summary>
    public string SubjectType { get; set; } = "Customer";
    public string SubjectId { get; set; } = "";
    public long? CustomerId { get; set; }
    public string SubjectName { get; set; } = "";
    public string? SubjectHash { get; set; }
    public string ProviderCode { get; set; } = "";
    public string Trigger { get; set; } = "Onboarding";
    /// <summary>Pending, Completed, Unavailable, Failed.</summary>
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? FailureHistory { get; set; }
    public long? ListVersionId { get; set; }
    public int ResultCount { get; set; }
    public int SuppressedCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class ScreeningResult : TenantEntity, IConcurrencyAware
{
    public long RequestId { get; set; }
    public string SubjectType { get; set; } = "Customer";
    public string SubjectId { get; set; } = "";
    public long? CustomerId { get; set; }
    public string SubjectName { get; set; } = "";
    public string ProviderCode { get; set; } = "";
    public string SourceCode { get; set; } = "";
    public string ListEntryId { get; set; } = "";
    public string? ListVersion { get; set; }
    public string? EntryHash { get; set; }
    public string? SubjectHash { get; set; }
    public decimal MatchScore { get; set; }
    public string MatchMethod { get; set; } = "";
    public string? MatchedAttributesJson { get; set; }
    public string? Explanation { get; set; }
    public string Category { get; set; } = "Sanctions";
    public string Status { get; set; } = ScreeningStatuses.PotentialMatch;
    public DateTime ScreenedAt { get; set; }
    public string? Reviewer { get; set; }
    public string? Decision { get; set; }
    public string? Notes { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public bool NeedsReview { get; set; }
    public string? NeedsReviewReason { get; set; }
    /// <summary>Encrypted provider response; only readable with screening.raw permission.</summary>
    public string? ProviderResponseEncrypted { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class ScreeningReviewHistory : TenantEntity
{
    public long ResultId { get; set; }
    public string FromStatus { get; set; } = "";
    public string ToStatus { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTime At { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Governed allow-list (good-guy list) of cleared false positives.</summary>
public class AllowListEntry : TenantEntity
{
    public string SubjectType { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string SubjectHash { get; set; } = "";
    public string SourceCode { get; set; } = "";
    public string ListEntryId { get; set; } = "";
    public string EntryHash { get; set; } = "";
    public string? ListVersion { get; set; }
    public long? ClearedResultId { get; set; }
    public string ClearedBy { get; set; } = "";
    public DateTime ClearedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public string? InvalidatedReason { get; set; }
    public DateTime? InvalidatedAt { get; set; }
}

public class ScreeningSuppressionLog : TenantEntity
{
    public long AllowListEntryId { get; set; }
    public long RequestId { get; set; }
    public string SubjectId { get; set; } = "";
    public string ListEntryId { get; set; } = "";
    public DateTime At { get; set; }
}

/// <summary>Development/test provider behaviour controls (ignored in Production).</summary>
public class ScreeningProviderStatus : TenantEntity
{
    public string ProviderCode { get; set; } = "";
    public bool IsAvailable { get; set; } = true;
    public string SimulationMode { get; set; } = "Normal";
    public int SimulatedFailuresRemaining { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public string? LastError { get; set; }
}
