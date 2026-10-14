using AML.Domain.Common;

namespace AML.Domain.Entities;

public class Tenant : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseCurrency { get; set; } = "JOD";
    public string TimeZone { get; set; } = "Asia/Amman";
    /// <summary>Comma separated DayOfWeek names that are non-working (e.g. "Friday,Saturday").</summary>
    public string WeekendDays { get; set; } = "Friday,Saturday";
    public string ReportingEntityId { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class AppUser : TenantEntity, IConcurrencyAware
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    /// <summary>PBKDF2 hash (development-only local authentication). Null when the user authenticates through OIDC.</summary>
    public string? PasswordHash { get; set; }
    public string? ExternalSubject { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Team { get; set; }
    /// <summary>Optional data scope (e.g. agent region). When set, customer data is limited to this region.</summary>
    public string? DataScopeRegion { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    /// <summary>Incremented to revoke issued tokens.</summary>
    public int TokenVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public List<UserRole> Roles { get; set; } = new();
}

public class Role : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<RolePermission> Permissions { get; set; } = new();
}

public class Permission : Entity
{
    public string Code { get; set; } = "";
    public string Module { get; set; } = "";
    public string Description { get; set; } = "";
}

public class UserRole : Entity
{
    public long UserId { get; set; }
    public long RoleId { get; set; }
    public Role? Role { get; set; }
}

public class RolePermission : Entity
{
    public long RoleId { get; set; }
    public long PermissionId { get; set; }
    public Permission? Permission { get; set; }
}

public class UserPreference : Entity
{
    public long UserId { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Append-only audit trail. UPDATE/DELETE are denied by a database trigger and each row carries an HMAC for tamper detection.</summary>
public class AuditLog : Entity
{
    public long? TenantId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
    public string? SourceContext { get; set; }
    /// <summary>Restricted (tipping-off sensitive) events are hidden from users without restricted access.</summary>
    public bool IsRestricted { get; set; }
    public string Outcome { get; set; } = "Success";
    public string Hash { get; set; } = "";
}
