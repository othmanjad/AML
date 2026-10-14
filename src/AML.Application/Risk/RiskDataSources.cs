using System.Text.Json;
using AML.Application.Common;
using AML.Application.Customers;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Risk;

public sealed record DataSourceInfo(string Code, string Type, string Description);

/// <summary>Injectable fault hooks used by resilience tests (no-op in production).</summary>
public interface IFaultInjector
{
    void Check(string point);
}

public sealed class NoFaultInjector : IFaultInjector
{
    public void Check(string point) { }
}

/// <summary>Typed data-source registry for risk factors. Dynamic customer fields are referenced as field:CODE (resolved by code and effective version).</summary>
public sealed class RiskDataSourceProvider
{
    public static readonly IReadOnlyList<DataSourceInfo> Static = new List<DataSourceInfo>
    {
        new("customer.type", "Text", "Customer type"),
        new("customer.nationality", "Country", "Nationality country code"),
        new("customer.residence", "Country", "Residence country code"),
        new("customer.incorporationCountry", "Country", "Incorporation country code"),
        new("customer.ageYears", "Number", "Age in years (individuals)"),
        new("customer.relationshipMonths", "Number", "Months since registration"),
        new("customer.isPep", "Boolean", "Customer flagged as PEP"),
        new("customer.region", "Text", "Region / data scope"),
        new("profile.declaredMonthlyIncome", "Number", "Declared monthly income (financial profile, freshness-aware)"),
        new("profile.sourceOfFunds", "Text", "Declared source of funds"),
        new("profile.expectedMonthlyVolume", "Number", "Expected monthly volume"),
        new("screening.pep", "Boolean", "PEP (customer flag or confirmed PEP screening result)"),
        new("screening.sanctionsConfirmed", "Boolean", "Confirmed sanctions / TF / PF match"),
        new("screening.adverseMedia", "Boolean", "Confirmed adverse media"),
        new("screening.status", "Text", "Current screening status (Unavailable is reported as unavailable data)"),
        new("ubo.pepOwner", "Boolean", "Any beneficial owner/controller is a PEP"),
        new("ubo.missing", "Boolean", "Beneficial ownership information missing"),
        new("ubo.depth", "Number", "Ownership chain depth"),
        new("kyc.gapCount", "Number", "Number of due-diligence gaps"),
        new("txn.volume90d", "Number", "Completed base-currency volume over 90 days"),
        new("txn.count90d", "Number", "Completed transaction count over 90 days"),
        new("txn.crossBorderRatio90d", "Number", "Cross-border share (%) over 90 days"),
        new("txn.reversalRate90d", "Number", "Reversal/refund rate (%) over 90 days"),
        new("txn.volumeVsExpected", "Number", "30-day volume / expected monthly volume"),
        new("agent.cashOutRatio90d", "Number", "Agent cash-out share (%) over 90 days"),
        new("agent.newCustomers30d", "Number", "Customers onboarded by the agent in 30 days"),
        new("entity.sharedDeviceLinks", "Number", "Entity-resolution links via shared devices"),
    };

    private readonly IAmlDbContext _db;
    private readonly CustomerService _customers;
    private readonly IFaultInjector _faults;

    public RiskDataSourceProvider(IAmlDbContext db, CustomerService customers, IFaultInjector faults)
    {
        _db = db; _customers = customers; _faults = faults;
    }

    public static bool IsKnown(string code, ICollection<string> publishedFieldCodes) =>
        Static.Any(s => s.Code == code) || code.StartsWith("field:") && publishedFieldCodes.Contains(code[6..]);

    public async Task<Dictionary<string, FactorInput>> GatherAsync(Customer c, IEnumerable<string> needed, DateTime asOf, CancellationToken ct)
    {
        _faults.Check("risk.gather");
        var result = new Dictionary<string, FactorInput>();
        var set = needed.ToHashSet();
        foreach (var code in set)
        {
            _faults.Check("risk.datasource:" + code);
            result[code] = await ResolveAsync(c, code, asOf, ct);
        }
        return result;
    }

    private async Task<FactorInput> ResolveAsync(Customer c, string code, DateTime asOf, CancellationToken ct)
    {
        if (code.StartsWith("field:"))
        {
            var fc = code[6..];
            var v = await _db.Set<CustomerFieldValue>().AsNoTracking()
                .Where(x => x.CustomerId == c.Id && x.FieldCode == fc && x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo > asOf))
                .OrderByDescending(x => x.EffectiveFrom).FirstOrDefaultAsync(ct);
            if (v is null) return FactorInput.Missing($"field:{fc}");
            object? value = v.ValueNumber is not null ? v.ValueNumber : v.ValueBool is not null ? v.ValueBool : v.ValueDate is not null ? v.ValueDate
                : v.ValueJson is not null ? JsonSerializer.Deserialize<List<string>>(v.ValueJson) : v.ValueText;
            if (v.ValueEncrypted is not null) return FactorInput.Of("(encrypted)", v.EffectiveFrom, $"field:{fc} v{v.FieldVersion}");
            return FactorInput.Of(value, v.EffectiveFrom, $"field:{fc} v{v.FieldVersion}");
        }
        switch (code)
        {
            case "customer.type": return FactorInput.Of(c.CustomerType);
            case "customer.nationality": return FactorInput.Of(c.NationalityCountry);
            case "customer.residence": return FactorInput.Of(c.ResidenceCountry);
            case "customer.incorporationCountry": return FactorInput.Of(c.IncorporationCountry);
            case "customer.region": return FactorInput.Of(c.Region);
            case "customer.isPep": return FactorInput.Of(c.IsPep);
            case "customer.ageYears":
                return c.DateOfBirth is { } dob ? FactorInput.Of((decimal)((asOf - dob.ToDateTime(TimeOnly.MinValue)).TotalDays / 365.25)) : FactorInput.Missing();
            case "customer.relationshipMonths":
                return c.RegistrationDate is { } rd ? FactorInput.Of((decimal)Math.Floor((asOf - rd.ToDateTime(TimeOnly.MinValue)).TotalDays / 30.44)) : FactorInput.Missing();
        }
        if (code.StartsWith("profile."))
        {
            var fp = await _db.Set<CustomerFinancialProfile>().AsNoTracking()
                .Where(p => p.CustomerId == c.Id && p.EffectiveFrom <= asOf && (p.EffectiveTo == null || p.EffectiveTo > asOf))
                .OrderByDescending(p => p.EffectiveFrom).FirstOrDefaultAsync(ct);
            if (fp is null) return FactorInput.Missing("financial profile");
            return code switch
            {
                "profile.declaredMonthlyIncome" => FactorInput.Of(fp.DeclaredMonthlyIncome, fp.DeclaredAt, $"financial profile #{fp.Id} declared {fp.DeclaredAt:yyyy-MM-dd}"),
                "profile.sourceOfFunds" => FactorInput.Of(fp.SourceOfFunds, fp.DeclaredAt, $"financial profile #{fp.Id}"),
                "profile.expectedMonthlyVolume" => FactorInput.Of(fp.ExpectedMonthlyVolume, fp.DeclaredAt, $"financial profile #{fp.Id}"),
                _ => FactorInput.Missing()
            };
        }
        if (code.StartsWith("screening."))
        {
            var confirmed = await _db.Set<ScreeningResult>().AsNoTracking()
                .Where(r => r.CustomerId == c.Id && r.SubjectType == "Customer" && r.Status == ScreeningStatuses.ConfirmedMatch)
                .Select(r => new { r.Category, r.SourceCode, r.ListEntryId }).ToListAsync(ct);
            switch (code)
            {
                case "screening.pep":
                    var pep = c.IsPep || confirmed.Any(r => r.Category == "PEP");
                    return FactorInput.Of(pep, detail: pep ? "PEP status recorded" : null);
                case "screening.sanctionsConfirmed":
                    var s = confirmed.Where(r => r.Category is "Sanctions" or "Terrorism" or "ProliferationFinancing").ToList();
                    return FactorInput.Of(s.Count > 0, detail: s.Count > 0 ? "Confirmed: " + string.Join(", ", s.Select(x => $"{x.SourceCode}/{x.ListEntryId}")) : null);
                case "screening.adverseMedia":
                    return FactorInput.Of(confirmed.Any(r => r.Category == "AdverseMedia"));
                case "screening.status":
                    return c.ScreeningStatus is "Unavailable" or "Pending" or "NotScreened"
                        ? FactorInput.Unavailable($"screening status {c.ScreeningStatus}")
                        : FactorInput.Of(c.ScreeningStatus);
            }
        }
        if (code.StartsWith("ubo."))
        {
            if (c.CustomerType == CustomerTypes.Individual) return FactorInput.Of(false, detail: "individual");
            var (analysis, _, threshold, _) = await _customers.AnalyzeOwnershipAsync(c, ct);
            var bos = analysis.Owners.Where(o => o.IsBeneficialOwner).ToList();
            switch (code)
            {
                case "ubo.pepOwner":
                    if (bos.Count == 0) return FactorInput.Missing("no beneficial owners recorded");
                    var peps = bos.Where(o => o.IsPep).ToList();
                    return FactorInput.Of(peps.Count > 0, detail: peps.Count > 0 ? "PEP beneficial owner(s): " + string.Join(", ", peps.Select(p => $"{p.OwnerName} ({p.Basis})")) : $"{bos.Count} beneficial owner(s) screened, none PEP (threshold {threshold}%)");
                case "ubo.missing":
                    return FactorInput.Of(bos.Count == 0, detail: bos.Count == 0 ? "No beneficial owner identified" : null);
                case "ubo.depth":
                    return bos.Count == 0 ? FactorInput.Missing("no ownership structure") : FactorInput.Of((decimal)analysis.MaxDepth);
            }
        }
        if (code == "kyc.gapCount") return FactorInput.Of((decimal)(await _customers.KycGapsAsync(c, ct)).Count);
        if (code.StartsWith("txn.") || code.StartsWith("agent.") || code.StartsWith("entity."))
            return await BehaviourAsync(c, code, asOf, ct);
        return FactorInput.Unavailable($"unknown data source {code}");
    }

    private async Task<FactorInput> BehaviourAsync(Customer c, string code, DateTime asOf, CancellationToken ct)
    {
        var from90 = asOf.AddDays(-90);
        var txns = _db.Set<Transaction>().AsNoTracking().Where(t => !t.IsReversal && t.TimestampUtc >= from90 && t.TimestampUtc <= asOf);
        switch (code)
        {
            case "txn.volume90d":
            {
                var mine = txns.Where(t => (t.SenderCustomerId == c.Id || t.ReceiverCustomerId == c.Id) && t.Status != TxnStatuses.Failed && t.Status != TxnStatuses.Pending);
                return FactorInput.Of(await mine.SumAsync(t => t.BaseAmount ?? 0, ct), asOf, "transactions 90d");
            }
            case "txn.count90d":
                return FactorInput.Of((decimal)await txns.CountAsync(t => (t.SenderCustomerId == c.Id || t.ReceiverCustomerId == c.Id) && t.Status != TxnStatuses.Failed, ct));
            case "txn.crossBorderRatio90d":
            {
                var mine = await txns.Where(t => t.SenderCustomerId == c.Id || t.ReceiverCustomerId == c.Id).Select(t => new { t.SourceCountry, t.DestinationCountry }).ToListAsync(ct);
                if (mine.Count == 0) return FactorInput.Of(0m, detail: "no activity");
                return FactorInput.Of(Math.Round(100m * mine.Count(t => t.SourceCountry != null && t.DestinationCountry != null && t.SourceCountry != t.DestinationCountry) / mine.Count, 2));
            }
            case "txn.reversalRate90d":
            {
                var mine = await txns.Where(t => t.SenderCustomerId == c.Id || t.ReceiverCustomerId == c.Id).Select(t => t.Status).ToListAsync(ct);
                if (mine.Count == 0) return FactorInput.Of(0m, detail: "no activity");
                return FactorInput.Of(Math.Round(100m * mine.Count(s => s is TxnStatuses.Reversed or TxnStatuses.PartiallyReversed) / mine.Count, 2));
            }
            case "txn.volumeVsExpected":
            {
                var fp = await _db.Set<CustomerFinancialProfile>().AsNoTracking().Where(p => p.CustomerId == c.Id && p.EffectiveTo == null).FirstOrDefaultAsync(ct);
                if (fp?.ExpectedMonthlyVolume is not > 0) return FactorInput.Missing("expected monthly volume not declared");
                var from30 = asOf.AddDays(-30);
                var vol = await _db.Set<Transaction>().AsNoTracking().Where(t => !t.IsReversal && t.TimestampUtc >= from30 && t.TimestampUtc <= asOf && t.Status == TxnStatuses.Completed &&
                    (t.SenderCustomerId == c.Id || t.ReceiverCustomerId == c.Id)).SumAsync(t => t.BaseAmount ?? 0, ct);
                return FactorInput.Of(Math.Round(vol / fp.ExpectedMonthlyVolume.Value, 4));
            }
            case "agent.cashOutRatio90d":
            {
                var mine = await txns.Where(t => t.AgentCustomerId == c.Id && t.Status == TxnStatuses.Completed).Select(t => new { t.TransactionType, t.BaseAmount }).ToListAsync(ct);
                var total = mine.Sum(t => t.BaseAmount ?? 0);
                if (total == 0) return FactorInput.Of(0m, detail: "no agent activity");
                return FactorInput.Of(Math.Round(100m * mine.Where(t => t.TransactionType == "CashOut").Sum(t => t.BaseAmount ?? 0) / total, 2));
            }
            case "agent.newCustomers30d":
            {
                var from = DateOnly.FromDateTime(asOf.AddDays(-30));
                return FactorInput.Of((decimal)await _db.Set<Customer>().CountAsync(x => x.OnboardingAgentId == c.Id && x.RegistrationDate >= from, ct));
            }
            case "entity.sharedDeviceLinks":
                return FactorInput.Of((decimal)await _db.Set<EntityLink>().CountAsync(l => (l.CustomerAId == c.Id || l.CustomerBId == c.Id) && l.LinkType == "SharedDevice", ct));
        }
        return FactorInput.Unavailable($"unknown data source {code}");
    }
}
