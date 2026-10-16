using System.Globalization;
using AML.Application.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Transactions;

public sealed class BalanceInput
{
    public string? WalletId { get; set; }
    public DateOnly? BusinessDate { get; set; }
    public decimal? Balance { get; set; }
    public string? Currency { get; set; }
}

public sealed class BalanceBatchRequest
{
    public List<BalanceInput> Balances { get; set; } = new();
}

public sealed record BalanceRecordResult(string? WalletId, DateOnly? BusinessDate, string Status, string? Error);

public sealed record BalanceBatchResult(int Received, int Stored, int Replaced, int Rejected, IReadOnlyList<BalanceRecordResult> Records);

/// <summary>
/// Ingests end-of-day wallet balances from a source system. (source, wallet, business date) is the idempotency key: a later
/// report for the same date replaces the stored balance. Balances are converted to the base currency with the FX rate effective
/// at the end of the business date; a missing rate leaves the base balance empty (never defaulted) and raises a data-quality item.
/// </summary>
public sealed class BalanceService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly TenantSettingsService _settings;
    private readonly FxService _fx;
    private readonly AccessPolicy _access;

    public BalanceService(IAmlDbContext db, ICurrentUser user, IClock clock, TenantSettingsService settings, FxService fx, AccessPolicy access)
    {
        _db = db; _user = user; _clock = clock; _settings = settings; _fx = fx; _access = access;
    }

    public async Task<BalanceBatchResult> IngestAsync(string source, BalanceBatchRequest req, CancellationToken ct)
    {
        var tenantId = _user.TenantId;
        var src = await _db.Set<SourceSystem>().AsNoTracking().FirstOrDefaultAsync(s => s.Code == source && s.IsActive, ct)
                  ?? throw new ValidationException("source", $"Unknown or inactive source system '{source}'.");
        if (req.Balances.Count == 0) throw new ValidationException("balances", "At least one balance is required.");
        if (req.Balances.Count > 5000) throw new ValidationException("balances", "Maximum 5000 balances per batch.");
        var tenant = await _settings.TenantAsync(tenantId, ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
        var currencies = (await _db.Set<LookupValue>().AsNoTracking().Where(l => l.Category == "CURRENCY" && l.IsActive).Select(l => l.Code).ToListAsync(ct)).ToHashSet();
        var results = new List<BalanceRecordResult>();
        int stored = 0, replaced = 0;
        foreach (var b in req.Balances)
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(b.WalletId)) problems.Add("walletId is required");
            if (b.BusinessDate is null) problems.Add("businessDate is required");
            if (b.Balance is null) problems.Add("balance is required");
            var currency = b.Currency?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(currency)) problems.Add("currency is required");
            else if (!currencies.Contains(currency)) problems.Add($"unsupported currency '{b.Currency}' (never defaulted)");
            if (problems.Count > 0) { results.Add(new(b.WalletId, b.BusinessDate, "Rejected", string.Join("; ", problems))); continue; }

            var walletId = b.WalletId!.Trim();
            var wallet = await _db.Set<Wallet>().AsNoTracking().Where(w => w.SourceSystem == src.Code && w.ExternalWalletId == walletId)
                .Select(w => new { w.Id, w.CustomerId }).FirstOrDefaultAsync(ct);
            // The balance is converted at the end of the local business day.
            var endLocal = DateTime.SpecifyKind(b.BusinessDate!.Value.ToDateTime(TimeOnly.MinValue).AddDays(1).AddTicks(-1), DateTimeKind.Unspecified);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, tz);
            var conv = await _fx.ConvertAsync(tenantId, b.Balance!.Value, currency!, tenant.BaseCurrency, endUtc, ct);
            var row = await _db.Set<WalletBalanceSnapshot>().FirstOrDefaultAsync(x => x.SourceSystem == src.Code && x.ExternalWalletId == walletId && x.BusinessDate == b.BusinessDate, ct);
            var isNew = row is null;
            row ??= _db.Set<WalletBalanceSnapshot>().Add(new WalletBalanceSnapshot { TenantId = tenantId, SourceSystem = src.Code, ExternalWalletId = walletId, BusinessDate = b.BusinessDate.Value }).Entity;
            row.WalletId = wallet?.Id;
            row.CustomerId = wallet?.CustomerId;
            row.Balance = b.Balance.Value;
            row.Currency = currency!;
            row.BaseBalance = conv.BaseAmount;
            row.ReceivedAt = _clock.UtcNow;
            if (conv.Status == "Missing")
                _db.Set<DataQualityIssue>().Add(new DataQualityIssue
                {
                    TenantId = tenantId, IssueType = "MissingFxRate", EntityType = "WalletBalance", EntityId = $"{walletId}@{b.BusinessDate:yyyy-MM-dd}",
                    Description = $"No effective FX rate {currency}->{tenant.BaseCurrency} for the balance of {walletId} on {b.BusinessDate:yyyy-MM-dd}; base balance not computed.",
                    Severity = "Medium", CreatedAt = _clock.UtcNow
                });
            if (wallet is null)
                _db.Set<DataQualityIssue>().Add(new DataQualityIssue
                {
                    TenantId = tenantId, IssueType = "UnresolvedParty", EntityType = "WalletBalance", EntityId = $"{walletId}@{b.BusinessDate:yyyy-MM-dd}",
                    Description = $"Balance received for unknown wallet {walletId}; stored but not linked to a customer.", Severity = "Low", CreatedAt = _clock.UtcNow
                });
            await _db.SaveChangesAsync(ct);
            if (isNew) stored++; else replaced++;
            results.Add(new(walletId, b.BusinessDate, isNew ? "Stored" : "Replaced", wallet is null ? "Wallet not found; balance not linked to a customer" : null));
        }
        return new BalanceBatchResult(req.Balances.Count, stored, replaced, results.Count(r => r.Status == "Rejected"), results);
    }

    public async Task<List<WalletBalanceSnapshot>> ForCustomerAsync(long customerId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await _access.Customers().AnyAsync(c => c.Id == customerId, ct)) throw new NotFoundException("Customer", customerId);
        var q = _db.Set<WalletBalanceSnapshot>().AsNoTracking().Where(x => x.CustomerId == customerId);
        if (from is { } f) q = q.Where(x => x.BusinessDate >= f);
        if (to is { } t) q = q.Where(x => x.BusinessDate <= t);
        return await q.OrderByDescending(x => x.BusinessDate).ThenBy(x => x.ExternalWalletId).Take(1000).ToListAsync(ct);
    }

}
