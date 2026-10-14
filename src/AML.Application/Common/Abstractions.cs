using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AML.Application.Common;

public interface IAmlDbContext
{
    DbSet<T> Set<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    DatabaseFacade Database { get; }
    EntityEntry<T> Entry<T>(T entity) where T : class;
    ChangeTracker ChangeTracker { get; }
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    long? UserId { get; }
    string Username { get; }
    long TenantId { get; }
    IReadOnlySet<string> Permissions { get; }
    IReadOnlySet<string> Roles { get; }
    string? Team { get; }
    string? DataScopeRegion { get; }
    string? SourceSystem { get; }
    bool IsSystem { get; }
    bool Has(string permission);
}

public interface ICorrelationContext
{
    string CorrelationId { get; set; }
    string? ClientIp { get; set; }
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public interface ICryptoService
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
    /// <summary>Keyed HMAC search token over a normalized value (never returned by APIs).</summary>
    string SearchToken(string value);
    /// <summary>Integrity HMAC (audit rows, payload signatures).</summary>
    string IntegrityHash(string value);
    string HashPassword(string password);
    bool VerifyPassword(string password, string hash);
}

public interface IAuditService
{
    /// <summary>Adds an audit row to the current unit of work (persisted with the caller's SaveChanges).</summary>
    void Record(string action, string entityType, string? entityId, object? oldValues = null, object? newValues = null,
        string? reason = null, bool restricted = false, string outcome = "Success");

    /// <summary>Persists an audit row immediately in its own unit of work (e.g. denied attempts).</summary>
    Task RecordNowAsync(string action, string entityType, string? entityId, object? oldValues = null, object? newValues = null,
        string? reason = null, bool restricted = false, string outcome = "Success", CancellationToken ct = default);
}

public interface IFileStore
{
    Task<string> SaveAsync(string category, Stream content, CancellationToken ct);
    Task<Stream?> OpenAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public interface IMalwareScanner
{
    Task<(bool Clean, string? Threat)> ScanAsync(Stream content, CancellationToken ct);
}

public interface IEventPublisher
{
    Task PublishAsync(Guid messageId, string eventType, string payloadJson, CancellationToken ct);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed class PageRequest
{
    public const int MaxPageSize = 200;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Sort { get; set; }
    public string? Dir { get; set; }

    public int SafePage => Page < 1 ? 1 : Page;
    public int SafePageSize => PageSize < 1 ? 25 : Math.Min(PageSize, MaxPageSize);
    public bool Desc => string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase);
}

public static class Paging
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> q, PageRequest p, CancellationToken ct)
    {
        var total = await q.CountAsync(ct);
        var items = await q.Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        return new PagedResult<T>(items, p.SafePage, p.SafePageSize, total);
    }

    /// <summary>Applies an allow-listed sort; unknown sort fields are rejected (no dynamic SQL).</summary>
    public static IQueryable<T> SortBy<T>(this IQueryable<T> q, PageRequest p, IReadOnlyDictionary<string, System.Linq.Expressions.Expression<Func<T, object?>>> allowed, string defaultSort, bool defaultDesc = false)
    {
        var key = string.IsNullOrWhiteSpace(p.Sort) ? defaultSort : p.Sort;
        if (!allowed.TryGetValue(key, out var expr))
            throw new ValidationException("sort", $"Unsupported sort field '{p.Sort}'. Allowed: {string.Join(", ", allowed.Keys)}.");
        var desc = p.Dir is null ? string.IsNullOrWhiteSpace(p.Sort) && defaultDesc : p.Desc;
        if (p.Dir is not null && p.Dir is not ("asc" or "desc"))
            throw new ValidationException("dir", "dir must be asc or desc.");
        return desc ? q.OrderByDescending(expr) : q.OrderBy(expr);
    }
}
