using System.Text.Json;
using AML.Application.Common;
using AML.Domain.Entities;
using AML.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Rules;

/// <summary>Database-backed data provider for the rule evaluator (cached per execution).</summary>
public sealed class RuleDataProvider : IRuleDataProvider
{
    private readonly IAmlDbContext _db;
    private readonly long _tenantId;
    private readonly Dictionary<long, Customer?> _customers = new();
    private readonly Dictionary<(long, string), object?> _attr = new();
    private readonly Dictionary<(DateTime, DateTime), List<RuleTxn>> _edges = new();

    public RuleDataProvider(IAmlDbContext db, long tenantId)
    {
        _db = db; _tenantId = tenantId;
    }

    private Customer? Cust(long id)
    {
        if (!_customers.TryGetValue(id, out var c))
            _customers[id] = c = _db.Set<Customer>().IgnoreQueryFilters().AsNoTracking().FirstOrDefault(x => x.Id == id && x.TenantId == _tenantId);
        return c;
    }

    public object? GetAttribute(long? customerId, string field, DateTime asOfUtc)
    {
        if (customerId is not { } id) return null;
        var key = (id, field + "@" + asOfUtc.Date.ToString("yyyyMMdd"));
        if (_attr.TryGetValue(key, out var cached)) return cached;
        object? v = null;
        var c = Cust(id);
        if (c is not null)
        {
            switch (field)
            {
                case "customer.type": v = c.CustomerType; break;
                case "customer.nationality": v = c.NationalityCountry; break;
                case "customer.residence": v = c.ResidenceCountry; break;
                case "customer.status": v = c.Status; break;
                case "customer.region": v = c.Region; break;
                case "customer.isPep": v = c.IsPep; break;
                case "customer.accountAgeDays":
                    // No fallback to the record's creation time: a missing registration date is reported as missing input.
                    // Activity dated before registration is inconsistent data: also treated as missing (never as a new account).
                    if (c.RegistrationDate is { } registered && DateOnly.FromDateTime(asOfUtc).DayNumber >= registered.DayNumber)
                        v = (decimal)(DateOnly.FromDateTime(asOfUtc).DayNumber - registered.DayNumber);
                    break;
                case "customer.age":
                    if (c.DateOfBirth is { } dob)
                    {
                        var on = DateOnly.FromDateTime(asOfUtc);
                        var years = on.Year - dob.Year - (on < dob.AddYears(on.Year - dob.Year) ? 1 : 0);
                        v = (decimal)years;
                    }
                    break;
                case "customer.riskLevel":
                    v = _db.Set<CurrentCustomerRiskProfile>().IgnoreQueryFilters().AsNoTracking().Where(p => p.CustomerId == id).Select(p => p.Level).FirstOrDefault();
                    break;
                default:
                    if (field.StartsWith("profile."))
                    {
                        var fp = _db.Set<CustomerFinancialProfile>().IgnoreQueryFilters().AsNoTracking()
                            .Where(p => p.CustomerId == id && p.EffectiveFrom <= asOfUtc && (p.EffectiveTo == null || p.EffectiveTo > asOfUtc))
                            .OrderByDescending(p => p.EffectiveFrom).FirstOrDefault();
                        v = field switch
                        {
                            "profile.expectedMonthlyVolume" => fp?.ExpectedMonthlyVolume,
                            "profile.declaredMonthlyIncome" => fp?.DeclaredMonthlyIncome,
                            "profile.expectedMonthlyCount" => fp?.ExpectedMonthlyCount is { } n ? (decimal)n : null,
                            _ => null
                        };
                    }
                    else if (field.StartsWith("field:"))
                    {
                        var code = field[6..];
                        var fv = _db.Set<CustomerFieldValue>().IgnoreQueryFilters().AsNoTracking()
                            .Where(x => x.CustomerId == id && x.FieldCode == code && x.EffectiveFrom <= asOfUtc && (x.EffectiveTo == null || x.EffectiveTo > asOfUtc))
                            .OrderByDescending(x => x.EffectiveFrom).FirstOrDefault();
                        if (fv is not null)
                            v = fv.ValueNumber is not null ? fv.ValueNumber : fv.ValueBool is not null ? fv.ValueBool : fv.ValueDate is not null ? fv.ValueDate
                                : fv.ValueJson is not null ? JsonSerializer.Deserialize<List<string>>(fv.ValueJson) : fv.ValueText;
                    }
                    break;
            }
        }
        _attr[key] = v;
        return v;
    }

    public IReadOnlyList<decimal> GetPeerValues(long subjectCustomerId, string peerGroup, DateTime fromUtc, DateTime toUtc)
    {
        var s = Cust(subjectCustomerId);
        if (s is null) return Array.Empty<decimal>();
        var peers = _db.Set<Customer>().IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == _tenantId && c.Id != s.Id && c.CustomerType == s.CustomerType);
        if (peerGroup == "typeAndRegion") peers = peers.Where(c => c.Region == s.Region);
        var ids = peers.Select(c => c.Id).ToList();
        var sums = _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == _tenantId && t.AgentCustomerId != null && ids.Contains(t.AgentCustomerId.Value) && t.TimestampUtc > fromUtc && t.TimestampUtc <= toUtc && t.Status == TxnStatuses.Completed)
            .GroupBy(t => t.AgentCustomerId).Select(g => g.Sum(x => x.BaseAmount ?? 0)).ToList();
        // Peers with no activity count as zero volume.
        return sums.Concat(Enumerable.Repeat(0m, Math.Max(0, ids.Count - sums.Count))).ToList();
    }

    public int CountNewCustomersByAgent(long agentCustomerId, DateTime fromUtc, DateTime toUtc)
    {
        var from = DateOnly.FromDateTime(fromUtc);
        var to = DateOnly.FromDateTime(toUtc);
        return _db.Set<Customer>().IgnoreQueryFilters().Count(c => c.TenantId == _tenantId && c.OnboardingAgentId == agentCustomerId && c.RegistrationDate >= from && c.RegistrationDate <= to);
    }

    public IReadOnlyList<long> CustomersOnDevice(string deviceId, DateTime fromUtc, DateTime toUtc)
    {
        var a = _db.Set<CustomerDeviceSignal>().IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == _tenantId && s.DeviceId == deviceId && s.OccurredAt > fromUtc && s.OccurredAt <= toUtc).Select(s => s.CustomerId).ToList();
        var b = _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == _tenantId && t.DeviceId == deviceId && t.TimestampUtc > fromUtc && t.TimestampUtc <= toUtc && t.SenderCustomerId != null)
            .Select(t => t.SenderCustomerId!.Value).ToList();
        return a.Concat(b).Distinct().OrderBy(x => x).ToList();
    }

    private readonly Dictionary<long, IReadOnlySet<long>> _links = new();
    private readonly Dictionary<long, IReadOnlyList<RuleSignal>> _signals = new();
    private readonly Dictionary<long, IReadOnlyList<string>> _devices = new();

    public IReadOnlySet<long> GetLinkedCustomers(long customerId)
    {
        if (_links.TryGetValue(customerId, out var l)) return l;
        var rows = _db.Set<EntityLink>().IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == _tenantId && x.Status == "Linked" && (x.CustomerAId == customerId || x.CustomerBId == customerId))
            .Select(x => x.CustomerAId == customerId ? x.CustomerBId : x.CustomerAId).ToList();
        return _links[customerId] = rows.ToHashSet();
    }

    public IReadOnlyList<RuleSignal> GetSignals(long customerId)
    {
        if (_signals.TryGetValue(customerId, out var l)) return l;
        var rows = _db.Set<CustomerDeviceSignal>().IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == _tenantId && x.CustomerId == customerId).OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
            .Select(x => new { x.DeviceId, x.IpAddress, x.GeoCountry, x.EventType, x.OccurredAt }).ToList();
        var roaming = RoamingPrefixes();
        return _signals[customerId] = rows.Select(x => new RuleSignal(x.DeviceId, x.IpAddress, x.GeoCountry, x.EventType, x.OccurredAt,
            x.IpAddress is { } ip && roaming.Any(p => ip.StartsWith(p, StringComparison.Ordinal)))).ToList();
    }

    private List<string>? _roaming;

    /// <summary>Carrier/roaming IP prefixes (setting Monitoring:RoamingIpPrefixes, comma separated): logins from them have no reliable location.</summary>
    private List<string> RoamingPrefixes()
    {
        if (_roaming is not null) return _roaming;
        var v = _db.Set<SystemSetting>().IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == _tenantId && x.Key == "Monitoring:RoamingIpPrefixes").Select(x => x.Value).FirstOrDefault();
        return _roaming = (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public IReadOnlyList<string> GetDevices(long customerId)
    {
        if (_devices.TryGetValue(customerId, out var l)) return l;
        var fromTxns = _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == _tenantId && t.SenderCustomerId == customerId && t.DeviceId != null).Select(t => t.DeviceId!).Distinct().Take(200).ToList();
        return _devices[customerId] = GetSignals(customerId).Select(x => x.DeviceId).Concat(fromTxns).Distinct().ToList();
    }

    public IReadOnlyList<RuleTxn> GetNetworkEdges(DateTime fromUtc, DateTime toUtc)
    {
        if (_edges.TryGetValue((fromUtc, toUtc), out var e)) return e;
        e = _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == _tenantId && t.TimestampUtc > fromUtc && t.TimestampUtc <= toUtc && !t.IsReversal && t.EconomicLegOfId == null && t.Status == TxnStatuses.Completed &&
                        t.SenderCustomerId != null && t.ReceiverCustomerId != null)
            .Select(t => new RuleTxn
            {
                Id = t.Id, SourceTransactionId = t.SourceTransactionId, TimestampUtc = t.TimestampUtc, Amount = t.Amount, Currency = t.Currency, BaseAmount = t.BaseAmount,
                SenderCustomerId = t.SenderCustomerId, ReceiverCustomerId = t.ReceiverCustomerId, Status = t.Status, Type = t.TransactionType
            }).Take(50000).ToList();
        _edges[(fromUtc, toUtc)] = e;
        return e;
    }

    public static RuleTxn Project(Transaction t, string direction) => new()
    {
        Id = t.Id, SourceTransactionId = t.SourceTransactionId, TimestampUtc = t.TimestampUtc, Amount = t.Amount, Currency = t.Currency, BaseAmount = t.BaseAmount,
        BaseCurrency = t.BaseCurrency, FxStatus = t.FxStatus, FxRate = t.FxRate, FxRateDate = t.FxRateDate, FxRateVersion = t.FxRateVersion, Type = t.TransactionType,
        Status = t.Status, Channel = t.Channel, Fee = t.Fee, SenderCustomerId = t.SenderCustomerId, ReceiverCustomerId = t.ReceiverCustomerId,
        SenderWallet = t.SenderWalletExternalId, ReceiverWallet = t.ReceiverWalletExternalId, AgentCustomerId = t.AgentCustomerId, SourceCountry = t.SourceCountry,
        DestinationCountry = t.DestinationCountry, Remarks = t.Remarks, DeviceId = t.DeviceId, CopResult = t.CopResult, ReversedAmount = t.ReversedAmount,
        IsReversal = t.IsReversal, Direction = direction
    };
}
