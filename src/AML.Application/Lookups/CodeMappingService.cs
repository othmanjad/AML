using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Lookups;

/// <summary>Result of translating one source code: Mapped (a mapping exists), Direct (already an AML code) or Unmapped.</summary>
public sealed record CodeMapResult(string? Code, string Status, string ExternalCode)
{
    public bool IsUnmapped => Status == "Unmapped";
}

/// <summary>
/// Translates codes sent by source systems (wallet, core) to AML codes using <see cref="LookupCodeMapping"/>. A category is
/// "managed" for a source once it has at least one active mapping: unknown codes are then queued as unmapped (and not
/// rejected); otherwise the platform keeps its strict validation of AML codes. Cached per request scope.
/// </summary>
public sealed class CodeMappingService
{
    public const string Country = "COUNTRY";
    public const string CustomerType = "CUSTOMER_TYPE";
    public const string Region = "REGION";
    public const string SourceOfFunds = "SOURCE_OF_FUNDS";
    public const string Occupation = "OCCUPATION";
    public const string TxnType = "TXN_TYPE";
    public const string Channel = "CHANNEL";

    private readonly IAmlDbContext _db;
    private readonly Dictionary<(string, string), Dictionary<string, string>?> _maps = new();
    private readonly Dictionary<string, HashSet<string>?> _codes = new(StringComparer.Ordinal);

    public CodeMappingService(IAmlDbContext db) => _db = db;

    /// <summary>Active mappings of a source and category (external code → AML code), null when the category is not managed.</summary>
    private async Task<Dictionary<string, string>?> MappingsAsync(string source, string category, CancellationToken ct)
    {
        if (_maps.TryGetValue((source, category), out var m)) return m;
        var rows = await _db.Set<LookupCodeMapping>().AsNoTracking().Where(x => x.SourceSystem == source && x.Category == category && x.IsActive)
            .Select(x => new { x.ExternalCode, x.Code }).ToListAsync(ct);
        m = rows.Count == 0 ? null : rows.ToDictionary(x => x.ExternalCode, x => x.Code, StringComparer.OrdinalIgnoreCase);
        return _maps[(source, category)] = m;
    }

    public async Task<bool> IsManagedAsync(string source, string category, CancellationToken ct) => await MappingsAsync(source, category, ct) is not null;

    /// <summary>
    /// Valid AML codes of a category: COUNTRY / TXN_TYPE / CURRENCY / ... from the reference lookups, CUSTOMER_TYPE from the
    /// customer types, select fields from their options. Null for free-text categories (any value accepted).
    /// </summary>
    public async Task<HashSet<string>?> AmlCodesAsync(string category, CancellationToken ct)
    {
        if (_codes.TryGetValue(category, out var c)) return c;
        if (category == CustomerType) c = CustomerTypes.All.ToHashSet(StringComparer.OrdinalIgnoreCase);
        else
        {
            var lookups = await _db.Set<LookupValue>().AsNoTracking().Where(l => l.Category == category && l.IsActive).Select(l => l.Code).ToListAsync(ct);
            if (lookups.Count > 0) c = lookups.ToHashSet(StringComparer.OrdinalIgnoreCase);
            else
            {
                var options = await _db.Set<CustomerFieldOption>().AsNoTracking().Where(o => o.FieldCode == category && o.IsActive).Select(o => o.OptionCode).ToListAsync(ct);
                c = options.Count > 0 ? options.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            }
        }
        return _codes[category] = c;
    }

    /// <summary>Translates one value. Values with a mapping are Mapped; known AML codes and free-text categories pass as Direct.</summary>
    public async Task<CodeMapResult> MapAsync(string? source, string category, string? value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) return new CodeMapResult(value, "Direct", value ?? "");
        var v = value.Trim();
        var maps = source is null ? null : await MappingsAsync(source, category, ct);
        if (maps is not null && maps.TryGetValue(v, out var code)) return new CodeMapResult(code, "Mapped", v);
        var known = await AmlCodesAsync(category, ct);
        if (known is null) return new CodeMapResult(v, "Direct", v);
        var hit = known.FirstOrDefault(k => string.Equals(k, v, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return new CodeMapResult(hit, "Direct", v);
        // Not an AML code: queued as unmapped when the category is managed for the source; otherwise strict validation applies.
        return maps is not null ? new CodeMapResult(null, "Unmapped", v) : new CodeMapResult(v, "Direct", v);
    }

    /// <summary>Clears the scope cache after mappings change.</summary>
    public void Invalidate()
    {
        _maps.Clear();
        _codes.Clear();
    }
}
