using System.Globalization;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Common;

/// <summary>Tenant settings, calendar, technical settings and jurisdiction values.</summary>
public sealed class TenantSettingsService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public TenantSettingsService(IAmlDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db; _user = user; _clock = clock;
    }

    public async Task<Tenant> TenantAsync(long? tenantId = null, CancellationToken ct = default)
    {
        var id = tenantId ?? _user.TenantId;
        return await _db.Set<Tenant>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
               ?? throw new NotFoundException("Tenant", id);
    }

    public async Task<WorkingCalendar> CalendarAsync(long? tenantId = null, CancellationToken ct = default)
    {
        var t = await TenantAsync(tenantId, ct);
        var hol = await _db.Set<Holiday>().IgnoreQueryFilters().Where(h => h.TenantId == t.Id).Select(h => h.Date).ToListAsync(ct);
        return WorkingCalendar.Create(t.TimeZone, t.WeekendDays, hol);
    }

    public async Task<TimeZoneInfo> TimeZoneAsync(long? tenantId = null, CancellationToken ct = default) =>
        TimeZoneInfo.FindSystemTimeZoneById((await TenantAsync(tenantId, ct)).TimeZone);

    public async Task<string> SettingAsync(string key, string defaultValue, long? tenantId = null, CancellationToken ct = default)
    {
        var id = tenantId ?? _user.TenantId;
        var s = await _db.Set<SystemSetting>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == id && x.Key == key, ct);
        return s?.Value ?? defaultValue;
    }

    public async Task<int> IntSettingAsync(string key, int defaultValue, long? tenantId = null, CancellationToken ct = default) =>
        int.TryParse(await SettingAsync(key, defaultValue.ToString(CultureInfo.InvariantCulture), tenantId, ct), out var v) ? v : defaultValue;

    public async Task<decimal> DecimalSettingAsync(string key, decimal defaultValue, long? tenantId = null, CancellationToken ct = default) =>
        decimal.TryParse(await SettingAsync(key, defaultValue.ToString(CultureInfo.InvariantCulture), tenantId, ct), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;

    public async Task<bool> BoolSettingAsync(string key, bool defaultValue, long? tenantId = null, CancellationToken ct = default) =>
        bool.TryParse(await SettingAsync(key, defaultValue.ToString(), tenantId, ct), out var v) ? v : defaultValue;

    public async Task SetSettingAsync(string key, string value, long? tenantId = null, CancellationToken ct = default)
    {
        var id = tenantId ?? _user.TenantId;
        var s = await _db.Set<SystemSetting>().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TenantId == id && x.Key == key, ct);
        if (s is null) _db.Set<SystemSetting>().Add(s = new SystemSetting { TenantId = id, Key = key });
        s.Value = value;
        s.UpdatedAt = _clock.UtcNow;
        s.UpdatedBy = _user.Username;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Jurisdiction value effective at <paramref name="asOf"/> (approved values preferred, seeded 'to be confirmed' values as fallback).</summary>
    public async Task<JurisdictionSetting?> JurisdictionAsync(string key, DateTime? asOf = null, long? tenantId = null, CancellationToken ct = default)
    {
        var id = tenantId ?? _user.TenantId;
        var at = asOf ?? _clock.UtcNow;
        var candidates = await _db.Set<JurisdictionSetting>().IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.TenantId == id && j.Key == key && j.EffectiveFrom <= at && (j.EffectiveTo == null || j.EffectiveTo > at) && j.Status != "Superseded" && j.Status != "PendingApproval" && j.Status != "Rejected")
            .ToListAsync(ct);
        return candidates.OrderByDescending(j => j.Status == "Approved").ThenByDescending(j => j.EffectiveFrom).FirstOrDefault();
    }

    public async Task<decimal> JurisdictionDecimalAsync(string key, decimal fallback, DateTime? asOf = null, long? tenantId = null, CancellationToken ct = default)
    {
        var j = await JurisdictionAsync(key, asOf, tenantId, ct);
        return j is not null && decimal.TryParse(j.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}

/// <summary>Converts amounts to the base currency with effective-dated, versioned rates. Never defaults to 1.</summary>
public sealed class FxService
{
    private readonly IAmlDbContext _db;
    public FxService(IAmlDbContext db) => _db = db;

    public async Task<List<FxConverter.Rate>> RatesAsync(long tenantId, CancellationToken ct) =>
        await _db.Set<FxRate>().IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == tenantId && r.IsCurrent)
            .Select(r => new FxConverter.Rate(r.Id, r.FromCurrency, r.ToCurrency, r.Rate, r.EffectiveFrom, r.Version, r.Source))
            .ToListAsync(ct);

    public async Task<FxConverter.Conversion> ConvertAsync(long tenantId, decimal amount, string currency, string target, DateTime atUtc, CancellationToken ct) =>
        FxConverter.Convert(amount, currency, target, atUtc, await RatesAsync(tenantId, ct));
}

/// <summary>Central data-visibility rules: tenant (global filter), data scope, and confidentiality (tipping-off) controls.</summary>
public sealed class AccessPolicy
{
    private readonly ICurrentUser _user;
    private readonly IAmlDbContext _db;

    public AccessPolicy(ICurrentUser user, IAmlDbContext db)
    {
        _user = user; _db = db;
    }

    public bool CanSeeRestricted => _user.Has(Perm.CasesRestricted);

    public IQueryable<Customer> Customers()
    {
        var q = _db.Set<Customer>().AsQueryable();
        if (!string.IsNullOrEmpty(_user.DataScopeRegion))
        {
            var region = _user.DataScopeRegion;
            q = q.Where(c => c.Region == region);
        }
        return q;
    }

    public IQueryable<AmlCase> Cases()
    {
        var q = _db.Set<AmlCase>().AsQueryable();
        if (!_user.Has(Perm.CasesRead)) return q.Where(c => false);
        if (CanSeeRestricted) return q;
        var u = _user.Username;
        var members = _db.Set<AmlCaseTeamMember>();
        return q.Where(c => c.Confidentiality == Confidentiality.Standard || c.AssignedInvestigator == u || c.Reviewer == u ||
                            members.Any(m => m.CaseId == c.Id && m.Username == u));
    }

    public IQueryable<AmlAlert> Alerts()
    {
        var q = _db.Set<AmlAlert>().AsQueryable();
        if (!_user.Has(Perm.AlertsRead)) return q.Where(a => false);
        if (CanSeeRestricted) return q;
        var visibleCases = Cases().Select(c => c.Id);
        return q.Where(a => a.Confidentiality == Confidentiality.Standard || (a.CaseId != null && visibleCases.Contains(a.CaseId.Value)));
    }

    public IQueryable<RegulatoryReport> Reports()
    {
        var q = _db.Set<RegulatoryReport>().AsQueryable();
        if (!_user.Has(Perm.StrPrepare) && !_user.Has(Perm.StrApprove)) return q.Where(r => false);
        if (CanSeeRestricted) return q;
        var visibleCases = Cases().Select(c => c.Id);
        return q.Where(r => visibleCases.Contains(r.CaseId) && r.PreparedBy == _user.Username);
    }

    public IQueryable<AuthorityRequest> AuthorityRequests()
    {
        var q = _db.Set<AuthorityRequest>().AsQueryable();
        if (!_user.Has(Perm.AuthorityManage)) return q.Where(r => false);
        if (CanSeeRestricted) return q;
        var u = _user.Username;
        return q.Where(r => r.Confidentiality == Confidentiality.Standard || r.AssignedOfficer == u || r.CreatedBy == u);
    }

    public bool IsCaseTeamMember(AmlCase c, IEnumerable<string> members) =>
        string.Equals(c.AssignedInvestigator, _user.Username, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(c.Reviewer, _user.Username, StringComparison.OrdinalIgnoreCase) ||
        members.Contains(_user.Username, StringComparer.OrdinalIgnoreCase);

    public string MaskDevice(string? deviceId) =>
        deviceId is null ? "" : _user.Has(Perm.DevicesUnmask) ? deviceId : FieldValueValidator.Mask(deviceId);
}
