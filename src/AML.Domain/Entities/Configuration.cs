using AML.Domain.Common;

namespace AML.Domain.Entities;

public class LookupValue : Entity
{
    public string Category { get; set; } = "";
    public string Code { get; set; } = "";
    public string LabelEn { get; set; } = "";
    public string LabelAr { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Optional attribute (e.g. FATF high-risk flag for countries, currency minor units).</summary>
    public string? Attribute { get; set; }
}

public class SourceSystem : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>Integration client identifier for machine-to-machine ingestion.</summary>
    public string? ClientId { get; set; }
    public string? ClientSecretHash { get; set; }
    /// <summary>Expected feed frequency used by data-feed completeness checks (Daily, None).</summary>
    public string ExpectedFeed { get; set; } = "Daily";
    public string? TransactionTypesInScope { get; set; }
}

/// <summary>Generic system/tenant settings (non-jurisdictional technical configuration).</summary>
public class SystemSetting : TenantEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Description { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Jurisdiction-specific values (deadlines, thresholds, retention). Must carry a legal source reference.</summary>
public class JurisdictionSetting : TenantEntity, IConcurrencyAware
{
    public string Jurisdiction { get; set; } = "JO";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Unit { get; set; }
    public string? LegalSource { get; set; }
    public string? ArticleReference { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    /// <summary>ToBeConfirmed (seed), PendingApproval, Approved, Superseded.</summary>
    public string Status { get; set; } = "ToBeConfirmed";
    public string? ComplianceOwner { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>Effective-dated, versioned FX rates. Edits create a new version; old versions are retained.</summary>
public class FxRate : TenantEntity
{
    public string FromCurrency { get; set; } = "";
    public string ToCurrency { get; set; } = "";
    public decimal Rate { get; set; }
    public string RateType { get; set; } = "Mid";
    public string Source { get; set; } = "";
    public DateTime EffectiveFrom { get; set; }
    public int Version { get; set; } = 1;
    public bool IsCurrent { get; set; } = true;
    public long? SupersedesId { get; set; }
    public bool IsTestData { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Holiday : TenantEntity
{
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>Maps source-system statuses to normalized statuses.</summary>
public class TransactionStatusMapping : TenantEntity
{
    public string SourceSystem { get; set; } = "*";
    public string SourceStatus { get; set; } = "";
    public string NormalizedStatus { get; set; } = "";
}
