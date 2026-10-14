using System.Text.Json;

namespace AML.Application.Customers;

public sealed class CustomerListItem
{
    public long Id { get; init; }
    public string SourceSystem { get; init; }
    public string ExternalId { get; init; }
    public string CustomerType { get; init; }
    public string Status { get; init; }
    public string? NameLatin { get; init; }
    public string? NameArabic { get; init; }
    public string? NationalityCountry { get; init; }
    public string? ResidenceCountry { get; init; }
    public string? RiskLevel { get; init; }
    public decimal? RiskScore { get; init; }
    public string KycStatus { get; init; }
    public string ScreeningStatus { get; init; }
    public DateOnly? NextReviewDate { get; init; }
    public string? AssignedOfficer { get; init; }
    public string? Region { get; init; }
    public string? DueDiligenceLevel { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class CustomerQuery
{
    public string? Search { get; set; }
    public string? CustomerType { get; set; }
    public string? Status { get; set; }
    public string? RiskLevel { get; set; }
    public string? KycStatus { get; set; }
    public string? ScreeningStatus { get; set; }
    public string? Country { get; set; }
    public bool? ReviewOverdue { get; set; }
    public string? SourceSystem { get; set; }
    public string? AssignedOfficer { get; set; }
    public string? Wallet { get; set; }
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }
    public DateTime? UpdatedFrom { get; set; }
    public DateTime? UpdatedTo { get; set; }
    /// <summary>Dynamic field filters "CODE:op:value" (op = eq, gt, gte, lt, lte, contains).</summary>
    public List<string>? FieldFilter { get; set; }
}

/// <summary>Search on low-entropy sensitive values; sent in the request body so values never appear in URLs or access logs.</summary>
public sealed class SensitiveSearchRequest
{
    public string? IdentityNumber { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? FieldCode { get; set; }
    public string? FieldValue { get; set; }
}

public sealed class IdentificationInput
{
    public string IdType { get; set; } = "NationalId";
    public string IdNumber { get; set; } = "";
    public string? IssuingCountry { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
}

public sealed class ContactInput
{
    public string ContactType { get; set; } = "Phone";
    public string Value { get; set; } = "";
    public bool IsPrimary { get; set; } = true;
}

public sealed class FinancialProfileInput
{
    public decimal? DeclaredMonthlyIncome { get; set; }
    public string? Currency { get; set; }
    public string? SourceOfFunds { get; set; }
    public string? SourceOfWealth { get; set; }
    public string? Occupation { get; set; }
    public decimal? ExpectedMonthlyVolume { get; set; }
    public int? ExpectedMonthlyCount { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? DeclaredAt { get; set; }
}

public sealed class CustomerCreateRequest
{
    public string SourceSystem { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string CustomerType { get; set; } = "";
    public string? NameLatin { get; set; }
    public string? NameArabic { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? NationalityCountry { get; set; }
    public string? ResidenceCountry { get; set; }
    public string? IncorporationCountry { get; set; }
    public DateOnly? RegistrationDate { get; set; }
    public string? AssignedOfficer { get; set; }
    public string? Region { get; set; }
    public long? OnboardingAgentId { get; set; }
    public bool IsPep { get; set; }
    public Dictionary<string, JsonElement>? Attributes { get; set; }
    public List<IdentificationInput>? Identifications { get; set; }
    public List<ContactInput>? Contacts { get; set; }
    public FinancialProfileInput? FinancialProfile { get; set; }
}

public sealed class CustomerUpdateRequest
{
    public string? NameLatin { get; set; }
    public string? NameArabic { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? NationalityCountry { get; set; }
    public string? ResidenceCountry { get; set; }
    public string? IncorporationCountry { get; set; }
    public string? AssignedOfficer { get; set; }
    public string? Region { get; set; }
    public bool? IsPep { get; set; }
    public string? Reason { get; set; }
    public string RowVersion { get; set; } = "";
}

public sealed record IdentificationDto(long Id, string IdType, string Masked, string? IssuingCountry, DateOnly? IssueDate, DateOnly? ExpiryDate, string VerificationStatus, bool IsExpired, string? Unmasked);

public sealed record ContactDto(long Id, string ContactType, string Masked, bool IsPrimary, string? Unmasked);

public sealed record RiskSummaryDto(decimal Score, string Level, decimal CalculatedScore, string CalculatedLevel, string MonitoringLevel, long? LastAssessmentId,
    DateTime? LastAssessedAt, DateOnly? NextReviewDate, bool OverrideApplied, string? OverrideType, string? OverrideReason, bool SanctionsWorkflow,
    string? MethodologyCode, int? MethodologyVersion, bool ReviewOverdue);

public sealed record CustomerDetailDto(long Id, string SourceSystem, string ExternalId, string CustomerType, string Status, string? NameLatin, string? NameArabic,
    DateOnly? DateOfBirth, string? NationalityCountry, string? ResidenceCountry, string? IncorporationCountry, DateOnly? RegistrationDate, string KycStatus,
    string ScreeningStatus, string? AssignedOfficer, string? Region, string? DueDiligenceLevel, bool IsPep, bool IsFrozen, DateTime? ExitedAt, bool IsTestData,
    DateTime CreatedAt, DateTime UpdatedAt, string RowVersion, IReadOnlyList<IdentificationDto> Identifications, IReadOnlyList<ContactDto> Contacts,
    RiskSummaryDto? Risk, string? AcceptanceDecision, int AlertCount, int CaseCount, IReadOnlyList<string> KycGaps, IReadOnlyList<string> Flags);

public sealed record AttributeValueDto(string Code, int FieldVersion, string LabelEn, string LabelAr, string GroupCode, string DataType, bool IsSensitive,
    object? Value, string Display, bool Masked, DateTime EffectiveFrom, DateTime? EffectiveTo, string Source, string VerificationStatus);

public sealed class AttributesUpdateRequest
{
    public Dictionary<string, JsonElement> Values { get; set; } = new();
    public DateTime? EffectiveFrom { get; set; }
    public string? Reason { get; set; }
    public string Source { get; set; } = "UI";
}

public sealed record FinancialProfileDto(long Id, decimal? DeclaredMonthlyIncome, string? Currency, string? SourceOfFunds, string? SourceOfWealth, string? Occupation,
    decimal? ExpectedMonthlyVolume, int? ExpectedMonthlyCount, DateTime EffectiveFrom, DateTime? EffectiveTo, DateTime DeclaredAt);

public sealed class OwnershipInput
{
    public long? OwnerCustomerId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerNameArabic { get; set; }
    public string OwnerType { get; set; } = "Person";
    public decimal? Percentage { get; set; }
    public string ControlType { get; set; } = "Ownership";
    public DateOnly? OwnerDateOfBirth { get; set; }
    public string? OwnerNationality { get; set; }
    public bool OwnerIsPep { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public string? Evidence { get; set; }
}

public sealed record OwnershipLinkDto(long Id, long EntityCustomerId, long? OwnerCustomerId, string? OwnerName, string OwnerType, decimal? Percentage,
    string ControlType, bool OwnerIsPep, string? ScreeningStatus, DateTime EffectiveFrom, DateTime? EffectiveTo, string? Evidence);

public sealed record BeneficialOwnerDto(string OwnerKey, string OwnerName, decimal EffectivePercentage, IReadOnlyList<string> ControlTypes, IReadOnlyList<string> Paths,
    bool IsBeneficialOwner, string Basis, bool IsPep);

public sealed record OwnershipAnalysisDto(decimal ThresholdPercent, string ThresholdSource, IReadOnlyList<OwnershipLinkDto> Links, IReadOnlyList<BeneficialOwnerDto> Owners,
    IReadOnlyList<string> Cycles, IReadOnlyList<string> Unresolved, bool MissingOwnershipInformation);
