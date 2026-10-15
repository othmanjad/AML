using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AML.Application.Admin;
using AML.Application.Common;
using AML.Application.Realtime;
using AML.Domain.Entities;
using AML.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AML.Infrastructure.Services;

public sealed class CorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ClientIp { get; set; }
}

/// <summary>Audit trail writer. Values are sanitized (sensitive keys masked) and every row carries an HMAC for tamper detection.</summary>
public sealed class AuditService : IAuditService
{
    private static readonly Regex LongDigits = new(@"\b\d{8,}\b", RegexOptions.Compiled);
    private readonly AmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ICorrelationContext _corr;
    private readonly ICryptoService _crypto;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopes;

    public AuditService(AmlDbContext db, ICurrentUser user, ICorrelationContext corr, ICryptoService crypto, IClock clock, IServiceScopeFactory scopes)
    {
        _db = db; _user = user; _corr = corr; _crypto = crypto; _clock = clock; _scopes = scopes;
    }

    public static string? Serialize(object? v)
    {
        if (v is null) return null;
        var json = v as string ?? JsonSerializer.Serialize(v);
        return LongDigits.Replace(json, m => new string('*', m.Value.Length - 4) + m.Value[^4..]);
    }

    private AuditLog Build(string action, string entityType, string? entityId, object? oldValues, object? newValues, string? reason, bool restricted, string outcome)
    {
        var a = new AuditLog
        {
            TenantId = _user.TenantId == 0 ? null : _user.TenantId, TimestampUtc = _clock.UtcNow, Actor = string.IsNullOrEmpty(_user.Username) ? "anonymous" : _user.Username,
            Action = action, EntityType = entityType, EntityId = entityId, OldValues = Serialize(oldValues), NewValues = Serialize(newValues), Reason = reason,
            CorrelationId = _corr.CorrelationId, SourceContext = _corr.ClientIp, IsRestricted = restricted, Outcome = outcome
        };
        a.TimestampUtc = new DateTime(a.TimestampUtc.Ticks - a.TimestampUtc.Ticks % 10, DateTimeKind.Utc);
        a.Hash = _crypto.IntegrityHash(AuditCanonical.Of(a));
        return a;
    }

    public void Record(string action, string entityType, string? entityId, object? oldValues = null, object? newValues = null, string? reason = null, bool restricted = false, string outcome = "Success") =>
        _db.Set<AuditLog>().Add(Build(action, entityType, entityId, oldValues, newValues, reason, restricted, outcome));

    public async Task RecordNowAsync(string action, string entityType, string? entityId, object? oldValues = null, object? newValues = null, string? reason = null, bool restricted = false,
        string outcome = "Success", CancellationToken ct = default)
    {
        var row = Build(action, entityType, entityId, oldValues, newValues, reason, restricted, outcome);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmlDbContext>();
        db.Set<AuditLog>().Add(row);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>File storage outside the web root; files are only reachable through authorized API endpoints.</summary>
public sealed class LocalFileStore : IFileStore
{
    private static readonly Regex KeyPattern = new("^[a-z-]+/[0-9a-f]{32}$", RegexOptions.Compiled);
    private readonly string _root;

    public LocalFileStore(IConfiguration config)
    {
        _root = Path.GetFullPath(config["Storage:Root"] ?? Path.Combine(Path.GetTempPath(), "aml-files"));
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(string category, Stream content, CancellationToken ct)
    {
        var key = $"{category}/{Guid.NewGuid():N}";
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var f = File.Create(path);
        await content.CopyToAsync(f, ct);
        return key;
    }

    public Task<Stream?> OpenAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        if (!KeyPattern.IsMatch(key)) throw new ValidationException("key", "Invalid storage key.");
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ValidationException("key", "Invalid storage key.");
        return full;
    }
}

/// <summary>Signature-based scanner (detects the EICAR test signature). Plug in ClamAV or the enterprise AV via this interface in production.</summary>
public sealed class SignatureMalwareScanner : IMalwareScanner
{
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes("EICAR-STANDARD-ANTIVIRUS-TEST-FILE");

    public async Task<(bool Clean, string? Threat)> ScanAsync(Stream content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        for (var i = 0; i + Eicar.Length <= bytes.Length; i++)
            if (bytes.AsSpan(i, Eicar.Length).SequenceEqual(Eicar)) return (false, "EICAR-Test-Signature");
        return (true, null);
    }
}

/// <summary>Development event publisher: records publication with consumer-side dedupe by message ID (exactly-once effect).</summary>
public sealed class DbEventPublisher : IEventPublisher
{
    private readonly IServiceScopeFactory _scopes;
    public DbEventPublisher(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task PublishAsync(Guid messageId, string eventType, string payloadJson, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmlDbContext>();
        if (await db.Set<PublishedEvent>().AnyAsync(p => p.MessageId == messageId, ct)) return;
        db.Set<PublishedEvent>().Add(new PublishedEvent { MessageId = messageId, EventType = eventType, PublishedAt = DateTime.UtcNow });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* already published by a concurrent publisher */ }
    }
}

public sealed class EnvironmentInfo : IEnvironmentInfo
{
    public EnvironmentInfo(string name) => Name = name;
    public string Name { get; }
    public bool IsProduction => string.Equals(Name, "Production", StringComparison.OrdinalIgnoreCase);
}

public static class SensitiveDataMasker
{
    // Standalone digit runs (identity/account numbers); runs embedded in alphanumeric tokens such as correlation IDs or hashes are kept,
    // otherwise log lines can no longer be correlated.
    private static readonly Regex Digits = new(@"(?<![\dA-Za-z])\d{8,}(?![\dA-Za-z])", RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"(?i)(bearer\s+)[A-Za-z0-9\-_\.=]+", RegexOptions.Compiled);
    private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"([A-Za-z0-9._%+-])[A-Za-z0-9._%+-]*@", RegexOptions.Compiled);
    private static readonly Regex Secrets = new("(?i)(\"?(password|secret|clientSecret|token)\"?\\s*[:=]\\s*\"?)[^\",\\s}]+", RegexOptions.Compiled);
    private static readonly Regex Hex = new(@"\b[0-9A-F]{64}\b", RegexOptions.Compiled);

    public static string Mask(string s)
    {
        s = Jwt.Replace(s, "***JWT***");
        s = Bearer.Replace(s, "$1***");
        s = Secrets.Replace(s, "$1***");
        s = Hex.Replace(s, "***HASH***");
        s = Digits.Replace(s, m => new string('*', m.Value.Length - 4) + m.Value[^4..]);
        s = Email.Replace(s, "$1***@");
        return s;
    }
}
