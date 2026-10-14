namespace AML.Domain.Common;

public abstract class Entity
{
    public long Id { get; set; }
}

/// <summary>Entities partitioned by tenant (legal entity). A global query filter enforces isolation.</summary>
public interface ITenantScoped
{
    long TenantId { get; set; }
}

public abstract class TenantEntity : Entity, ITenantScoped
{
    public long TenantId { get; set; }
}

/// <summary>Entities protected with optimistic concurrency (SQL Server rowversion).</summary>
public interface IConcurrencyAware
{
    byte[] RowVersion { get; set; }
}

public interface IAuditableEntity
{
    DateTime CreatedAt { get; set; }
    string? CreatedBy { get; set; }
    DateTime? UpdatedAt { get; set; }
    string? UpdatedBy { get; set; }
}

public static class RiskLevels
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";
    public const string Critical = "Critical";
}

public static class CustomerTypes
{
    public const string Individual = "Individual";
    public const string Corporate = "Corporate";
    public const string Merchant = "Merchant";
    public const string Agent = "Agent";
    public static readonly string[] All = { Individual, Corporate, Merchant, Agent };
}

public static class ConfigStatus
{
    public const string Draft = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Published = "Published";
    public const string Active = "Active";
    public const string Disabled = "Disabled";
    public const string Superseded = "Superseded";
    public const string Retired = "Retired";
    public const string Inactive = "Inactive";
    public const string Rejected = "Rejected";
}

public static class Confidentiality
{
    public const string Standard = "Standard";
    public const string Restricted = "Restricted";
    public const string StrictlyConfidential = "StrictlyConfidential";
    public static bool IsRestricted(string? c) => c == Restricted || c == StrictlyConfidential;
}
