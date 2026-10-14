using AML.Domain.Common;

namespace AML.Domain.Entities;

public static class FieldDataTypes
{
    public const string ShortText = "ShortText";
    public const string LongText = "LongText";
    public const string Integer = "Integer";
    public const string Decimal = "Decimal";
    public const string Date = "Date";
    public const string DateTime = "DateTime";
    public const string Boolean = "Boolean";
    public const string SingleSelect = "SingleSelect";
    public const string MultiSelect = "MultiSelect";
    public const string Country = "Country";
    public const string Currency = "Currency";
    public const string Phone = "Phone";
    public const string Email = "Email";
    public const string Identifier = "Identifier";
    public const string DocumentReference = "DocumentReference";

    public static readonly string[] All =
    {
        ShortText, LongText, Integer, Decimal, Date, DateTime, Boolean, SingleSelect, MultiSelect,
        Country, Currency, Phone, Email, Identifier, DocumentReference
    };

    public static bool IsNumeric(string t) => t is Integer or Decimal;
    public static bool IsTemporal(string t) => t is Date or DateTime;
    public static bool IsTextual(string t) => !IsNumeric(t) && !IsTemporal(t) && t != Boolean && t != MultiSelect;
}

public class CustomerFieldGroup : TenantEntity
{
    public string Code { get; set; } = "";
    public string LabelEn { get; set; } = "";
    public string LabelAr { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One row per field version. (TenantId, Code, Version) is unique. Only one Published version is current at a time;
/// drafts are edited in place, publishing supersedes the previous version.
/// </summary>
public class CustomerFieldDefinition : TenantEntity, IConcurrencyAware
{
    public string Code { get; set; } = "";
    public int Version { get; set; } = 1;
    /// <summary>Draft, PendingApproval, Published, Superseded, Inactive.</summary>
    public string Status { get; set; } = ConfigStatus.Draft;
    public string LabelEn { get; set; } = "";
    public string LabelAr { get; set; } = "";
    public string? Description { get; set; }
    public string GroupCode { get; set; } = "";
    public string DataType { get; set; } = FieldDataTypes.ShortText;
    /// <summary>Comma separated customer types; empty = all.</summary>
    public string CustomerTypes { get; set; } = "";
    /// <summary>Customer types for which the field is mandatory (subset of CustomerTypes); empty = none.</summary>
    public string RequiredForTypes { get; set; } = "";
    public bool IsSearchable { get; set; }
    public bool IsFilterable { get; set; }
    public bool IsSensitive { get; set; }
    public bool IsEncrypted { get; set; }
    /// <summary>JSON validation: {min,max,regex,maxLength,minLength,precision,scale,minDate,maxDate,phoneRegion}.</summary>
    public string? ValidationJson { get; set; }
    public string? OptionsSource { get; set; }
    public string? DefaultValue { get; set; }
    public int SortOrder { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public bool IsSample { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public bool AppliesTo(string customerType) =>
        string.IsNullOrWhiteSpace(CustomerTypes) ||
        CustomerTypes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(customerType, StringComparer.OrdinalIgnoreCase);

    public bool IsRequiredFor(string customerType) =>
        RequiredForTypes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(customerType, StringComparer.OrdinalIgnoreCase);
}

public class CustomerFieldOption : TenantEntity
{
    public string FieldCode { get; set; } = "";
    public string OptionCode { get; set; } = "";
    public string LabelEn { get; set; } = "";
    public string LabelAr { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
}

/// <summary>
/// Typed EAV value storage. Search/filter uses typed columns indexed by (FieldCode, Value*).
/// Encrypted fields store only ciphertext plus an optional keyed HMAC search token.
/// </summary>
public class CustomerFieldValue : TenantEntity
{
    public long CustomerId { get; set; }
    public string FieldCode { get; set; } = "";
    public long FieldDefinitionId { get; set; }
    public int FieldVersion { get; set; }
    public string? ValueText { get; set; }
    public decimal? ValueNumber { get; set; }
    public DateTime? ValueDate { get; set; }
    public bool? ValueBool { get; set; }
    /// <summary>JSON array for multi-select values.</summary>
    public string? ValueJson { get; set; }
    public string? ValueEncrypted { get; set; }
    public string? ValueToken { get; set; }
    public string? MaskedDisplay { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string Source { get; set; } = "UI";
    public string VerificationStatus { get; set; } = "Unverified";
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

public class CustomerFormLayout : TenantEntity
{
    public string CustomerType { get; set; } = "";
    /// <summary>Role code or "*" for default.</summary>
    public string RoleCode { get; set; } = "*";
    public int Version { get; set; } = 1;
    public string Status { get; set; } = ConfigStatus.Published;
    /// <summary>JSON: [{ "group": "FINANCIAL", "fields": ["EXP_MONTHLY_CASH", ...] }]</summary>
    public string LayoutJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

public class CustomerFieldChangeHistory : TenantEntity
{
    public long CustomerId { get; set; }
    public string FieldCode { get; set; } = "";
    public int FieldVersion { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string Actor { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
}
