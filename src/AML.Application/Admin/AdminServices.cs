using System.Globalization;
using System.Security.Cryptography;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Admin;

public static class AuditCanonical
{
    public static string Of(AuditLog a) => string.Join("|", a.TenantId?.ToString(CultureInfo.InvariantCulture) ?? "", a.TimestampUtc.ToString("O", CultureInfo.InvariantCulture), a.Actor, a.Action,
        a.EntityType, a.EntityId ?? "", a.OldValues ?? "", a.NewValues ?? "", a.Reason ?? "", a.CorrelationId ?? "", a.IsRestricted ? "1" : "0", a.Outcome);
}

public sealed record UserDto(long Id, string Username, string DisplayName, string? Email, bool IsActive, string? Team, string? DataScopeRegion, IReadOnlyList<string> Roles, DateTime CreatedAt, DateTime? LastLoginAt);

public sealed class UserInput
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? Team { get; set; }
    public string? DataScopeRegion { get; set; }
    public List<string> Roles { get; set; } = new();
    public bool IsActive { get; set; } = true;
}

public sealed class AdminService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly ICryptoService _crypto;

    public AdminService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, ICryptoService crypto)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _crypto = crypto;
    }

    public async Task<List<UserDto>> UsersAsync(CancellationToken ct)
    {
        var users = await _db.Set<AppUser>().AsNoTracking().Include(u => u.Roles).ThenInclude(r => r.Role).OrderBy(u => u.Username).ToListAsync(ct);
        return users.Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.Email, u.IsActive, u.Team, u.DataScopeRegion, u.Roles.Select(r => r.Role!.Code).ToList(), u.CreatedAt, u.LastLoginAt)).ToList();
    }

    public async Task<UserDto> CreateUserAsync(UserInput i, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminUsers);
        if (string.IsNullOrWhiteSpace(i.Username)) throw new ValidationException("username", "Username is required.");
        if (await _db.Set<AppUser>().AnyAsync(u => u.Username == i.Username, ct)) throw new ConflictException("Username already exists.");
        if (i.Password is not null && i.Password.Length < 12) throw new ValidationException("password", "Password must be at least 12 characters.");
        var u = new AppUser
        {
            TenantId = _user.TenantId, Username = i.Username, DisplayName = i.DisplayName, Email = i.Email, PasswordHash = i.Password is null ? null : _crypto.HashPassword(i.Password),
            Team = i.Team, DataScopeRegion = i.DataScopeRegion, IsActive = i.IsActive, CreatedAt = _clock.UtcNow
        };
        _db.Set<AppUser>().Add(u);
        await _db.SaveChangesAsync(ct);
        await SetRolesAsync(u, i.Roles, ct);
        _audit.Record("USER_CREATE", "AppUser", u.Username, null, new { i.DisplayName, i.Team, i.DataScopeRegion, i.Roles });
        await _db.SaveChangesAsync(ct);
        return (await UsersAsync(ct)).First(x => x.Id == u.Id);
    }

    public async Task<UserDto> UpdateUserAsync(long id, UserInput i, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminUsers);
        var u = await _db.Set<AppUser>().Include(x => x.Roles).ThenInclude(r => r.Role).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("User", id);
        var old = new { u.DisplayName, u.Team, u.DataScopeRegion, u.IsActive, Roles = u.Roles.Select(r => r.Role!.Code).ToList() };
        u.DisplayName = i.DisplayName; u.Email = i.Email; u.Team = i.Team; u.DataScopeRegion = i.DataScopeRegion;
        if (u.IsActive && !i.IsActive) u.TokenVersion++;
        u.IsActive = i.IsActive;
        if (i.Password is not null)
        {
            if (i.Password.Length < 12) throw new ValidationException("password", "Password must be at least 12 characters.");
            u.PasswordHash = _crypto.HashPassword(i.Password);
            u.TokenVersion++;
        }
        await SetRolesAsync(u, i.Roles, ct);
        _audit.Record("USER_UPDATE", "AppUser", u.Username, old, new { i.DisplayName, i.Team, i.DataScopeRegion, i.IsActive, i.Roles, passwordChanged = i.Password is not null });
        await _db.SaveChangesAsync(ct);
        return (await UsersAsync(ct)).First(x => x.Id == u.Id);
    }

    private async Task SetRolesAsync(AppUser u, List<string> roles, CancellationToken ct)
    {
        var all = await _db.Set<Role>().ToListAsync(ct);
        var unknown = roles.Except(all.Select(r => r.Code)).ToList();
        if (unknown.Count > 0) throw new ValidationException("roles", $"Unknown role(s): {string.Join(", ", unknown)}.");
        var existing = await _db.Set<UserRole>().Where(x => x.UserId == u.Id).ToListAsync(ct);
        _db.Set<UserRole>().RemoveRange(existing.Where(e => !roles.Contains(all.First(r => r.Id == e.RoleId).Code)));
        foreach (var r in roles.Where(r => !existing.Any(e => all.First(a => a.Id == e.RoleId).Code == r)))
            _db.Set<UserRole>().Add(new UserRole { UserId = u.Id, RoleId = all.First(a => a.Code == r).Id });
        if (existing.Count != roles.Count || existing.Any(e => !roles.Contains(all.First(r => r.Id == e.RoleId).Code))) u.TokenVersion++;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeSessionsAsync(long userId, CancellationToken ct)
    {
        var u = await _db.Set<AppUser>().FirstOrDefaultAsync(x => x.Id == userId, ct) ?? throw new NotFoundException("User", userId);
        if (u.Id != _user.UserId) Guard.Require(_user, Perm.AdminUsers);
        u.TokenVersion++;
        _audit.Record("SESSIONS_REVOKED", "AppUser", u.Username, null, null);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<object> RolesAsync(CancellationToken ct)
    {
        var roles = await _db.Set<Role>().AsNoTracking().Include(r => r.Permissions).ThenInclude(p => p.Permission).OrderBy(r => r.Code).ToListAsync(ct);
        var perms = await _db.Set<Permission>().AsNoTracking().OrderBy(p => p.Code).ToListAsync(ct);
        return new { roles = roles.Select(r => new { r.Id, r.Code, r.Name, r.Description, permissions = r.Permissions.Select(p => p.Permission!.Code).OrderBy(x => x) }), permissions = perms.Select(p => new { p.Code, p.Module, p.Description }) };
    }

    public async Task SetRolePermissionsAsync(string roleCode, List<string> permissions, string? reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminUsers);
        var role = await _db.Set<Role>().Include(r => r.Permissions).ThenInclude(p => p.Permission).FirstOrDefaultAsync(r => r.Code == roleCode, ct) ?? throw new NotFoundException("Role", roleCode);
        var all = await _db.Set<Permission>().ToListAsync(ct);
        var unknown = permissions.Except(all.Select(p => p.Code)).ToList();
        if (unknown.Count > 0) throw new ValidationException("permissions", $"Unknown permission(s): {string.Join(", ", unknown)}.");
        var old = role.Permissions.Select(p => p.Permission!.Code).OrderBy(x => x).ToList();
        _db.Set<RolePermission>().RemoveRange(role.Permissions);
        foreach (var p in permissions.Distinct()) _db.Set<RolePermission>().Add(new RolePermission { RoleId = role.Id, PermissionId = all.First(a => a.Code == p).Id });
        _audit.Record("ROLE_PERMISSIONS_UPDATE", "Role", roleCode, old, permissions.OrderBy(x => x), reason);
        await _db.SaveChangesAsync(ct);
    }

    // lookups
    public async Task<List<LookupValue>> LookupsAsync(string? category, CancellationToken ct) =>
        await _db.Set<LookupValue>().AsNoTracking().Where(l => category == null || l.Category == category).OrderBy(l => l.Category).ThenBy(l => l.SortOrder).ThenBy(l => l.Code).ToListAsync(ct);

    public async Task<LookupValue> UpsertLookupAsync(LookupValue input, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminLookups);
        if (string.IsNullOrWhiteSpace(input.Category) || string.IsNullOrWhiteSpace(input.Code) || string.IsNullOrWhiteSpace(input.LabelEn)) throw new ValidationException("code", "Category, code and English label are required.");
        var l = await _db.Set<LookupValue>().FirstOrDefaultAsync(x => x.Category == input.Category && x.Code == input.Code, ct);
        var old = l is null ? null : new { l.LabelEn, l.LabelAr, l.IsActive, l.Attribute };
        if (l is null) _db.Set<LookupValue>().Add(l = new LookupValue { Category = input.Category, Code = input.Code });
        l.LabelEn = input.LabelEn; l.LabelAr = string.IsNullOrWhiteSpace(input.LabelAr) ? input.LabelEn : input.LabelAr; l.SortOrder = input.SortOrder; l.IsActive = input.IsActive; l.Attribute = input.Attribute;
        _audit.Record("LOOKUP_UPSERT", "LookupValue", $"{input.Category}:{input.Code}", old, new { l.LabelEn, l.LabelAr, l.IsActive, l.Attribute });
        await _db.SaveChangesAsync(ct);
        return l;
    }

    // settings
    public async Task<List<SystemSetting>> SettingsAsync(CancellationToken ct) => await _db.Set<SystemSetting>().AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct);

    public async Task<SystemSetting> SetSettingAsync(string key, string value, string? reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminSettings);
        var s = await _db.Set<SystemSetting>().FirstOrDefaultAsync(x => x.Key == key, ct);
        var old = s?.Value;
        if (s is null) _db.Set<SystemSetting>().Add(s = new SystemSetting { TenantId = _user.TenantId, Key = key });
        s.Value = value; s.UpdatedAt = _clock.UtcNow; s.UpdatedBy = _user.Username;
        _audit.Record("SETTING_UPDATE", "SystemSetting", key, new { value = old }, new { value }, reason);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    // FX
    public async Task<List<FxRate>> FxRatesAsync(bool includeHistory, CancellationToken ct) =>
        await _db.Set<FxRate>().AsNoTracking().Where(r => includeHistory || r.IsCurrent).OrderBy(r => r.FromCurrency).ThenByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Version).ToListAsync(ct);

    public async Task<FxRate> AddFxRateAsync(string from, string to, decimal rate, DateTime effectiveFrom, string source, string rateType, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminFx);
        if (rate <= 0) throw new ValidationException("rate", "Rate must be positive.");
        from = from.ToUpperInvariant(); to = to.ToUpperInvariant();
        var cur = await _db.Set<LookupValue>().Where(l => l.Category == "CURRENCY" && (l.Code == from || l.Code == to)).Select(l => l.Code).ToListAsync(ct);
        if (!cur.Contains(from) || !cur.Contains(to)) throw new ValidationException("currency", "Unknown currency code.");
        var r = new FxRate { TenantId = _user.TenantId, FromCurrency = from, ToCurrency = to, Rate = rate, EffectiveFrom = effectiveFrom, Source = source, RateType = rateType, CreatedBy = _user.Username, CreatedAt = _clock.UtcNow };
        _db.Set<FxRate>().Add(r);
        _audit.Record("FX_RATE_ADD", "FxRate", $"{from}/{to}", null, new { rate, effectiveFrom, source });
        await _db.SaveChangesAsync(ct);
        return r;
    }

    /// <summary>Editing a rate creates a new version; the old version is retained (not current) for historical evidence.</summary>
    public async Task<FxRate> EditFxRateAsync(long id, decimal rate, string? reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminFx);
        if (rate <= 0) throw new ValidationException("rate", "Rate must be positive.");
        var old = await _db.Set<FxRate>().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("FX rate", id);
        if (!old.IsCurrent) throw new ConflictException("Only the current version can be edited.");
        old.IsCurrent = false;
        var n = new FxRate
        {
            TenantId = old.TenantId, FromCurrency = old.FromCurrency, ToCurrency = old.ToCurrency, Rate = rate, RateType = old.RateType, Source = old.Source, EffectiveFrom = old.EffectiveFrom,
            Version = old.Version + 1, SupersedesId = old.Id, CreatedBy = _user.Username, CreatedAt = _clock.UtcNow
        };
        _db.Set<FxRate>().Add(n);
        _audit.Record("FX_RATE_EDIT", "FxRate", $"{old.FromCurrency}/{old.ToCurrency}", new { old.Rate, old.Version }, new { rate, n.Version }, reason);
        await _db.SaveChangesAsync(ct);
        return n;
    }

    public async Task<List<Holiday>> HolidaysAsync(CancellationToken ct) => await _db.Set<Holiday>().AsNoTracking().OrderBy(h => h.Date).ToListAsync(ct);

    public async Task<Holiday> AddHolidayAsync(DateOnly date, string name, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminSettings);
        var h = new Holiday { TenantId = _user.TenantId, Date = date, Name = name };
        _db.Set<Holiday>().Add(h);
        _audit.Record("HOLIDAY_ADD", "Holiday", date.ToString("yyyy-MM-dd"), null, new { name });
        await _db.SaveChangesAsync(ct);
        return h;
    }

    public async Task<object> SourceSystemsAsync(CancellationToken ct) =>
        await _db.Set<SourceSystem>().AsNoTracking().Select(s => new { s.Id, s.Code, s.Name, s.IsActive, s.ClientId, s.ExpectedFeed, s.TransactionTypesInScope }).ToListAsync(ct);

    /// <summary>Creates/rotates integration client credentials; the secret is returned once and stored hashed.</summary>
    public async Task<object> RotateClientSecretAsync(string code, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminSettings);
        var s = await _db.Set<SourceSystem>().FirstOrDefaultAsync(x => x.Code == code, ct) ?? throw new NotFoundException("Source system", code);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        s.ClientId ??= $"src-{code.ToLowerInvariant()}";
        s.ClientSecretHash = _crypto.HashPassword(secret);
        _audit.Record("CLIENT_SECRET_ROTATE", "SourceSystem", code, null, new { s.ClientId });
        await _db.SaveChangesAsync(ct);
        return new { s.ClientId, clientSecret = secret, note = "Store this secret in the source system's secret manager; it is not retrievable again." };
    }

    public async Task<object> UserPreferenceAsync(string key, CancellationToken ct) =>
        new { key, value = await _db.Set<UserPreference>().AsNoTracking().Where(p => p.UserId == _user.UserId && p.Key == key).Select(p => p.Value).FirstOrDefaultAsync(ct) };

    public async Task SetUserPreferenceAsync(string key, string value, CancellationToken ct)
    {
        if (_user.UserId is not { } uid) throw new ForbiddenException("Preferences require a user account.");
        if (key.Length > 100 || value.Length > 4000) throw new ValidationException("value", "Preference too large.");
        var p = await _db.Set<UserPreference>().FirstOrDefaultAsync(x => x.UserId == uid && x.Key == key, ct);
        if (p is null) _db.Set<UserPreference>().Add(p = new UserPreference { UserId = uid, Key = key });
        p.Value = value;
        p.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

public sealed class AuditQueryService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ICryptoService _crypto;
    private readonly IAuditService _audit;

    public AuditQueryService(IAmlDbContext db, ICurrentUser user, ICryptoService crypto, IAuditService audit)
    {
        _db = db; _user = user; _crypto = crypto; _audit = audit;
    }

    public async Task<PagedResult<AuditLog>> QueryAsync(string? entityType, string? entityId, string? actor, string? action, DateTime? from, DateTime? to, PageRequest page, CancellationToken ct)
    {
        var q = _db.Set<AuditLog>().AsNoTracking().Where(a => a.TenantId == _user.TenantId || a.TenantId == null);
        if (!_user.Has(Perm.CasesRestricted)) q = q.Where(a => !a.IsRestricted);
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(entityId)) q = q.Where(a => a.EntityId == entityId);
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(a => a.Actor == actor);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (from is { } f) q = q.Where(a => a.TimestampUtc >= f);
        if (to is { } t) q = q.Where(a => a.TimestampUtc < t);
        return await q.OrderByDescending(a => a.Id).ToPagedAsync(page, ct);
    }

    /// <summary>Recomputes row HMACs and checks for identity gaps (deleted rows).</summary>
    public async Task<object> VerifyIntegrityAsync(CancellationToken ct)
    {
        var rows = await _db.Set<AuditLog>().AsNoTracking().OrderBy(a => a.Id).Select(a => a).ToListAsync(ct);
        var tampered = rows.Where(r => r.Hash != _crypto.IntegrityHash(AuditCanonical.Of(r))).Select(r => r.Id).ToList();
        var gaps = new List<long>();
        for (var i = 1; i < rows.Count; i++)
            if (rows[i].Id != rows[i - 1].Id + 1) gaps.Add(rows[i - 1].Id);
        await _audit.RecordNowAsync("AUDIT_INTEGRITY_CHECK", "AuditLog", null, null, new { rows = rows.Count, tampered = tampered.Count, gaps = gaps.Count });
        return new { checkedRows = rows.Count, tamperedRowIds = tampered, gapsAfterIds = gaps, intact = tampered.Count == 0 && gaps.Count == 0, note = "Gaps can also come from rolled-back inserts (identity cache); investigate before concluding deletion." };
    }
}
