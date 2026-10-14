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

    public IReadOnlyList<RuleTxn> GetNetworkEdges(DateTime fromUtc, DateTime toUtc)
    {
        if (_edges.TryGetValue((fromUtc, toUtc), out var e)) return e;
        e = _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == _tenantId && t.TimestampUtc > fromUtc && t.TimestampUtc <= toUtc && !t.IsReversal && t.Status == TxnStatuses.Completed &&
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
