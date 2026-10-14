using AML.Domain.Common;

namespace AML.Domain.Entities;

public class Customer : TenantEntity, IConcurrencyAware
{
    public string SourceSystem { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string CustomerType { get; set; } = CustomerTypes.Individual;
    /// <summary>Prospect, Active, Restricted, Suspended, Dormant, Exited, Rejected.</summary>
    public string Status { get; set; } = "Prospect";
    public string? NameLatin { get; set; }
    public string? NameArabic { get; set; }
    /// <summary>Normalized name keys (Arabic + Latin) used for search; never displayed.</summary>
    public string? NameSearchKey { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? NationalityCountry { get; set; }
    public string? ResidenceCountry { get; set; }
    public string? IncorporationCountry { get; set; }
    public DateOnly? RegistrationDate { get; set; }
    public string KycStatus { get; set; } = "Incomplete";
    /// <summary>Clear, Pending, Unavailable, PotentialMatch, ConfirmedMatch, NotScreened.</summary>
    public string ScreeningStatus { get; set; } = "NotScreened";
    public string? AssignedOfficer { get; set; }
    public string? Region { get; set; }
    public long? OnboardingAgentId { get; set; }
    public string? DueDiligenceLevel { get; set; }
    public bool IsPep { get; set; }
    public bool IsFrozen { get; set; }
    public DateTime? ExitedAt { get; set; }
    public bool IsAnonymised { get; set; }
    public bool IsTestData { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class CustomerIdentification : TenantEntity
{
    public long CustomerId { get; set; }
    public string IdType { get; set; } = "NationalId";
    public string IdNumberEncrypted { get; set; } = "";
    /// <summary>Keyed HMAC search token. Never returned by the API.</summary>
    public string IdNumberToken { get; set; } = "";
    public string Last4 { get; set; } = "";
    public string? IssuingCountry { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string VerificationStatus { get; set; } = "Unverified";
    public DateTime CreatedAt { get; set; }
}

public class CustomerContact : TenantEntity
{
    public long CustomerId { get; set; }
    /// <summary>Phone, Email, Address.</summary>
    public string ContactType { get; set; } = "Phone";
    public string ValueEncrypted { get; set; } = "";
    public string? SearchToken { get; set; }
    public string Masked { get; set; } = "";
    public bool IsPrimary { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Effective-dated financial profile so earlier declarations can be reconstructed.</summary>
public class CustomerFinancialProfile : TenantEntity
{
    public long CustomerId { get; set; }
    public decimal? DeclaredMonthlyIncome { get; set; }
    public string? Currency { get; set; }
    public string? SourceOfFunds { get; set; }
    public string? SourceOfWealth { get; set; }
    public string? Occupation { get; set; }
    public decimal? ExpectedMonthlyVolume { get; set; }
    public int? ExpectedMonthlyCount { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public DateTime DeclaredAt { get; set; }
    public string? CreatedBy { get; set; }
}

public class CustomerDocument : TenantEntity
{
    public long CustomerId { get; set; }
    public string DocumentType { get; set; } = "";
    public string? Reference { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string Status { get; set; } = "Valid";
    public string? StoragePath { get; set; }
    public string? Sha256 { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Ownership and control structure: beneficial owners, controllers, signatories and directors.</summary>
public class OwnershipLink : TenantEntity
{
    public long EntityCustomerId { get; set; }
    public long? OwnerCustomerId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerNameArabic { get; set; }
    /// <summary>Person or Entity.</summary>
    public string OwnerType { get; set; } = "Person";
    public decimal? Percentage { get; set; }
    /// <summary>Ownership, VotingRights, ControlOtherMeans, SeniorManagingOfficial, Signatory, Director, ActingOnBehalf.</summary>
    public string ControlType { get; set; } = "Ownership";
    public DateOnly? OwnerDateOfBirth { get; set; }
    public string? OwnerNationality { get; set; }
    public bool OwnerIsPep { get; set; }
    public string? ScreeningStatus { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? Evidence { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CustomerDeviceSignal : TenantEntity
{
    public long CustomerId { get; set; }
    public string DeviceId { get; set; } = "";
    public string? IpAddress { get; set; }
    public string? GeoCountry { get; set; }
    /// <summary>Registration, Login, SimChange, PhoneChange.</summary>
    public string EventType { get; set; } = "Login";
    public DateTime OccurredAt { get; set; }
}

/// <summary>Entity-resolution link between customers. A link is a signal, never an automatic merge.</summary>
public class EntityLink : TenantEntity
{
    public long CustomerAId { get; set; }
    public long CustomerBId { get; set; }
    /// <summary>SameIdentity, SamePhone, SameEmail, SharedDevice, SharedIp, SimilarName, SharedBeneficiary.</summary>
    public string LinkType { get; set; } = "";
    public string Evidence { get; set; } = "";
    public decimal Confidence { get; set; }
    public string RuleVersion { get; set; } = "ER-1";
    /// <summary>Linked (high-confidence signal) or Candidate (needs review, never merged).</summary>
    public string Status { get; set; } = "Linked";
    public DateTime CreatedAt { get; set; }
}

public class CustomerAcceptanceDecision : TenantEntity
{
    public long CustomerId { get; set; }
    /// <summary>Accepted, AcceptedWithConditions, PendingInformation, PendingSeniorApproval, Rejected.</summary>
    public string Decision { get; set; } = "";
    public string PolicyCode { get; set; } = "";
    public int PolicyVersion { get; set; }
    public string DecidedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? Reason { get; set; }
    public string? ReasonCodes { get; set; }
    public string? Conditions { get; set; }
    public DateOnly? ConditionsDueDate { get; set; }
    public bool IsAutomatic { get; set; }
    public bool SanctionsWorkflow { get; set; }
    public bool IsCurrent { get; set; } = true;
    public DateTime DecidedAt { get; set; }
}

public class AcceptancePolicy : TenantEntity
{
    public string Code { get; set; } = "ACCEPT";
    public int Version { get; set; } = 1;
    public string Status { get; set; } = ConfigStatus.Active;
    public string SeniorApprovalRiskLevels { get; set; } = "High,Critical";
    public string ConditionalRiskLevels { get; set; } = "";
    public bool RequireCompleteKyc { get; set; } = true;
    public bool PepRequiresSeniorApproval { get; set; } = true;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public bool IsSample { get; set; } = true;
}

public class DueDiligenceLevelDefinition : TenantEntity
{
    public string Code { get; set; } = "CDD";
    public string Name { get; set; } = "";
    public string RequiredFieldCodes { get; set; } = "";
    public string RequiredDocuments { get; set; } = "";
    public string VerificationMethods { get; set; } = "";
    public string? ApprovalPermission { get; set; }
    public bool RequiresSourceOfFunds { get; set; }
    public bool RequiresSourceOfWealth { get; set; }
    public int ReviewFrequencyMonths { get; set; } = 24;
    public bool IsSample { get; set; } = true;
}

/// <summary>Ordered rules choosing a due-diligence level (first match wins by priority).</summary>
public class DueDiligenceSelectionRule : TenantEntity
{
    public int Priority { get; set; }
    /// <summary>RiskLevelIn, Pep, HighRiskJurisdiction, ComplexOwnership, FieldEquals, Default.</summary>
    public string ConditionType { get; set; } = "Default";
    public string? ConditionValue { get; set; }
    public string LevelCode { get; set; } = "CDD";
    public bool IsActive { get; set; } = true;
}

public class EddTask : TenantEntity
{
    public long CustomerId { get; set; }
    public string LevelCode { get; set; } = "EDD";
    public string TaskType { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string? EvidenceReference { get; set; }
    public string? SignedOffBy { get; set; }
    public DateTime? SignedOffAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CustomerExitRequest : TenantEntity
{
    public long CustomerId { get; set; }
    /// <summary>Neutral reason code shown to non-compliance users (e.g. BUSINESS_DECISION).</summary>
    public string ReasonCode { get; set; } = "";
    /// <summary>Internal compliance reason; restricted.</summary>
    public string? InternalReason { get; set; }
    public long? LinkedCaseId { get; set; }
    public string? Evidence { get; set; }
    public string Status { get; set; } = "PendingApproval";
    public string RequestedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public bool AddToDoNotOnboard { get; set; } = true;
}

public class DoNotOnboardEntry : TenantEntity
{
    public string? IdNumberToken { get; set; }
    public string? NameSearchKey { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public long? OriginalCustomerId { get; set; }
    public long? LinkedCaseId { get; set; }
    public string ReasonCode { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
