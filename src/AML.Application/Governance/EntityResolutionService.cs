using System.Globalization;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Governance;

/// <summary>Entity-resolution links (signals, never automatic merges) and permission-aware network graph.</summary>
public sealed class EntityResolutionService
{
    public const string RuleVersion = "ER-1.0";
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly AccessPolicy _access;
    private readonly INameMatcher _matcher;
    private readonly TenantSettingsService _settings;

    public EntityResolutionService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, AccessPolicy access, INameMatcher matcher, TenantSettingsService settings)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _access = access; _matcher = matcher; _settings = settings;
    }

    public async Task<object> RunAsync(long tenantId, CancellationToken ct)
    {
        var created = 0;
        var existing = (await _db.Set<EntityLink>().IgnoreQueryFilters().Where(l => l.TenantId == tenantId).Select(l => new { l.CustomerAId, l.CustomerBId, l.LinkType }).ToListAsync(ct))
            .Select(l => (l.CustomerAId, l.CustomerBId, l.LinkType)).ToHashSet();
        async Task Add(long a, long b, string type, string evidence, decimal confidence, string status)
        {
            if (a == b) return;
            var (x, y) = a < b ? (a, b) : (b, a);
            if (!existing.Add((x, y, type))) return;
            _db.Set<EntityLink>().Add(new EntityLink { TenantId = tenantId, CustomerAId = x, CustomerBId = y, LinkType = type, Evidence = evidence, Confidence = confidence, RuleVersion = RuleVersion, Status = status, CreatedAt = _clock.UtcNow });
            created++;
            await Task.CompletedTask;
        }
        foreach (var g in (await _db.Set<CustomerIdentification>().IgnoreQueryFilters().AsNoTracking().Where(i => i.TenantId == tenantId).Select(i => new { i.CustomerId, i.IdNumberToken, i.IdType }).ToListAsync(ct))
                 .GroupBy(i => i.IdNumberToken).Where(g => g.Select(x => x.CustomerId).Distinct().Count() > 1))
        {
            var ids = g.Select(x => x.CustomerId).Distinct().ToList();
            for (var i = 0; i < ids.Count; i++) for (var j = i + 1; j < ids.Count; j++)
                await Add(ids[i], ids[j], "SameIdentity", $"Same {g.First().IdType} (matched by keyed identity token; token not disclosed)", 1.0m, "Linked");
        }
        foreach (var g in (await _db.Set<CustomerContact>().IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId && c.SearchToken != null).Select(c => new { c.CustomerId, c.SearchToken, c.ContactType }).ToListAsync(ct))
                 .GroupBy(c => (c.SearchToken, c.ContactType)).Where(g => g.Select(x => x.CustomerId).Distinct().Count() > 1))
        {
            var ids = g.Select(x => x.CustomerId).Distinct().ToList();
            for (var i = 0; i < ids.Count; i++) for (var j = i + 1; j < ids.Count; j++)
                await Add(ids[i], ids[j], g.Key.ContactType == "Email" ? "SameEmail" : "SamePhone", $"Shared {g.Key.ContactType.ToLowerInvariant()} (keyed token)", 0.8m, "Linked");
        }
        var devices = await _db.Set<CustomerDeviceSignal>().IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == tenantId).Select(s => new { s.CustomerId, s.DeviceId, s.IpAddress }).ToListAsync(ct);
        foreach (var g in devices.GroupBy(d => d.DeviceId).Where(g => g.Select(x => x.CustomerId).Distinct().Count() > 1))
        {
            var ids = g.Select(x => x.CustomerId).Distinct().ToList();
            for (var i = 0; i < ids.Count; i++) for (var j = i + 1; j < ids.Count; j++)
                await Add(ids[i], ids[j], "SharedDevice", $"Shared device (signal, not proof of identity); {ids.Count} customers on device", 0.6m, "Linked");
        }
        foreach (var g in devices.Where(d => d.IpAddress != null).GroupBy(d => d.IpAddress).Where(g => g.Select(x => x.CustomerId).Distinct().Count() > 1))
        {
            var ids = g.Select(x => x.CustomerId).Distinct().ToList();
            for (var i = 0; i < ids.Count; i++) for (var j = i + 1; j < ids.Count; j++)
                await Add(ids[i], ids[j], "SharedIp", $"Shared IP address; may be NAT/carrier-grade/public Wi-Fi - context only, not identity ({ids.Count} customers)", 0.3m, "Candidate");
        }
        var autoThreshold = await _settings.DecimalSettingAsync("EntityResolution:NameLinkThreshold", 0.95m, tenantId, ct);
        var candThreshold = await _settings.DecimalSettingAsync("EntityResolution:NameCandidateThreshold", 0.85m, tenantId, ct);
        var people = await _db.Set<Customer>().IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId && c.CustomerType == CustomerTypes.Individual && !c.IsAnonymised)
            .Select(c => new { c.Id, c.NameLatin, c.NameArabic, c.DateOfBirth }).Take(5000).ToListAsync(ct);
        foreach (var block in people.GroupBy(p => p.DateOfBirth).Where(g => g.Key != null && g.Count() > 1))
        {
            var list = block.ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var s = _matcher.Match(list[i].NameLatin ?? list[i].NameArabic ?? "", list[j].NameLatin ?? list[j].NameArabic ?? "");
                    if (s.Score < candThreshold) continue;
                    // Similar names are never auto-merged; even high-confidence links stay links.
                    await Add(list[i].Id, list[j].Id, "SimilarName", $"Name similarity {s.Score:0.00} with same date of birth: {s.Explanation}", s.Score, s.Score >= autoThreshold ? "Linked" : "Candidate");
                }
        }
        await _db.SaveChangesAsync(ct);
        _audit.Record("ENTITY_RESOLUTION_RUN", "EntityLink", null, null, new { created, RuleVersion });
        await _db.SaveChangesAsync(ct);
        return new { created, ruleVersion = RuleVersion };
    }

    public async Task<CustomerDeviceSignal> AddSignalAsync(long customerId, string deviceId, string? ip, string eventType, DateTime? at, CancellationToken ct)
    {
        var c = await _access.Customers().AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId, ct) ?? throw new NotFoundException("Customer", customerId);
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ValidationException("deviceId", "Device ID is required.");
        var s = new CustomerDeviceSignal { TenantId = c.TenantId, CustomerId = customerId, DeviceId = deviceId, IpAddress = ip, EventType = eventType, OccurredAt = at ?? _clock.UtcNow };
        _db.Set<CustomerDeviceSignal>().Add(s);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    public async Task<object> DeviceSignalsAsync(long customerId, CancellationToken ct)
    {
        if (!await _access.Customers().AnyAsync(c => c.Id == customerId, ct)) throw new NotFoundException("Customer", customerId);
        var list = await _db.Set<CustomerDeviceSignal>().AsNoTracking().Where(s => s.CustomerId == customerId).OrderByDescending(s => s.OccurredAt).Take(200).ToListAsync(ct);
        var unmask = _user.Has(Perm.DevicesUnmask);
        return list.Select(s => new { s.Id, deviceId = _access.MaskDevice(s.DeviceId), ipAddress = unmask ? s.IpAddress : s.IpAddress is null ? null : FieldValueValidator.Mask(s.IpAddress), s.EventType, s.OccurredAt, s.GeoCountry });
    }

    /// <summary>Network graph limited by configured depth; customers the user may not see (data scope or restricted investigations) are omitted without trace.</summary>
    public async Task<object> GraphAsync(long customerId, int depth, CancellationToken ct)
    {
        var maxDepth = await _settings.IntSettingAsync("Graph:MaxDepth", 3, ct: ct);
        var maxNodes = await _settings.IntSettingAsync("Graph:MaxNodes", 200, ct: ct);
        var d = Math.Clamp(depth, 1, maxDepth);
        if (!await _access.Customers().AnyAsync(c => c.Id == customerId, ct)) throw new NotFoundException("Customer", customerId);
        var hidden = await RestrictedCustomersAsync(ct);
        var visibleIds = _access.Customers().Select(c => c.Id);
        var nodes = new HashSet<long> { customerId };
        var edges = new List<object>();
        var frontier = new List<long> { customerId };
        var truncated = false;
        for (var level = 0; level < d && frontier.Count > 0; level++)
        {
            var next = new List<long>();
            var links = await _db.Set<EntityLink>().AsNoTracking().Where(l => frontier.Contains(l.CustomerAId) || frontier.Contains(l.CustomerBId)).ToListAsync(ct);
            var flows = await _db.Set<Transaction>().AsNoTracking().Where(t => t.SenderCustomerId != null && t.ReceiverCustomerId != null &&
                    (frontier.Contains(t.SenderCustomerId.Value) || frontier.Contains(t.ReceiverCustomerId.Value)))
                .GroupBy(t => new { t.SenderCustomerId, t.ReceiverCustomerId }).Select(g => new { g.Key.SenderCustomerId, g.Key.ReceiverCustomerId, Count = g.Count(), Sum = g.Sum(x => x.BaseAmount ?? 0) }).ToListAsync(ct);
            var candidates = links.SelectMany(l => new[] { l.CustomerAId, l.CustomerBId }).Concat(flows.SelectMany(f => new[] { f.SenderCustomerId!.Value, f.ReceiverCustomerId!.Value })).Distinct().ToList();
            var allowed = (await _db.Set<Customer>().AsNoTracking().Where(c => candidates.Contains(c.Id) && visibleIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)).Where(x => !hidden.Contains(x)).ToHashSet();
            foreach (var l in links.Where(l => allowed.Contains(l.CustomerAId) && allowed.Contains(l.CustomerBId)))
            {
                edges.Add(new { from = l.CustomerAId, to = l.CustomerBId, kind = "link", l.LinkType, l.Confidence, l.Status });
                foreach (var n in new[] { l.CustomerAId, l.CustomerBId }) if (nodes.Count < maxNodes && nodes.Add(n)) next.Add(n); else if (nodes.Count >= maxNodes) truncated = true;
            }
            foreach (var f in flows.Where(f => allowed.Contains(f.SenderCustomerId!.Value) && allowed.Contains(f.ReceiverCustomerId!.Value)))
            {
                edges.Add(new { from = f.SenderCustomerId, to = f.ReceiverCustomerId, kind = "flow", f.Count, amount = f.Sum });
                foreach (var n in new[] { f.SenderCustomerId!.Value, f.ReceiverCustomerId!.Value }) if (nodes.Count < maxNodes && nodes.Add(n)) next.Add(n); else if (nodes.Count >= maxNodes) truncated = true;
            }
            frontier = next;
        }
        var nodeInfo = await _db.Set<Customer>().AsNoTracking().Where(c => nodes.Contains(c.Id)).Select(c => new { c.Id, c.NameLatin, c.NameArabic, c.CustomerType, c.Status }).ToListAsync(ct);
        var wallets = await _db.Set<Wallet>().AsNoTracking().Where(w => w.CustomerId != null && nodes.Contains(w.CustomerId.Value)).Select(w => new { w.Id, w.ExternalWalletId, w.CustomerId }).ToListAsync(ct);
        var devs = await _db.Set<CustomerDeviceSignal>().AsNoTracking().Where(s => nodes.Contains(s.CustomerId)).Select(s => new { s.CustomerId, s.DeviceId }).Distinct().ToListAsync(ct);
        return new
        {
            root = customerId, depth = d, maxDepth, truncated, nodes = nodeInfo, wallets,
            devices = devs.Select(x => new { x.CustomerId, deviceId = _access.MaskDevice(x.DeviceId) }), edges = edges.DistinctBy(e => System.Text.Json.JsonSerializer.Serialize(e)).ToList()
        };
    }

    /// <summary>Customers whose primary restricted case is not visible to the user (hidden from graphs).</summary>
    private async Task<HashSet<long>> RestrictedCustomersAsync(CancellationToken ct)
    {
        if (_access.CanSeeRestricted) return new HashSet<long>();
        var visibleCases = _access.Cases().Select(c => c.Id);
        var restricted = await _db.Set<AmlCase>().AsNoTracking().Where(c => c.Confidentiality != Confidentiality.Standard && !visibleCases.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        return (await _db.Set<AmlCaseCustomer>().AsNoTracking().Where(x => restricted.Contains(x.CaseId)).Select(x => x.CustomerId).ToListAsync(ct)).ToHashSet();
    }
}
