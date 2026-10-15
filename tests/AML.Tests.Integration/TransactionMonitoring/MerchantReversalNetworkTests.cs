using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.TransactionMonitoring;

[Collection("tm")]
public class MerchantReversalTests : TmBase
{
    public MerchantReversalTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private static readonly string[] Subject = { "rule", "version", "subject" };
    private static readonly string[] PerWindow = { "rule", "version", "subject", "window" };

    [Fact]
    public async Task MER_01_merchant_volume_vs_profile()
    {
        var rule = await Rule(new { scope = new { customerTypes = new[] { "Merchant" } }, dedupKey = PerWindow, window = new { type = "Calendar", unit = "Month" }, condition = new { aggregate = "profileRatio", profileField = "profile.expectedMonthlyVolume", direction = "in", @operator = "gt", value = 3 } });
        var profile = new { expectedMonthlyVolume = 2000, expectedMonthlyCount = 100, currency = "JOD", sourceOfFunds = "Sales", declaredMonthlyIncome = 2000 };
        var m1 = await Cust("Merchant", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "71000001" }, financialProfile: profile);
        var m2 = await Cust("Merchant", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "71000002" }, financialProfile: profile);
        await Backdate(m1.Id, m2.Id);
        await Send(Enumerable.Range(0, 30).Select(i => T($"W-SHOPPER{i}", m1.W, 300, At(2025, 9, 1, "10:00:00").AddDays(i), "MerchantPayment", channel: "POS"))
            .Concat(Enumerable.Range(0, 25).Select(i => T($"W-SHOPPER{i}", m2.W, 100, At(2025, 9, 1, "10:00:00").AddDays(i), "MerchantPayment", channel: "POS"))));
        await Run(rule, At(2025, 9, 1, "00:00:00"), At(2025, 10, 1, "00:00:00"));
        Result("TM-MER-01-01", (await Alerts(rule, m1.Id)).Count == 1, $"Grocery with expected 2,000/month processes 9,000 (450%) → {(await Alerts(rule, m1.Id)).Count} alert. Note: expected volume comes from the merchant's own financial profile (no MCC/business-size benchmark table).");
        Result("TM-MER-01-02", (await Alerts(rule, m2.Id)).Count == 0, $"Volume 2,500 (125%) → {(await Alerts(rule, m2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task MER_02_merchant_split_payments()
    {
        var mp = F_("txn.type", "eq", "MerchantPayment");
        var rule = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", minutes = 10 }, condition = And(Cnt(mp, "gte", 3, "out"), new { aggregate = "distinctCounterparties", filter = mp, direction = "out", @operator = "eq", value = 1 }) });
        var c1 = await Cust(); var c2 = await Cust();
        await Send(Enumerable.Range(0, 3).Select(i => T(c1.W, "W-MERCHANT-77", 330, At(2025, 9, 3, "12:00:00").AddMinutes(i * 2), "MerchantPayment", channel: "POS"))
            .Append(T(c2.W, "W-MERCHANT-77", 330, At(2025, 9, 3, "12:00:00"), "MerchantPayment", channel: "POS")));
        await Run(rule, At(2025, 9, 3, "00:00:00"), At(2025, 9, 4, "00:00:00"));
        Result("TM-MER-02-01", (await Alerts(rule, c1.Id)).Count == 1, $"3 × 330 to the same merchant in 4 minutes → {(await Alerts(rule, c1.Id)).Count} alert. Note: 'same merchant' configured as count ≥ 3 AND exactly one distinct merchant in the 10-minute window; the merchant limit (350) is not referenced.");
        Result("TM-MER-02-02", (await Alerts(rule, c2.Id)).Count == 0, $"One payment → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task REV_01_high_reversal_rate()
    {
        var reversed = F_("txn.status", "in", new[] { "Reversed", "PartiallyReversed" });
        var rule = await Rule(new
        {
            reversalHandling = "Include", dedupKey = Subject, window = new { type = "Rolling", days = 30 },
            condition = And(new { aggregate = "ratio", direction = "out", numerator = reversed, measure = "count", @operator = "gte", value = 20 }, Cnt(reversed, "gte", 5, "out"))
        });
        async Task<(long Id, string W)> Pattern(int total, int rev, int day)
        {
            var c = await Cust();
            var ids = Enumerable.Range(0, total).Select(i => $"REV1-{c.Id}-{i}").ToList();
            await Send(ids.Select((id, i) => T(c.W, "W-SHOP-REV", 40, At(2025, 9, day, "09:00:00").AddHours(i), "MerchantPayment", channel: "POS", id: id)));
            await Send(ids.Take(rev).Select((id, i) => T("W-SHOP-REV", c.W, 40, At(2025, 9, day + 2, "09:00:00").AddMinutes(i), "Reversal", channel: "POS", original: id, reversal: true)));
            return c;
        }
        var c1 = await Pattern(25, 7, 5); var c2 = await Pattern(15, 4, 5); var c3 = await Pattern(25, 1, 5);
        await Run(rule, At(2025, 9, 5, "00:00:00"), At(2025, 9, 12, "00:00:00"));
        Result("TM-REV-01-01", (await Alerts(rule, c1.Id)).Count == 1, $"25 transactions, 7 reversed (28%) → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-REV-01-02", (await Alerts(rule, c2.Id)).Count == 0, $"4 reversals of 15 (26.7%, count below 5) → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-REV-01-03", (await Alerts(rule, c3.Id)).Count == 0, $"1 reversal of 25 → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task REV_04_many_failed_transactions()
    {
        var rule = await Rule(new { eligibleStatuses = new[] { "Failed" }, window = new { type = "Calendar", unit = "Day" }, condition = Cnt(null, "gte", 10, "out") }, "Low");
        var c1 = await Cust(); var c2 = await Cust();
        await Send(Enumerable.Range(0, 12).Select(i => T(c1.W, "W-FAILX", 60, At(2025, 9, 9, "09:00:00").AddMinutes(i * 20), status: "FAIL"))
            .Concat(Enumerable.Range(0, 3).Select(i => T(c2.W, "W-FAILX", 60, At(2025, 9, 9, "09:00:00").AddMinutes(i * 20), status: "FAIL"))));
        await Run(rule, At(2025, 9, 9, "00:00:00"), At(2025, 9, 10, "00:00:00"));
        Result("TM-REV-04-01", (await Alerts(rule, c1.Id)).Count == 1, $"12 failed transfers in one day (rule counts status Failed) → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-REV-04-02", (await Alerts(rule, c2.Id)).Count == 0, $"3 failures → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }
}

[Collection("tm")]
public class NetworkMuleTests : TmBase
{
    public NetworkMuleTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact]
    public async Task CIR_01_round_tripping()
    {
        var rule = await Rule(new { dedupKey = new[] { "rule", "version", "subject" }, window = new { type = "Rolling", days = 7 }, condition = new { aggregate = "cycle", maxDepth = 3, @operator = "gte", value = 1 } }, "High");
        var a1 = await Cust(); var b1 = await Cust(); var c1 = await Cust();
        var a2 = await Cust(); var b2 = await Cust(); var c2 = await Cust();
        var a3 = await Cust(); var b3 = await Cust(); var c3 = await Cust();
        await Send(new[]
        {
            T(a1.W, b1.W, 1000, At(2025, 9, 15, "10:00:00")), T(b1.W, c1.W, 950, At(2025, 9, 16, "10:00:00")), T(c1.W, a1.W, 900, At(2025, 9, 17, "10:00:00")),
            T(a2.W, b2.W, 1000, At(2025, 9, 15, "10:00:00")), T(b2.W, c2.W, 950, At(2025, 9, 16, "10:00:00")),
            T(a3.W, b3.W, 1000, At(2025, 9, 15, "10:00:00")), T(b3.W, c3.W, 950, At(2025, 9, 18, "10:00:00")), T(c3.W, a3.W, 900, At(2025, 9, 23, "11:00:00"))
        });
        await Run(rule, At(2025, 9, 14, "00:00:00"), At(2025, 9, 25, "00:00:00"));
        var x1 = await Alerts(rule, a1.Id);
        Result("TM-CIR-01-01", x1.Count == 1 && x1[0].EvidenceJson!.Contains("cycle.path"),
            $"A→B 1,000; B→C 950; C→A 900 within 3 days → {x1.Count} alert on A with the cycle path in evidence. Note: the 80% return-amount condition is not configurable for cycle detection.");
        Result("TM-CIR-01-02", (await Alerts(rule, a2.Id)).Count == 0, $"A→B, B→C only → {(await Alerts(rule, a2.Id)).Count} alerts.");
        Result("TM-CIR-01-03", (await Alerts(rule, a3.Id)).Count == 0, $"Funds return to A after 8 days → {(await Alerts(rule, a3.Id)).Count} alerts for A.");
        Done();
    }

    [Fact]
    public async Task MUL_01_mule_inflows_then_cash_out()
    {
        var rule = await Rule(new
        {
            dedupKey = new[] { "rule", "version", "subject" }, window = new { type = "Rolling", hours = 24 },
            condition = And(new { aggregate = "distinctCounterparties", direction = "in", @operator = "gte", value = 5 }, new { aggregate = "rapidMovement", maxMinutes = 1440, @operator = "gte", value = 80 })
        }, "High");
        var c1 = await Cust(); var c2 = await Cust();
        var t = new List<object>();
        foreach (var c in new[] { c1, c2 }) t.AddRange(Enumerable.Range(0, 7).Select(i => T($"W-STRANGER{i}", c.W, 300, At(2025, 9, 20, "08:00:00").AddMinutes(i * 15))));
        t.Add(T(c1.W, "W-AGENT-MUL", 1900, At(2025, 9, 20, "13:00:00"), "CashOut", channel: "Agent"));
        t.Add(T(c2.W, "W-AGENT-MUL", 1900, At(2025, 10, 20, "13:00:00"), "CashOut", channel: "Agent"));
        await Send(t);
        await Run(rule, At(2025, 9, 20, "00:00:00"), At(2025, 10, 21, "00:00:00"));
        Result("TM-MUL-01-01", (await Alerts(rule, c1.Id)).Count == 1, $"7 senders × 300, 1,900 cashed out within 5 h → {(await Alerts(rule, c1.Id)).Count} alert. Note: '≥80% cashed out' is measured as outflow within 24 h relative to an inflow; 'unrelated' senders and the cash-out type of the outflow are not checked.");
        Result("TM-MUL-01-02", (await Alerts(rule, c2.Id)).Count == 0, $"7 senders, funds kept for a month → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }
}

[Collection("tm")]
public class CrossBorderPrepaidTests : TmBase
{
    public CrossBorderPrepaidTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private static readonly string[] Subject = { "rule", "version", "subject" };
    private static readonly string[] PerWindow = { "rule", "version", "subject", "window" };
    private static readonly object XB = F_("txn.isCrossBorder", "eq", true);

    [Fact]
    public async Task XBR_01_many_foreign_beneficiaries()
    {
        var rule = await Rule(new { dedupKey = PerWindow, window = new { type = "Calendar", unit = "Month" }, condition = new { aggregate = "distinctCounterparties", direction = "out", filter = XB, @operator = "gte", value = 5 } });
        var c1 = await Cust(); var c2 = await Cust();
        var countries = new[] { "EG", "PH", "IN" };
        await Send(Enumerable.Range(0, 7).Select(i => T(c1.W, $"W-ABR-{i}", 150, At(2025, 10, 2, "10:00:00").AddDays(i), "Remittance", dst: countries[i % 3]))
            .Concat(Enumerable.Range(0, 3).Select(i => T(c2.W, "W-ABR-FAMILY", 150, At(2025, 8, 2, "10:00:00").AddMonths(i), "Remittance", dst: "EG"))));
        await Run(rule, At(2025, 8, 1, "00:00:00"), At(2025, 11, 1, "00:00:00"));
        Result("TM-XBR-01-01", (await Alerts(rule, c1.Id)).Count == 1, $"Remits to 7 people in 3 countries in October → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-XBR-01-02", (await Alerts(rule, c2.Id)).Count == 0, $"Monthly to 1 family member → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task XBR_02_payment_transparency()
    {
        var c = await Cust(); var w = c.W;
        string Id_() => "XBR2-" + Guid.NewGuid().ToString("N")[..10];
        var (t1, t2, t3) = (Id_(), Id_(), Id_());
        var when = DateTime.UtcNow.AddMinutes(-5);
        await Send(new[]
        {
            T(w, "W-XB-1", 1200, when, "Remittance", currency: "USD", dst: "AE", id: t1, originator: new { name = "Tm Sender", dateOfBirth = "1990-01-01" }, beneficiary: new { name = "Tm Receiver", account = "AE-TEST-1" }),
            T(w, "W-XB-2", 1200, when, "Remittance", currency: "USD", dst: "AE", id: t2, originator: new { name = "Tm Sender", address = "Amman", dateOfBirth = "1990-01-01" }, beneficiary: new { name = "Tm Receiver", account = "AE-TEST-2" }),
            T(w, "W-XB-3", 1200, when, "Remittance", currency: "USD", dst: "AE", id: t3, originator: new { name = "Tm Sender", address = "Amman", dateOfBirth = "1990-01-01" }, beneficiary: new { name = "XXX", account = "AE-TEST-3" })
        });
        async Task<(string Status, string? Issues, int Alerts)> State(string t) => await F.WithDb(async db =>
        {
            var x = await db.Set<Transaction>().FirstAsync(y => y.SourceTransactionId == t);
            return (x.PaymentTransparencyStatus ?? "", x.PaymentTransparencyIssues, await db.Set<AmlAlert>().CountAsync(a => a.DeduplicationKey == "R16:CORE:" + t));
        });
        var s1 = await State(t1); var s2 = await State(t2); var s3 = await State(t3);
        Result("TM-XBR-02-01", s1.Status == "Incomplete" && (s1.Issues ?? "").Contains("OriginatorAddress") && s1.Alerts == 1, $"1,200 USD remittance without originator address → status {s1.Status} ({s1.Issues}), {s1.Alerts} completeness alert.");
        Result("TM-XBR-02-02", s2.Status != "Incomplete" && s2.Alerts == 0, $"All required fields present → status {s2.Status}, {s2.Alerts} alerts.");
        Result("TM-XBR-02-03", s3.Status == "Incomplete" || s3.Alerts > 0,
            s3.Status == "Incomplete" ? $"Beneficiary name 'XXX' treated as missing → {s3.Status} ({s3.Issues}), {s3.Alerts} alert." :
            $"Beneficiary name 'XXX' → status {s3.Status}, {s3.Alerts} alerts. DEFECT: placeholder values (e.g. 'XXX') are accepted as present; no meaningful-content validation in the payment-transparency check.");
        Done();
    }

    [Fact]
    public async Task XBR_04_inbound_then_domestic_dispersal()
    {
        var rule = await Rule(new
        {
            dedupKey = Subject, window = new { type = "Rolling", hours = 48 },
            condition = And(Sum(XB, "gte", 2000, "in"), Cnt(F_("txn.isCrossBorder", "eq", false), "gte", 5, "out"))
        }, "High");
        var c1 = await Cust(); var c2 = await Cust();
        await Send(new[] { c1, c2 }.Select(c => T("W-FOREIGN-REMITTER", c.W, 5000, At(2025, 10, 5, "10:00:00"), "Remittance", src: "AE", dst: "JO"))
            .Concat(Enumerable.Range(0, 8).Select(i => T(c1.W, $"W-LOCAL-{i}", 600, At(2025, 10, 6, "10:00:00").AddMinutes(i * 30)))));
        await Run(rule, At(2025, 10, 5, "00:00:00"), At(2025, 10, 8, "00:00:00"));
        Result("TM-XBR-04-01", (await Alerts(rule, c1.Id)).Count == 1, $"Receives 5,000 from abroad, sends to 8 local wallets next day → {(await Alerts(rule, c1.Id)).Count} alert. Note: the order inbound-then-outbound is not enforced (both legs within 48 h).");
        Result("TM-XBR-04-02", (await Alerts(rule, c2.Id)).Count == 0, $"Funds kept in wallet → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task XBR_05_just_below_threshold()
    {
        var band = And(XB, F_("txn.currency", "eq", "USD"), F_("txn.amount", "between", new[] { 900m, 999.99m }));
        var rule = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 14 }, condition = Cnt(band, "gte", 3, "out") }, "High");
        var c1 = await Cust(); var c2 = await Cust();
        await Send(Enumerable.Range(0, 3).Select(i => T(c1.W, $"W-USD-{i}", 950, At(2025, 10, 10, "10:00:00").AddDays(i * 4), "Remittance", currency: "USD", dst: "AE"))
            .Append(T(c2.W, "W-USD-X", 950, At(2025, 10, 10, "10:00:00"), "Remittance", currency: "USD", dst: "AE")));
        await Run(rule, At(2025, 10, 10, "00:00:00"), At(2025, 10, 25, "00:00:00"));
        Result("TM-XBR-05-01", (await Alerts(rule, c1.Id)).Count == 1, $"Three 950 USD remittances in 9 days → {(await Alerts(rule, c1.Id)).Count} alert. Note: band configured on the original-currency amount (900–999.99 USD); other currencies need their own band.");
        Result("TM-XBR-05-02", (await Alerts(rule, c2.Id)).Count == 0, $"Single 950 USD remittance → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task PRP_01_to_04_prepaid_and_bills()
    {
        var topup = F_("txn.type", "eq", "TopUp");
        var r1 = await Rule(new { dedupKey = PerWindow, window = new { type = "Calendar", unit = "Month" }, condition = Or(Sum(topup, "gte", 500, "out"), new { aggregate = "distinctCounterparties", filter = topup, direction = "out", @operator = "gte", value = 10 }) }, "Low");
        var r2 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 7 }, condition = Sum(F_("txn.type", "in", new[] { "Voucher", "GiftCard" }), "gte", 1000, "out") });
        var r3 = await Rule(new { dedupKey = PerWindow, window = new { type = "Calendar", unit = "Month" }, condition = new { aggregate = "distinctCounterparties", filter = F_("txn.type", "eq", "BillPayment"), direction = "out", @operator = "gte", value = 10 } }, "Low");
        var r4 = await Rule(new
        {
            dedupKey = Subject, window = new { type = "Rolling", hours = 12 },
            condition = And(new { aggregate = "rapidMovement", filter = And(F_("txn.type", "eq", "CardLoad"), F_("txn.amount", "gte", 500)), maxMinutes = 720, @operator = "gt", value = 0 },
                Cnt(And(F_("txn.channel", "eq", "ATM"), F_("txn.sourceCountry", "neq", "JO")), "gte", 1, "out"))
        }, "High");
        var p1 = await Cust(); var p2 = await Cust(); var v1 = await Cust(); var v2 = await Cust(); var b1 = await Cust(); var b2 = await Cust(); var k1 = await Cust(); var k2 = await Cust();
        var d = At(2025, 10, 13, "10:00:00");
        await Send(Enumerable.Range(0, 15).Select(i => T(p1.W, $"MSISDN-0790000{i:000}", i < 10 ? 50 : 40, d.AddHours(i * 5), "TopUp"))
            .Append(T(p2.W, "MSISDN-OWN", 20, d, "TopUp"))
            .Concat(Enumerable.Range(0, 12).Select(i => T(v1.W, "VOUCHER-SHOP", 100, d.AddHours(i * 5), "Voucher")))
            .Append(T(v2.W, "VOUCHER-SHOP", 50, d, "Voucher"))
            .Concat(Enumerable.Range(0, 14).Select(i => T(b1.W, $"ELEC-ACC-{i:000}", 30, d.AddHours(i * 6), "BillPayment")))
            .Concat(Enumerable.Range(0, 3).Select(i => T(b2.W, $"OWN-BILL-{i}", 30, d.AddHours(i * 6), "BillPayment")))
            .Append(T("W-CARD-PROG", k1.W, 1000, d, "CardLoad", channel: "Card")).Append(T(k1.W, "ATM-IST-01", 950, d.AddHours(4), "Withdrawal", channel: "ATM", src: "TR", dst: "TR"))
            .Append(T("W-CARD-PROG", k2.W, 1000, d, "CardLoad", channel: "Card")).Concat(Enumerable.Range(0, 3).Select(i => T(k2.W, $"POS-AMM-{i}", 60, d.AddHours(2 + i), "MerchantPayment", channel: "POS"))));
        foreach (var r in new[] { r1, r2, r3, r4 }) await Run(r, At(2025, 10, 1, "00:00:00"), At(2025, 11, 1, "00:00:00"));
        Result("TM-PRP-01-01", (await Alerts(r1, p1.Id)).Count == 1, $"Top-ups to 15 numbers totalling 700 → {(await Alerts(r1, p1.Id)).Count} alert.");
        Result("TM-PRP-01-02", (await Alerts(r1, p2.Id)).Count == 0, $"Own number top-up 20 → {(await Alerts(r1, p2.Id)).Count} alerts.");
        Result("TM-PRP-02-01", (await Alerts(r2, v1.Id)).Count == 1, $"12 vouchers of 100 within 3 days → {(await Alerts(r2, v1.Id)).Count} alert.");
        Result("TM-PRP-02-02", (await Alerts(r2, v2.Id)).Count == 0, $"One 50 voucher → {(await Alerts(r2, v2.Id)).Count} alerts.");
        Result("TM-PRP-03-01", (await Alerts(r3, b1.Id)).Count == 1, $"Pays 14 different electricity accounts → {(await Alerts(r3, b1.Id)).Count} alert.");
        Result("TM-PRP-03-02", (await Alerts(r3, b2.Id)).Count == 0, $"Pays own 3 bills → {(await Alerts(r3, b2.Id)).Count} alerts.");
        Result("TM-PRP-04-01", (await Alerts(r4, k1.Id)).Count == 1, $"Loads 1,000, ATM withdrawal in TR 4 h later → {(await Alerts(r4, k1.Id)).Count} alert.");
        Result("TM-PRP-04-02", (await Alerts(r4, k2.Id)).Count == 0, $"Load followed by domestic purchases → {(await Alerts(r4, k2.Id)).Count} alerts.");
        Done();
    }
}

[Collection("tm")]
public class TerroristFinancingSanctionsTests : TmBase
{
    public TerroristFinancingSanctionsTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private static readonly string[] Subject = { "rule", "version", "subject" };
    private static readonly string[] TfAreas = { "SY", "YE", "SO" };

    [Fact]
    public async Task TF_01_small_transfers_to_tf_areas()
    {
        var rule = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 30 }, condition = Cnt(And(F_("txn.destinationCountry", "in", TfAreas), F_("txn.amount", "lte", 200)), "gte", 5, "out") }, "High");
        var c1 = await Cust(); var c2 = await Cust();
        await Send(Enumerable.Range(0, 6).Select(i => T(c1.W, $"W-TF-{i}", 100, At(2025, 11, 3, "10:00:00").AddDays(i * 3), "Remittance", dst: "YE"))
            .Append(T(c2.W, "W-TF-X", 100, At(2025, 11, 3, "10:00:00"), "Remittance", dst: "YE")));
        await Run(rule, At(2025, 11, 1, "00:00:00"), At(2025, 12, 1, "00:00:00"));
        Result("TM-TF-01-01", (await Alerts(rule, c1.Id)).Count == 1, $"6 transfers of 100 to a configured high-risk area → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-TF-01-02", (await Alerts(rule, c2.Id)).Count == 0, $"1 transfer → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task TF_02_and_03_crowdfunding_and_npo()
    {
        await Field("TM_NPO_APPROVED", "Boolean", "Corporate");
        var donation = And(F_("txn.amount", "lte", 50), Or(F_("txn.remarks", "contains", "تبرع"), F_("txn.remarks", "contains", "donation")));
        var dc = new { aggregate = "distinctCounterparties", filter = donation, direction = "in", @operator = "gte", value = 30 };
        var dcNpo = new { aggregate = "distinctCounterparties", filter = donation, direction = "in", @operator = "gte", value = 500 };
        var r2 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 30 }, condition = Or(And(F_("field:TM_NPO_APPROVED", "neq", true), dc), And(F_("field:TM_NPO_APPROVED", "eq", true), dcNpo)) }, "High");
        var r3 = await Rule(new { dedupKey = new[] { "rule", "version", "subject", "window" }, window = new { type = "Calendar", unit = "Month" }, condition = And(F_("field:TM_NPO_APPROVED", "eq", true), Sum(F_("txn.destinationCountry", "in", TfAreas), "gte", 1000, "out")) }, "High");
        var ind = await Cust();
        var npo = await Cust("Corporate", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "88000001", ["TM_NPO_APPROVED"] = true }, name: "Tm Charity Collection One");
        var npo2 = await Cust("Corporate", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "88000002", ["TM_NPO_APPROVED"] = true }, name: "Tm Charity Collection Two");
        await Backdate(ind.Id, npo.Id, npo2.Id);
        var d = At(2025, 11, 10, "10:00:00");
        await Send(new[] { ind, npo }.SelectMany(c => Enumerable.Range(0, 40).Select(i => T($"W-DONOR-{i}", c.W, 5 + i % 16, d.AddHours(i), remarks: "تبرع للعائلات")))
            .Concat(Enumerable.Range(0, 4).Select(i => T(npo2.W, $"W-IND-SY-{i}", 500, d.AddDays(i), "Remittance", dst: "SY")))
            .Concat(Enumerable.Range(0, 4).Select(i => T(npo.W, $"W-SUPPLIER-{i}", 500, d.AddDays(i), "BankTransfer"))));
        await Run(r2, At(2025, 11, 1, "00:00:00"), At(2025, 12, 1, "00:00:00"));
        await Run(r3, At(2025, 11, 1, "00:00:00"), At(2025, 12, 1, "00:00:00"));
        Result("TM-TF-02-01", (await Alerts(r2, ind.Id)).Count == 1, $"40 senders send 5–20 with remarks 'تبرع' to an individual → {(await Alerts(r2, ind.Id)).Count} alert.");
        Result("TM-TF-02-02", (await Alerts(r2, npo.Id)).Count == 0, $"Approved NPO (dynamic field TM_NPO_APPROVED) with the same pattern → {(await Alerts(r2, npo.Id)).Count} alerts; NPO threshold (500 donors) applied.");
        Result("TM-TF-03-01", (await Alerts(r3, npo2.Id)).Count == 1, $"Charity wallet sends 2,000 to recipients in SY → {(await Alerts(r3, npo2.Id)).Count} alert. Note: the counterparty being an individual (vs entity) is not checkable; configured on destination country only.");
        Result("TM-TF-03-02", (await Alerts(r3, npo.Id)).Count == 0, $"Charity pays domestic suppliers → {(await Alerts(r3, npo.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task SAN_01_name_match()
    {
        var c = await Cust();
        await AddListEntry("UN-TEST", "Varokim Telzandi Qubrexo", "TM-SAN-01");
        await AddListEntry("UN-TEST", "Kamil Haddur Zubairan", "TM-SAN-03");
        var d1 = await Decide(Rt(c.W, 200, beneficiary: "Varokim Telzandy Qubrexo"));
        var sev = await F.WithDb(db => db.Set<AmlAlert>().Where(a => a.PrimaryCustomerId == c.Id && a.Source == "Realtime").Select(a => a.Severity).ToListAsync());
        Result("TM-SAN-01-01", d1["decision"]!.GetValue<string>() != "Allow" && sev.Contains("Critical"),
            $"Beneficiary fuzzy-matches a mandatory sanctions entry → real-time {d1["decision"]} ({d1["reasonCodes"]!.ToJsonString()}) before execution and alert(s) with severity [{string.Join(", ", sev)}]. Note: the score reached the reject threshold (0.95), so the decision is Reject rather than Hold; a score between 0.85 and 0.95 gives Hold.");
        var d2 = await Decide(Rt(c.W, 200, beneficiary: "Varon Pellik Ostrava"));
        Result("TM-SAN-01-02", d2["decision"]!.GetValue<string>() == "Allow", $"Low-similarity name → {d2["decision"]} ({d2["reasonCodes"]!.ToJsonString()}).");
        var d3 = await Decide(Rt(c.W, 200, beneficiary: "كامل حدور زبيران"));
        Result("TM-SAN-01-03", d3["decision"]!.GetValue<string>() != "Allow", $"Arabic-script beneficiary vs Latin list entry → {d3["decision"]} ({d3["reasonCodes"]!.ToJsonString()}).");
        Done();
    }

    [Fact]
    public async Task SAN_02_remarks_keywords()
    {
        await RealtimeRule("TMSAN02KW", new { realtimeCapable = true, window = new { type = "Calendar", unit = "Day" }, condition = Or(F_("txn.remarks", "contains", "weapon"), F_("txn.remarks", "contains", "سلاح"), F_("txn.remarks", "contains", "explosive")) });
        var c = await Cust();
        var d1 = await Decide(Rt(c.W, 80, remarks: "payment for weapon parts"));
        Result("TM-SAN-02-01", d1["decision"]!.GetValue<string>() == "HoldForReview", $"Remark contains configured keyword → {d1["decision"]} ({d1["reasonCodes"]!.ToJsonString()}).");
        var d2 = await Decide(Rt(c.W, 80, remarks: "rent October"));
        Result("TM-SAN-02-02", d2["decision"]!.GetValue<string>() == "Allow", $"Remark 'rent October' → {d2["decision"]}.");
        var d3a = await Decide(Rt(c.W, 80, remarks: "w e a p o n"));
        var d3b = await Decide(Rt(c.W, 80, remarks: "سِلاح"));
        Result("TM-SAN-02-03", d3a["decision"]!.GetValue<string>() == "HoldForReview" && d3b["decision"]!.GetValue<string>() == "HoldForReview",
            $"Obfuscated 'w e a p o n' → {d3a["decision"]}; Arabic keyword with diacritics 'سِلاح' → {d3b["decision"]} (keyword matching normalises case, diacritics and letter spacing).");
        Done();
    }

    [Fact]
    public async Task SAN_03_pep_counterparty()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = And(F_("customer.isPep", "eq", true), F_("txn.amount", "gte", 1000)) });
        var pep = await Cust(isPep: true, name: "Tm Domestic Pep");
        var c1 = await Cust(); var c2 = await Cust();
        await Send(new[] { T(c1.W, pep.W, 3000, At(2025, 11, 20, "10:00:00")), T(c2.W, pep.W, 200, At(2025, 11, 21, "10:00:00")) });
        await Run(rule, At(2025, 11, 20, "00:00:00"), At(2025, 11, 22, "00:00:00"));
        var a = await Alerts(rule, pep.Id);
        var parties = a.Count == 0 ? new List<long>() : await F.WithDb(db => db.Set<AmlAlertTransaction>().Where(x => x.AlertId == a[0].Id).Select(x => x.TransactionId).ToListAsync());
        Result("TM-SAN-03-01", a.Count == 1, $"Customer sends 3,000 to a domestic PEP → {a.Count} alert (subject = the PEP counterparty; the sender is linked through the transaction). Note: a rule cannot test the counterparty's PEP flag from the sender's perspective.");
        Result("TM-SAN-03-02", a.Count == 1 && parties.Count == 1, $"200 transfer to the PEP → not matched ({parties.Count} transaction linked to the alert, the 3,000 transfer only).");
        Done();
    }

    [Fact]
    public async Task SAN_04_internal_watchlist()
    {
        var c = await Cust();
        await AddListEntry("INTERNAL", "Pelvaro Quintesk Dramuli", "TM-SAN-04");
        var d1 = await Decide(Rt(c.W, 50, beneficiary: "Pelvaro Quintesk Dramuli"));
        var alerts = await F.WithDb(db => db.Set<AmlAlert>().Where(a => a.PrimaryCustomerId == c.Id && a.Source == "Realtime").Select(a => a.Severity).ToListAsync());
        Result("TM-SAN-04-01", d1["decision"]!.GetValue<string>() != "Allow" && alerts.Count > 0,
            $"Transfer to a party on the internal watchlist → real-time {d1["decision"]} ({d1["reasonCodes"]!.ToJsonString()}) and alert raised (severity {string.Join(", ", alerts)}).");
        await RemoveListEntry("INTERNAL", "TM-SAN-04");
        var d2 = await Decide(Rt(c.W, 50, beneficiary: "Pelvaro Quintesk Dramuli"));
        Result("TM-SAN-04-02", d2["decision"]!.GetValue<string>() == "Allow", $"Party removed from list (new list version) → {d2["decision"]}.");
        Done();
    }
}
