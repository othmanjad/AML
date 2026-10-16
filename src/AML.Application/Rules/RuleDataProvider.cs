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
                case "customer.householdId": v = c.HouseholdId; break;
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

    private readonly Dictionary<(long?, string?), Dictionary<DateOnly, decimal>> _balances = new();

    /// <summary>Balances are loaded once per customer / wallet (whole history) and filtered in memory.</summary>
    public IReadOnlyDictionary<DateOnly, decimal> GetBalances(long? customerId, string? walletExternalId, DateOnly from, DateOnly to)
    {
        var key = (walletExternalId is null ? customerId : null, walletExternalId);
        if (!_balances.TryGetValue(key, out var all))
        {
            var q = _db.Set<WalletBalanceSnapshot>().IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == _tenantId && x.BaseBalance != null);
            q = walletExternalId is not null ? q.Where(x => x.ExternalWalletId == walletExternalId) : q.Where(x => x.CustomerId == customerId);
            all = q.GroupBy(x => x.BusinessDate).Select(g => new { g.Key, Total = g.Sum(x => x.BaseBalance!.Value) }).ToList().ToDictionary(x => x.Key, x => x.Total);
            _balances[key] = all;
        }
        return all.Where(kv => kv.Key >= from && kv.Key <= to).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private readonly Dictionary<string, IReadOnlySet<string>> _lists = new(StringComparer.Ordinal);

    public IReadOnlySet<string> GetLookupCodes(string reference)
    {
        if (_lists.TryGetValue(reference, out var l)) return l;
        var parts = reference.Split(':', 2);
        var category = parts[0];
        var q = _db.Set<LookupValue>().AsNoTracking().Where(x => x.Category == category && x.IsActive);
        if (parts.Length == 2) { var attr = parts[1]; q = q.Where(x => x.Attribute == attr); }
        return _lists[reference] = q.Select(x => x.Code).ToList().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, string?>? _countryRisk;

    public string? GetCountryRisk(string? country)
    {
        if (country is null) return null;
        _countryRisk ??= _db.Set<LookupValue>().AsNoTracking().Where(x => x.Category == "COUNTRY" && x.IsActive).Select(x => new { x.Code, x.Attribute }).ToList()
            .ToDictionary(x => x.Code, x => x.Attribute, StringComparer.OrdinalIgnoreCase);
        return _countryRisk.TryGetValue(country, out var a) ? a ?? "Standard" : null;
    }

    private readonly Dictionary<string, Dictionary<long, string?>> _attrColumns = new(StringComparer.Ordinal);

    /// <summary>Current value of a peer attribute for every customer of the tenant (loaded once per attribute).</summary>
    private Dictionary<long, string?> AttributeColumn(string attribute)
    {
        if (_attrColumns.TryGetValue(attribute, out var col)) return col;
        var customers = _db.Set<Customer>().IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == _tenantId && !c.IsAnonymised);
        col = attribute switch
        {
            "customer.type" => customers.Select(c => new { c.Id, V = (string?)c.CustomerType }).ToDictionary(x => x.Id, x => x.V),
            "customer.region" => customers.Select(c => new { c.Id, V = c.Region }).ToDictionary(x => x.Id, x => x.V),
            "customer.residence" => customers.Select(c => new { c.Id, V = c.ResidenceCountry }).ToDictionary(x => x.Id, x => x.V),
            "customer.nationality" => customers.Select(c => new { c.Id, V = c.NationalityCountry }).ToDictionary(x => x.Id, x => x.V),
            "customer.riskLevel" => (from c in customers
                                     join p in _db.Set<CurrentCustomerRiskProfile>().IgnoreQueryFilters().AsNoTracking() on c.Id equals p.CustomerId into g
                                     from p in g.DefaultIfEmpty()
                                     select new { c.Id, V = p == null ? null : p.Level }).ToDictionary(x => x.Id, x => x.V),
            _ when attribute.StartsWith("field:", StringComparison.Ordinal) => FieldColumn(attribute[6..], customers),
            _ => new Dictionary<long, string?>()
        };
        return _attrColumns[attribute] = col;
    }

    private Dictionary<long, string?> FieldColumn(string code, IQueryable<Customer> customers)
    {
        var ids = customers.Select(c => c.Id).ToList();
        var rows = _db.Set<CustomerFieldValue>().IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == _tenantId && x.FieldCode == code && x.EffectiveTo == null)
            .Select(x => new { x.CustomerId, x.ValueText, x.ValueNumber, x.ValueBool, x.ValueJson }).ToList();
        var map = ids.ToDictionary(i => i, _ => (string?)null);
        foreach (var r in rows)
            map[r.CustomerId] = r.ValueText ?? r.ValueJson ?? (r.ValueNumber is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : r.ValueBool?.ToString());
        return map;
    }

    /// <summary>Peers share the subject's current value of every attribute; a subject missing any attribute has no peer group.</summary>
    public (string Signature, IReadOnlyList<long> Members)? GetPeerGroup(long customerId, IReadOnlyList<string> peerBy)
    {
        var cols = peerBy.Select(a => (Attr: a, Col: AttributeColumn(a))).ToList();
        var values = new List<string>();
        foreach (var (a, col) in cols)
        {
            if (!col.TryGetValue(customerId, out var v) || string.IsNullOrEmpty(v)) return null;
            values.Add($"{a}={v}");
        }
        var members = cols[0].Col.Keys.Where(id => cols.All(c => c.Col.TryGetValue(id, out var v) && string.Equals(v, c.Col[customerId], StringComparison.OrdinalIgnoreCase))).ToList();
        return (string.Join(";", values), members);
    }

    private readonly Dictionary<(string, DateTime, DateTime), List<(long, RuleTxn)>> _customerTxns = new();

    public IReadOnlyList<(long CustomerId, RuleTxn Txn)> GetCustomerTransactions(IReadOnlyCollection<long> customerIds, DateTime fromUtc, DateTime toUtc)
    {
        var key = (string.Join(",", customerIds.OrderBy(x => x)), fromUtc, toUtc);
        if (_customerTxns.TryGetValue(key, out var cached)) return cached;
        var result = new List<(long, RuleTxn)>();
        foreach (var chunk in customerIds.Chunk(1000))
        {
            var ids = chunk.ToList();
            var rows = _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.TenantId == _tenantId && t.TimestampUtc > fromUtc && t.TimestampUtc <= toUtc && t.EconomicLegOfId == null &&
                            ((t.SenderCustomerId != null && ids.Contains(t.SenderCustomerId.Value)) || (t.ReceiverCustomerId != null && ids.Contains(t.ReceiverCustomerId.Value))))
                .ToList();
            var set = ids.ToHashSet();
            foreach (var t in rows)
            {
                if (t.SenderCustomerId is { } s && set.Contains(s)) result.Add((s, Project(t, "out")));
                if (t.ReceiverCustomerId is { } r && r != t.SenderCustomerId && set.Contains(r)) result.Add((r, Project(t, "in")));
            }
        }
        return _customerTxns[key] = result;
    }

    private Dictionary<string, List<IndustryBenchmark>>? _benchmarks;

    public RuleBenchmark? GetBenchmark(string metric, long customerId, DateTime asOfUtc)
    {
        _benchmarks ??= _db.Set<IndustryBenchmark>().IgnoreQueryFilters().AsNoTracking().Where(b => b.TenantId == _tenantId && b.IsActive).ToList()
            .GroupBy(b => b.Metric, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (!_benchmarks.TryGetValue(metric, out var rows)) return null;
        foreach (var b in rows.Where(b => b.EffectiveFrom <= asOfUtc && (b.EffectiveTo == null || b.EffectiveTo > asOfUtc)).OrderByDescending(b => b.EffectiveFrom))
        {
            var v = GetAttribute(customerId, b.SegmentField, asOfUtc);
            var text = v is IEnumerable<string> l ? l.ToList() : v is null ? new List<string>() : new List<string> { Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
            if (text.Any(x => string.Equals(x, b.SegmentValue, StringComparison.OrdinalIgnoreCase)))
                return new RuleBenchmark(b.Metric, b.SegmentField, b.SegmentValue, b.Value, b.Source);
        }
        return null;
    }

    public static RuleTxn Project(Transaction t, string direction) => new()
    {
        Id = t.Id, SourceTransactionId = t.SourceTransactionId, TimestampUtc = t.TimestampUtc, Amount = t.Amount, Currency = t.Currency, BaseAmount = t.BaseAmount,
        BaseCurrency = t.BaseCurrency, FxStatus = t.FxStatus, FxRate = t.FxRate, FxRateDate = t.FxRateDate, FxRateVersion = t.FxRateVersion, Type = t.TransactionType,
        Status = t.Status, Channel = t.Channel, Fee = t.Fee, SenderCustomerId = t.SenderCustomerId, ReceiverCustomerId = t.ReceiverCustomerId,
        SenderWallet = t.SenderWalletExternalId, ReceiverWallet = t.ReceiverWalletExternalId, AgentCustomerId = t.AgentCustomerId, SourceCountry = t.SourceCountry,
        DestinationCountry = t.DestinationCountry, Remarks = t.Remarks, DeviceId = t.DeviceId, CopResult = t.CopResult, ReversedAmount = t.ReversedAmount,
        InvoiceAmount = t.InvoiceAmount, InvoiceCurrency = t.InvoiceCurrency, GoodsCategory = t.GoodsCategory,
        Commission = t.Commission, SubType = t.SubType, Mcc = t.Mcc,
        BalanceBefore = direction == "in" ? t.ReceiverBalanceBefore : t.SenderBalanceBefore,
        BalanceAfter = direction == "in" ? t.ReceiverBalanceAfter : t.SenderBalanceAfter,
        IsReversal = t.IsReversal, Direction = direction
    };
}
