using AML.Application.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Transactions;

public sealed record WalletDto(long Id, string SourceSystem, string ExternalWalletId, long? CustomerId, string WalletType, string Status, string Currency,
    long? ParentWalletId, bool IsFrozen, bool IsInternal, string? MappingCategory, IReadOnlyList<WalletDto> Children);

public sealed class WalletInput
{
    public string SourceSystem { get; set; } = "";
    public string ExternalWalletId { get; set; } = "";
    public long? CustomerId { get; set; }
    public string WalletType { get; set; } = "Personal";
    public string Status { get; set; } = "Active";
    public string Currency { get; set; } = "JOD";
    public long? ParentWalletId { get; set; }
}

public sealed class WalletMappingInput
{
    public string SourceSystem { get; set; } = "";
    public string ExternalWalletId { get; set; } = "";
    public string MappingCategory { get; set; } = "Clearing";
    public string? Channel { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public string? RowVersion { get; set; }
    public string? Reason { get; set; }
}

public sealed class WalletService
{
    private static readonly string[] Categories = { "Clearing", "Settlement", "Fee", "Suspense", "Processing" };
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly AccessPolicy _access;

    public WalletService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, AccessPolicy access)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _access = access;
    }

    public async Task<List<WalletDto>> CustomerWalletsAsync(long customerId, CancellationToken ct)
    {
        if (!await _access.Customers().AnyAsync(c => c.Id == customerId, ct)) throw new NotFoundException("Customer", customerId);
        var own = await _db.Set<Wallet>().AsNoTracking().Where(w => w.CustomerId == customerId).ToListAsync(ct);
        var ids = own.Select(w => w.Id).ToList();
        var children = await _db.Set<Wallet>().AsNoTracking().Where(w => w.ParentWalletId != null && ids.Contains(w.ParentWalletId.Value)).ToListAsync(ct);
        var all = own.Concat(children.Where(c => !ids.Contains(c.Id))).ToList();
        var mappings = await MappingsForAsync(all, ct);
        WalletDto Build(Wallet w) => new(w.Id, w.SourceSystem, w.ExternalWalletId, w.CustomerId, w.WalletType, w.Status, w.Currency, w.ParentWalletId, w.IsFrozen,
            mappings.ContainsKey(w.ExternalWalletId), mappings.GetValueOrDefault(w.ExternalWalletId), all.Where(c => c.ParentWalletId == w.Id).Select(Build).ToList());
        return all.Where(w => w.ParentWalletId is null || !all.Any(p => p.Id == w.ParentWalletId)).Select(Build).ToList();
    }

    private async Task<Dictionary<string, string>> MappingsForAsync(List<Wallet> wallets, CancellationToken ct)
    {
        var ext = wallets.Select(w => w.ExternalWalletId).ToList();
        var now = _clock.UtcNow;
        return (await _db.Set<WalletMapping>().AsNoTracking().Where(m => ext.Contains(m.ExternalWalletId) && m.IsActive && m.EffectiveFrom <= now && (m.EffectiveTo == null || m.EffectiveTo > now)).ToListAsync(ct))
            .GroupBy(m => m.ExternalWalletId).ToDictionary(g => g.Key, g => g.First().MappingCategory);
    }

    public async Task<WalletDto> CreateAsync(WalletInput i, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.SourceSystem) || string.IsNullOrWhiteSpace(i.ExternalWalletId)) throw new ValidationException("externalWalletId", "Source system and external wallet ID are required.");
        if (i.CustomerId is { } cid && !await _access.Customers().AnyAsync(c => c.Id == cid, ct)) throw new ValidationException("customerId", "Unknown customer.");
        if (await _db.Set<Wallet>().AnyAsync(w => w.SourceSystem == i.SourceSystem && w.ExternalWalletId == i.ExternalWalletId, ct))
            throw new ConflictException($"Wallet {i.SourceSystem}/{i.ExternalWalletId} already exists.");
        if (i.ParentWalletId is { } pid && !await _db.Set<Wallet>().AnyAsync(w => w.Id == pid, ct)) throw new ValidationException("parentWalletId", "Unknown parent wallet.");
        var w = new Wallet
        {
            TenantId = _user.TenantId, SourceSystem = i.SourceSystem, ExternalWalletId = i.ExternalWalletId, CustomerId = i.CustomerId, WalletType = i.WalletType,
            Status = i.Status, Currency = i.Currency, ParentWalletId = i.ParentWalletId, CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow
        };
        _db.Set<Wallet>().Add(w);
        _audit.Record("WALLET_CREATE", "Wallet", i.ExternalWalletId, null, i);
        await _db.SaveChangesAsync(ct);
        return new WalletDto(w.Id, w.SourceSystem, w.ExternalWalletId, w.CustomerId, w.WalletType, w.Status, w.Currency, w.ParentWalletId, w.IsFrozen, false, null, Array.Empty<WalletDto>());
    }

    public async Task<List<WalletMapping>> MappingsAsync(CancellationToken ct) =>
        await _db.Set<WalletMapping>().AsNoTracking().OrderBy(m => m.SourceSystem).ThenBy(m => m.ExternalWalletId).ThenByDescending(m => m.EffectiveFrom).ToListAsync(ct);

    public async Task<WalletMapping> CreateMappingAsync(WalletMappingInput i, CancellationToken ct)
    {
        Validate(i);
        var m = new WalletMapping { TenantId = _user.TenantId, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username };
        Apply(m, i);
        _db.Set<WalletMapping>().Add(m);
        _audit.Record("WALLET_MAPPING_CREATE", "WalletMapping", $"{i.SourceSystem}/{i.ExternalWalletId}", null, Snapshot(m), i.Reason);
        await _db.SaveChangesAsync(ct);
        return m;
    }

    public async Task<WalletMapping> UpdateMappingAsync(long id, WalletMappingInput i, CancellationToken ct)
    {
        Validate(i);
        var m = await _db.Set<WalletMapping>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Wallet mapping", id);
        _db.Entry(m).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(i.RowVersion, "wallet mapping");
        var old = Snapshot(m);
        Apply(m, i);
        m.RuleVersion++;
        m.UpdatedAt = _clock.UtcNow;
        m.UpdatedBy = _user.Username;
        _audit.Record("WALLET_MAPPING_UPDATE", "WalletMapping", $"{m.SourceSystem}/{m.ExternalWalletId}", old, Snapshot(m), i.Reason);
        await _db.SaveChangesAsync(ct);
        return m;
    }

    private static object Snapshot(WalletMapping m) => new { m.SourceSystem, m.ExternalWalletId, m.MappingCategory, m.Channel, m.EffectiveFrom, m.EffectiveTo, m.IsActive, m.Notes, m.RuleVersion };

    private static void Validate(WalletMappingInput i)
    {
        if (string.IsNullOrWhiteSpace(i.SourceSystem) || string.IsNullOrWhiteSpace(i.ExternalWalletId)) throw new ValidationException("externalWalletId", "Source system and wallet are required.");
        if (!Categories.Contains(i.MappingCategory)) throw new ValidationException("mappingCategory", $"Allowed categories: {string.Join(", ", Categories)}.");
        if (i.EffectiveTo is { } to && i.EffectiveFrom is { } from && to <= from) throw new ValidationException("effectiveTo", "effectiveTo must be after effectiveFrom.");
    }

    private void Apply(WalletMapping m, WalletMappingInput i)
    {
        m.SourceSystem = i.SourceSystem;
        m.ExternalWalletId = i.ExternalWalletId;
        m.MappingCategory = i.MappingCategory;
        m.Channel = i.Channel;
        m.EffectiveFrom = i.EffectiveFrom ?? _clock.UtcNow;
        m.EffectiveTo = i.EffectiveTo;
        m.IsActive = i.IsActive;
        m.Notes = i.Notes;
    }
}
