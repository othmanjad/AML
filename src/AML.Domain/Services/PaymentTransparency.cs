using AML.Domain.Entities;

namespace AML.Domain.Services;

/// <summary>FATF R.16 completeness checks driven by configuration (corridor, transaction type, threshold).</summary>
public static class PaymentTransparencyValidator
{
    public static readonly HashSet<string> MeaninglessValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "XXX", "XX", "X", "N/A", "NA", "N.A.", "NONE", "NULL", "UNKNOWN", "-", "--", ".", "TEST", "ABC", "123", "000", "0", "?", "NIL", "غير معروف"
    };

    public sealed record Outcome(bool Applicable, string Corridor, string? RuleCode, List<string> Missing, List<string> Meaningless, decimal? ComparedAmount)
    {
        public bool IsComplete => Missing.Count == 0 && Meaningless.Count == 0;
    }

    public static Outcome Check(Transaction t, IEnumerable<PaymentTransparencyRule> rules, Func<decimal, string, string, decimal?> convert, DateTime asOf)
    {
        var corridor = t.SourceCountry is not null && t.DestinationCountry is not null && !string.Equals(t.SourceCountry, t.DestinationCountry, StringComparison.OrdinalIgnoreCase)
            ? "CrossBorder" : "Domestic";
        var rule = rules.Where(r => r.Status == "Active" && r.Corridor == corridor && r.EffectiveFrom <= asOf && (r.EffectiveTo is null || asOf < r.EffectiveTo))
            .Where(r => string.IsNullOrWhiteSpace(r.TransactionTypes) || r.TransactionTypes.Split(',', StringSplitOptions.TrimEntries).Contains(t.TransactionType, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        if (rule is null) return new Outcome(false, corridor, null, new(), new(), null);

        var compared = convert(t.Amount, t.Currency, rule.ThresholdCurrency);
        var required = new List<string>(Split(rule.AlwaysRequiredFields));
        // When conversion is impossible the stricter requirement applies (fail safe).
        if (compared is null || compared >= rule.ThresholdAmount) required.AddRange(Split(rule.RequiredFields));
        var missing = new List<string>();
        var meaningless = new List<string>();
        foreach (var f in required.Distinct())
        {
            var v = Get(t, f);
            if (string.IsNullOrWhiteSpace(v)) missing.Add(f);
            else if (IsMeaningless(v)) meaningless.Add(f);
        }
        return new Outcome(true, corridor, rule.Code, missing, meaningless, compared);
    }

    public static bool IsMeaningless(string v)
    {
        var s = v.Trim();
        if (MeaninglessValues.Contains(s)) return true;
        if (s.Length >= 2 && s.All(c => c == s[0])) return true; // e.g. "AAAA", "0000"
        return false;
    }

    private static IEnumerable<string> Split(string csv) => csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static string? Get(Transaction t, string field) => field switch
    {
        "OriginatorName" => t.OriginatorName,
        "OriginatorAccount" => t.OriginatorAccount ?? t.SenderWalletExternalId,
        "OriginatorAddress" => t.OriginatorAddress,
        "OriginatorNationalId" => t.OriginatorNationalId,
        "OriginatorDateOfBirth" => t.OriginatorDateOfBirth?.ToString("yyyy-MM-dd"),
        "OriginatorPlaceOfBirth" => t.OriginatorPlaceOfBirth,
        "BeneficiaryName" => t.BeneficiaryName,
        "BeneficiaryAccount" => t.BeneficiaryAccount ?? t.ReceiverWalletExternalId,
        "IntermediaryInstitution" => t.IntermediaryInstitution,
        "OrderingInstitution" => t.OrderingInstitution,
        _ => null
    };
}
