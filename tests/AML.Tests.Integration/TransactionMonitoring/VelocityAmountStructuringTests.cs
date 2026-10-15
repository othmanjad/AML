using AML.Domain.Entities;
using AML.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.TransactionMonitoring;

[Collection("tm")]
public class VelocityTests : TmBase
{
    public VelocityTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private static readonly object CashOutFilter = new { op = "and", conditions = new object[] { new { field = "txn.type", @operator = "in", value = new[] { "CashOut", "Withdrawal" } }, new { field = "txn.channel", @operator = "in", value = new[] { "Agent", "ATM" } } } };

    [Fact]
    public async Task VEL_01_cash_withdrawals_per_day()
    {
        var rule = await Rule(new { reversalHandling = "Exclude", window = new { type = "Calendar", unit = "Day" }, condition = Cnt(CashOutFilter, "gte", 20, "out") }, "High");
        var agent = await Cust("Agent", name: "Tm Agent Till");
        const int D = 1;
        var from = L(D, "00:00:00", -1); var to = L(D, "23:59:59", 3);

        var c1 = await Cust();
        var ids = Enumerable.Range(0, 20).Select(i => $"V01A-{c1.Id}-{i}").ToList();
        await Send(ids.Select((id, i) => T(c1.W, agent.W, 50, L(D, "09:00:00").AddMinutes(i * 27), "CashOut", channel: "Agent", id: id)));
        var c2 = await Cust();
        await Send(Enumerable.Range(0, 19).Select(i => T(c2.W, agent.W, 50, L(D, "09:00:00").AddMinutes(i * 27), "CashOut", channel: "Agent")));
        var c3 = await Cust();
        await Send(Enumerable.Range(0, 19).Select(i => T(c3.W, agent.W, 50, L(D, "09:00:00").AddMinutes(i * 27), "CashOut", channel: "ATM"))
            .Append(T(c3.W, agent.W, 50, L(D, "23:59:59"), "CashOut", channel: "ATM")));
        var c4 = await Cust();
        await Send(Enumerable.Range(0, 10).Select(i => T(c4.W, agent.W, 50, L(D, "23:00:00").AddMinutes(i * 5), "CashOut", channel: "Agent"))
            .Concat(Enumerable.Range(0, 10).Select(i => T(c4.W, agent.W, 50, L(D, "00:00:00", 1).AddMinutes(i * 5), "CashOut", channel: "Agent"))));
        var c5 = await Cust();
        await Send(Enumerable.Range(0, 20).Select(i => T(c5.W, agent.W, 50, L(D, "10:00:00").AddMinutes(i * 10), "CashOut", status: i < 6 ? "FAIL" : "SUCC", channel: "Agent")));
        var c6 = await Cust();
        var ids6 = Enumerable.Range(0, 20).Select(i => $"V01R-{c6.Id}-{i}").ToList();
        await Send(ids6.Select((id, i) => T(c6.W, agent.W, 50, L(D, "10:00:00").AddMinutes(i * 10), "CashOut", channel: "Agent", id: id)));
        await Send(ids6.Take(3).Select((id, i) => T(agent.W, c6.W, 50, L(D, "16:00:00").AddMinutes(i), "Reversal", channel: "Agent", original: id, reversal: true)));
        var c7 = await Cust();
        var w7b = await Wallet(c7.Id);
        await Send(Enumerable.Range(0, 20).Select(i => T(i % 2 == 0 ? c7.W : w7b, agent.W, 50, L(D, "10:00:00").AddMinutes(i * 10), "CashOut", channel: "Agent")));

        await Run(rule, from, to);

        var a1 = await Alerts(rule, c1.Id);
        var m1 = a1.Count == 1 ? await Matches(a1[0].Id) : new();
        var twentieth = await TxnId(ids[19]);
        var n1 = a1.Count == 1 ? await TxnCount(a1[0].Id) : 0;
        Result("TM-VEL-01-01", a1.Count == 1 && m1.Count == 1 && m1[0].MatchKey == $"trigger:{twentieth}" && n1 == 20 && m1[0].EvidenceJson.Contains("windowStartLocal"),
            $"{a1.Count} alert ({a1.FirstOrDefault()?.AlertNumber}) triggered by the 20th withdrawal ({ids[19]}); {n1} contributing transactions linked; evidence window {Window(m1)}.");
        var a2 = await Alerts(rule, c2.Id);
        Result("TM-VEL-01-02", a2.Count == 0, $"19 withdrawals → {a2.Count} alerts.");
        var a3 = await Alerts(rule, c3.Id);
        Result("TM-VEL-01-03", a3.Count == 1, $"20th withdrawal at 23:59:59 local → {a3.Count} alert (same calendar day {Window(a3.Count == 1 ? await Matches(a3[0].Id) : new())}).");
        var a4 = await Alerts(rule, c4.Id);
        Result("TM-VEL-01-04", a4.Count == 0, $"10 withdrawals 23:00–23:45 and 10 on the next local day → {a4.Count} alerts (two calendar days of 10).");
        var a5 = await Alerts(rule, c5.Id);
        Result("TM-VEL-01-05", a5.Count == 0, $"20 attempts, 6 Failed → 14 completed counted → {a5.Count} alerts.");
        var a6 = await Alerts(rule, c6.Id);
        Result("TM-VEL-01-06", a6.Count == 0, $"Rule netting policy 'Exclude': 3 of 20 withdrawals reversed → 17 counted → {a6.Count} alerts (with policy 'Include' the reversed items would count).");
        var a7 = await Alerts(rule, c7.Id);
        Result("TM-VEL-01-07", a7.Count == 1, $"20 withdrawals split 10/10 across two wallets of the same customer → {a7.Count} customer-level alert.");
        Done();
    }

    private static string Window(List<AML.Domain.Entities.AmlAlertRuleMatch> m) => m.Count == 0 ? "n/a" : $"{m[0].WindowStart:yyyy-MM-dd HH:mm}Z–{m[0].WindowEnd:yyyy-MM-dd HH:mm}Z";

    [Fact]
    public async Task VEL_02_withdrawal_burst_per_hour()
    {
        var rule = await Rule(new { window = new { type = "Rolling", minutes = 60 }, condition = Cnt(new { field = "txn.type", @operator = "in", value = new[] { "CashOut", "Withdrawal" } }, "gte", 8, "out") }, "High");
        const int D = 2;
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust(); var c4 = await Cust();
        await Send(Enumerable.Range(0, 8).Select(i => T(c1.W, "ATM-1", 100, L(D, "14:00:00").AddMinutes(i * 6), "Withdrawal", channel: "ATM"))
            .Concat(Enumerable.Range(0, 8).Select(i => T(c2.W, "ATM-1", 100, L(D, "14:00:00").AddSeconds(i == 7 ? 3601 : i * 500), "Withdrawal", channel: "ATM")))
            .Concat(Enumerable.Range(0, 8).Select(i => T(c3.W, "ATM-1", 100, L(D, "09:00:00").AddHours(i), "Withdrawal", channel: "ATM")))
            .Concat(Enumerable.Range(0, 8).Select(i => T(c4.W, "ATM-1", 100, L(D, "23:30:00").AddMinutes(i * 7), "Withdrawal", channel: "ATM"))));
        await Run(rule, L(D, "00:00:00"), L(D, "23:59:59", 2));
        var a1 = await Alerts(rule, c1.Id);
        var ev = a1.Count == 1 ? (await Matches(a1[0].Id))[0].EvidenceJson : "";
        Result("TM-VEL-02-01", a1.Count == 1 && ev.Contains("windowStartUtc"), $"8 ATM withdrawals 14:00–14:42 → {a1.Count} alert; rolling 60-min window boundaries in evidence.");
        var a2 = await Alerts(rule, c2.Id);
        Result("TM-VEL-02-02", a2.Count == 0, $"8 withdrawals spanning 60 min 01 s (window (t−60m, t]) → {a2.Count} alerts.");
        var a3 = await Alerts(rule, c3.Id);
        Result("TM-VEL-02-03", a3.Count == 0, $"8 withdrawals one per hour → {a3.Count} alerts.");
        var a4 = await Alerts(rule, c4.Id);
        Result("TM-VEL-02-04", a4.Count == 1, $"Burst 23:30–00:19 local across midnight → {a4.Count} alert (rolling window ignores calendar day).");
        Done();
    }

    [Fact]
    public async Task VEL_03_outgoing_p2p_per_day()
    {
        var p2p = And(F_("txn.type", "eq", "P2P"), F_("txn.isOwnAccount", "eq", false));
        var rule = await Rule(new { scope = new { customerTypes = new[] { "Individual" } }, window = new { type = "Calendar", unit = "Day" }, condition = Cnt(p2p, "gte", 30, "out") });
        var ruleMerchant = await Rule(new { scope = new { customerTypes = new[] { "Merchant" } }, window = new { type = "Calendar", unit = "Day" }, condition = Cnt(p2p, "gte", 200, "out") });
        const int D = 3;
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust(); var m = await Cust("Merchant");
        var c3b = await Wallet(c3.Id);
        var rnd = new Random(3);
        await Send(Enumerable.Range(0, 30).Select(i => T(c1.W, $"W-EXT-{i}", 10 + rnd.Next(31), L(D, "08:00:00").AddMinutes(i * 20)))
            .Concat(Enumerable.Range(0, 29).Select(i => T(c2.W, $"W-EXT-{i}", 20, L(D, "08:00:00").AddMinutes(i * 20))))
            .Concat(Enumerable.Range(0, 30).Select(i => T(c3.W, i < 15 ? c3b : $"W-EXT-{i}", 20, L(D, "08:00:00").AddMinutes(i * 20))))
            .Concat(Enumerable.Range(0, 30).Select(i => T(m.W, $"W-EXT-{i}", 20, L(D, "08:00:00").AddMinutes(i * 20)))));
        await Run(rule, L(D, "00:00:00"), L(D, "23:59:59"));
        await Run(ruleMerchant, L(D, "00:00:00"), L(D, "23:59:59"));
        Result("TM-VEL-03-01", (await Alerts(rule, c1.Id)).Count == 1, $"30 P2P transfers (10–40 JOD) in one day → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-VEL-03-02", (await Alerts(rule, c2.Id)).Count == 0, $"29 transfers → {(await Alerts(rule, c2.Id)).Count} alerts.");
        var a3 = await Alerts(rule, c3.Id);
        Result("TM-VEL-03-03", a3.Count == 0, $"30 transfers of which 15 to the customer's own second wallet → {a3.Count} alert(s). " +
            (a3.Count == 0 ? "Own-account transfers excluded by the rule filter txn.isOwnAccount = false (15 counted)." : "DEFECT: the rule DSL has no own-account/self-transfer exclusion, so transfers between the customer's own wallets are counted as outgoing P2P."));
        var am = (await Alerts(rule, m.Id)).Count + (await Alerts(ruleMerchant, m.Id)).Count;
        Result("TM-VEL-03-04", am == 0, $"Merchant with 30 transfers: individual rule scoped out, merchant rule threshold 200 → {am} alerts.");
        Done();
    }

    [Fact]
    public async Task VEL_04_incoming_per_day()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = Cnt(new { field = "txn.type", @operator = "notIn", value = new[] { "Refund", "Reversal" } }, "gte", 25, "in") });
        const int D = 4;
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust(); var c4 = await Cust(); var merchant = await Cust("Merchant");
        await Send(Enumerable.Range(0, 25).Select(i => T($"W-S{i}", c1.W, 20, L(D, "08:00:00").AddMinutes(i * 15)))
            .Concat(Enumerable.Range(0, 24).Select(i => T($"W-S{i}", c2.W, 20, L(D, "08:00:00").AddMinutes(i * 15))))
            .Concat(Enumerable.Range(0, 25).Select(i => T(merchant.W, c3.W, 20, L(D, "08:00:00").AddMinutes(i * 15), "Refund", refund: true)))
            .Append(T("W-EMPLOYER", c4.W, 25 * 400, L(D, "09:00:00"), "SalaryBatch")));
        await Run(rule, L(D, "00:00:00"), L(D, "23:59:59"));
        Result("TM-VEL-04-01", (await Alerts(rule, c1.Id)).Count == 1, $"25 incoming transfers of 20 from different senders → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-VEL-04-02", (await Alerts(rule, c2.Id)).Count == 0, $"24 incoming → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-VEL-04-03", (await Alerts(rule, c3.Id)).Count == 0, $"25 incoming merchant refunds excluded by type filter → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Result("TM-VEL-04-04", (await Alerts(rule, c4.Id)).Count == 0, $"Salary bulk payment ingested as one batch record → counted once → {(await Alerts(rule, c4.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task VEL_05_weekly_count()
    {
        var rule = await Rule(new { scope = new { customerTypes = new[] { "Individual" } }, window = new { type = "Rolling", days = 7 }, condition = Cnt(null, "gte", 100) });
        const int D = 5;
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust(); var corp = await Cust("Corporate", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "12345678" });
        string[] types = { "P2P", "CashOut", "BillPayment", "MerchantPayment" };
        await Send(Enumerable.Range(0, 101).Select(i => T(c1.W, $"W-X{i}", 5, L(D, "08:00:00").AddMinutes(i * 80), types[i % 4]))
            .Concat(Enumerable.Range(0, 100).Select(i => T(c2.W, $"W-X{i}", 5, L(D, "08:00:00").AddMinutes(i * 100))))
            .Concat(Enumerable.Range(0, 100).Select(i => T(c3.W, $"W-X{i}", 5, L(D, "08:00:00").AddMinutes(i * 116))))
            .Concat(Enumerable.Range(0, 150).Select(i => T(corp.W, $"W-X{i}", 5, L(D, "08:00:00").AddMinutes(i * 50)))));
        await Run(rule, L(D, "00:00:00"), L(D, "00:00:00", 10));
        Result("TM-VEL-05-01", (await Alerts(rule, c1.Id)).Count >= 1, $"101 mixed transactions in ~5.6 days → {(await Alerts(rule, c1.Id)).Count} alert(s).");
        Result("TM-VEL-05-02", (await Alerts(rule, c2.Id)).Count >= 1, $"Exactly 100 transactions within 6.9 days → {(await Alerts(rule, c2.Id)).Count} alert (≥).");
        Result("TM-VEL-05-03", (await Alerts(rule, c3.Id)).Count == 0, $"100 transactions spread over 8 days → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Result("TM-VEL-05-04", (await Alerts(rule, corp.Id)).Count == 0, $"Corporate with 150 in a week, rule scoped to individuals → {(await Alerts(rule, corp.Id)).Count} alerts.");
        Done();
    }
}

[Collection("tm")]
public class AmountTests : TmBase
{
    public AmountTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact]
    public async Task AMT_01_single_large_transaction()
    {
        var ind = await Rule(new { scope = new { customerTypes = new[] { "Individual" } }, condition = F_("txn.baseAmount", "gte", 10000) });
        var corpRule = await Rule(new { scope = new { customerTypes = new[] { "Corporate" } }, condition = F_("txn.baseAmount", "gte", 50000) });
        const int D = 6;
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust(); var c5 = await Cust();
        var corp = await Cust("Corporate", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "12345678" });
        await Send(new[] { T(c1.W, "W-B1", 10000, L(D, "10:00:00")), T(c2.W, "W-B1", 9999.999m, L(D, "10:00:00")), T(c3.W, "W-B1", 15000, L(D, "10:00:00"), currency: "USD"), T(corp.W, "W-B1", 20000, L(D, "10:00:00")) });
        var pid = $"AMT01P-{c5.Id}";
        await Send(new[] { T(c5.W, "W-B1", 12000, L(D, "11:00:00"), status: "PEND", id: pid) });
        await Run(ind, L(D, "00:00:00"), L(D, "23:59:59"));
        var pendingAlerts = (await Alerts(ind, c5.Id)).Count;
        await Send(new[] { T(c5.W, "W-B1", 12000, L(D, "11:05:00"), status: "SUCC", id: pid) });
        await Run(ind, L(D, "00:00:00"), L(D, "23:59:59"));
        await Run(ind, L(D, "00:00:00"), L(D, "23:59:59"));
        await Run(corpRule, L(D, "00:00:00"), L(D, "23:59:59"));
        Result("TM-AMT-01-01", (await Alerts(ind, c1.Id)).Count == 1, $"Individual 10,000 JOD transfer → {(await Alerts(ind, c1.Id)).Count} alert.");
        Result("TM-AMT-01-02", (await Alerts(ind, c2.Id)).Count == 0, $"9,999.999 JOD → {(await Alerts(ind, c2.Id)).Count} alerts (decimal precision preserved).");
        var a3 = await Alerts(ind, c3.Id);
        Result("TM-AMT-01-03", a3.Count == 1 && a3[0].EvidenceJson.Contains("0.709"), $"15,000 USD → base 10,635 JOD → {a3.Count} alert; FX rate 0.709 and date in evidence.");
        var ac = (await Alerts(ind, corp.Id)).Count + (await Alerts(corpRule, corp.Id)).Count;
        Result("TM-AMT-01-04", ac == 0, $"Corporate 20,000 (corporate threshold 50,000) → {ac} alerts.");
        var a5 = await Alerts(ind, c5.Id);
        Result("TM-AMT-01-05", pendingAlerts == 0 && a5.Count == 1, $"While Pending: {pendingAlerts} alerts; after status update to Completed and two reruns: {a5.Count} alert (raised once, at configured status).");
        Done();
    }

    [Fact]
    public async Task AMT_02_daily_cumulative()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = Sum(null, "gte", 5000, "out") });
        const int D = 7;
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust(); var c4 = await Cust();
        var ids = Enumerable.Range(0, 5).Select(i => $"AMT02-{c1.Id}-{i}").ToList();
        await Send(ids.Select((id, i) => T(c1.W, "W-B2", 1000, L(D, "09:00:00").AddHours(i), id: id))
            .Concat(new[] { T(c2.W, "W-B2", 2500, L(D, "09:00:00")), T(c2.W, "W-B2", 2499.99m, L(D, "12:00:00")) })
            .Concat(new[] { T(c3.W, "W-B2", 3000, L(D, "12:00:00")), T(c3.W, "W-B2", 3000, L(D, "12:00:00", 1)) })
            .Concat(new[] { T(c4.W, "W-B2", 2000, L(D, "09:00:00")), T(c4.W, "W-B2", 4300, L(D, "10:00:00"), currency: "USD") }));
        await Run(rule, L(D, "00:00:00"), L(D, "23:59:59", 1));
        var a1 = await Alerts(rule, c1.Id);
        var m1 = a1.Count == 1 ? await Matches(a1[0].Id) : new();
        Result("TM-AMT-02-01", a1.Count == 1 && m1[0].MatchKey == $"trigger:{await TxnId(ids[4])}", $"5 × 1,000 same day → {a1.Count} alert raised on the 5th transfer.");
        Result("TM-AMT-02-02", (await Alerts(rule, c2.Id)).Count == 0, $"Total 4,999.99 → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-AMT-02-03", (await Alerts(rule, c3.Id)).Count == 0, $"3,000 on day 1 and 3,000 on day 2 → {(await Alerts(rule, c3.Id)).Count} alerts.");
        var a4 = await Alerts(rule, c4.Id);
        Result("TM-AMT-02-04", a4.Count == 1, $"2,000 JOD + 4,300 USD (=3,048.70 JOD at 0.709) = 5,048.70 JOD → {a4.Count} alert.");
        Done();
    }

    [Fact]
    public async Task AMT_03_monthly_income_multiple()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Month" }, condition = new { aggregate = "profileRatio", profileField = "profile.declaredMonthlyIncome", direction = "in", @operator = "gt", value = 3 } }, "High");
        object Fp(decimal income) => new { declaredMonthlyIncome = income, currency = "JOD", sourceOfFunds = "Salary", effectiveFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var c1 = await Cust(financialProfile: Fp(500)); var c2 = await Cust(financialProfile: Fp(500)); var c3 = await Cust(financialProfile: Fp(2000)); var c4 = await Cust();
        var c5 = await Cust(financialProfile: Fp(500));
        await (await As("manager")).PostOk($"/api/customers/{c5.Id}/financial-profiles", new { declaredMonthlyIncome = 2000, currency = "JOD", sourceOfFunds = "Promotion", effectiveFrom = At(2025, 3, 15, "00:00:00") });
        await Send(new[] { T("W-P1", c1.W, 800, At(2025, 3, 5, "10:00:00")), T("W-P2", c1.W, 800, At(2025, 3, 20, "10:00:00")),
            T("W-P1", c2.W, 750, At(2025, 3, 5, "10:00:00")), T("W-P2", c2.W, 750, At(2025, 3, 20, "10:00:00")),
            T("W-P1", c3.W, 4000, At(2025, 3, 10, "10:00:00")), T("W-P1", c4.W, 1600, At(2025, 3, 10, "10:00:00")),
            T("W-P1", c5.W, 800, At(2025, 3, 5, "10:00:00")), T("W-P2", c5.W, 800, At(2025, 3, 20, "10:00:00")) });
        await Run(rule, At(2025, 3, 1, "00:00:00"), At(2025, 4, 1, "00:00:00"));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-AMT-03-01", a1.Count == 1 && a1[0].EvidenceJson.Contains("profileRatio.profileValue"), $"Declared income 500, received 1,600 in the month (3.2×) → {a1.Count} alert; declared income value and profile field in evidence (financial-profile version is effective-dated; version number itself not written to evidence).");
        Result("TM-AMT-03-02", (await Alerts(rule, c2.Id)).Count == 0, $"Received exactly 1,500 (3.0×, rule is strictly greater) → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-AMT-03-03", (await Alerts(rule, c3.Id)).Count == 0, $"Income 2,000, received 4,000 (2×) → {(await Alerts(rule, c3.Id)).Count} alerts.");
        var a4 = await Alerts(rule, c4.Id);
        var exec = await F.WithDb(db => db.Set<AML.Domain.Entities.AmlRuleExecution>().Where(e => e.RuleId == rule).OrderByDescending(e => e.Id).FirstAsync());
        var dq4 = await F.WithDb(db => db.Set<DataQualityIssue>().Where(d => d.IssueType == "RuleInputMissing" && d.EntityId == c4.Id.ToString()).Select(d => d.Description).FirstOrDefaultAsync());
        Result("TM-AMT-03-04", dq4 is not null, $"Declared income missing → {a4.Count} alerts; condition treated as not met and data-quality item raised: \"{dq4}\" (execution status {exec.Status}).");
        var a5 = await Alerts(rule, c5.Id);
        Result("TM-AMT-03-05", a5.Count == 0, $"Income changed 500→2,000 on 15 Mar; 1,600 received → evaluated against the value effective at each transaction time (documented rule) → 1,600/2,000 = 0.8 at month total → {a5.Count} alerts.");
        Done();
    }

    [Fact]
    public async Task AMT_04_expected_activity()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Month" }, condition = new { aggregate = "profileRatio", profileField = "profile.expectedMonthlyVolume", @operator = "gt", value = 3 } });
        object Fp(decimal exp) => new { declaredMonthlyIncome = 1000, expectedMonthlyVolume = exp, currency = "JOD", sourceOfFunds = "Salary", effectiveFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var c1 = await Cust(financialProfile: Fp(1000)); var c2 = await Cust(financialProfile: Fp(1000)); var c3 = await Cust(financialProfile: Fp(1000)); var c4 = await Cust(financialProfile: Fp(1000));
        await (await As("manager")).PostOk($"/api/customers/{c4.Id}/financial-profiles", new { declaredMonthlyIncome = 3000, expectedMonthlyVolume = 5000, currency = "JOD", sourceOfFunds = "Business", effectiveFrom = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
        await Send(new[] { T(c1.W, "W-Q", 3100, At(2025, 3, 10, "10:00:00")), T(c2.W, "W-Q", 3000, At(2025, 3, 10, "10:00:00")), T(c3.W, "W-Q", 2500, At(2025, 3, 10, "10:00:00")), T(c4.W, "W-Q", 3100, At(2025, 3, 10, "10:00:00")) });
        await Run(rule, At(2025, 3, 1, "00:00:00"), At(2025, 4, 1, "00:00:00"));
        Result("TM-AMT-04-01", (await Alerts(rule, c1.Id)).Count == 1, $"Expected 1,000/month, actual 3,100 (>200% above) → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-AMT-04-02", (await Alerts(rule, c2.Id)).Count == 0, $"Actual 3,000 (exactly 200% above, not exceeded) → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-AMT-04-03", (await Alerts(rule, c3.Id)).Count == 0, $"Actual 2,500 → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Result("TM-AMT-04-04", (await Alerts(rule, c4.Id)).Count == 0, $"EDD-approved higher expectation 5,000 (latest effective profile) used; 3,100 → {(await Alerts(rule, c4.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task AMT_08_basic_kyc_limit()
    {
        var rule = await Rule(new { condition = And(F_("field:PRODUCT_TIER", "eq", "BASIC"), F_("txn.direction", "eq", "in"), F_("txn.baseAmount", "gt", 1000)) }, "High");
        var basicAttr = new Dictionary<string, object> { ["OCCUPATION"] = "EMPLOYEE", ["SOURCE_OF_INCOME"] = "SALARY" };
        var c1 = await Cust(attributes: basicAttr); var c2 = await Cust(attributes: basicAttr); var c3 = await Cust(attributes: basicAttr);
        var api = await As("manager");
        var at = At(2025, 4, 10, "12:00:00");
        await api.PutOk($"/api/customers/{c1.Id}/attributes", new { values = new { PRODUCT_TIER = "BASIC" }, effectiveFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), reason = "basic wallet" });
        await api.PutOk($"/api/customers/{c2.Id}/attributes", new { values = new { PRODUCT_TIER = "PREMIUM" }, effectiveFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), reason = "full kyc" });
        await api.PutOk($"/api/customers/{c3.Id}/attributes", new { values = new { PRODUCT_TIER = "BASIC" }, effectiveFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), reason = "basic" });
        await api.PutOk($"/api/customers/{c3.Id}/attributes", new { values = new { PRODUCT_TIER = "PREMIUM" }, effectiveFrom = at.AddHours(-1), reason = "upgraded to full KYC" });
        await Send(new[] { T("W-SRC", c1.W, 1200, at), T("W-SRC", c2.W, 1200, at), T("W-SRC", c3.W, 1200, at) });
        await Run(rule, at.AddHours(-2), at.AddHours(2));
        Result("TM-AMT-08-01", (await Alerts(rule, c1.Id)).Count == 1, $"Basic-KYC wallet received 1,200 (> product limit 1,000) → {(await Alerts(rule, c1.Id)).Count} limit-breach alert.");
        Result("TM-AMT-08-02", (await Alerts(rule, c2.Id)).Count == 0, $"Full-KYC wallet received 1,200 → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-AMT-08-03", (await Alerts(rule, c3.Id)).Count == 0, $"Wallet upgraded 1 h before the transaction; KYC level evaluated as of transaction time (effective-dated field) → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Done();
    }
}

[Collection("tm")]
public class StructuringTests : TmBase
{
    public StructuringTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact]
    public async Task STR_01_below_threshold()
    {
        var rule = await Rule(new { window = new { type = "Rolling", days = 7 }, condition = Cnt(F_("txn.baseAmount", "between", new[] { 9000m, 9999.99m }), "gte", 3, "in") }, "High");
        const int D = 10;
        var c = new List<(long Id, string W)>();
        for (var i = 0; i < 5; i++) c.Add(await Cust());
        await Send(new[]
        {
            T("W-D", c[0].W, 9500, L(D, "10:00:00")), T("W-D", c[0].W, 9800, L(D, "10:00:00", 2)), T("W-D", c[0].W, 9900, L(D, "10:00:00", 3)),
            T("W-D", c[1].W, 9500, L(D, "10:00:00")), T("W-D", c[1].W, 9800, L(D, "11:00:00")), T("W-D", c[1].W, 8999, L(D, "12:00:00")),
            T("W-D", c[2].W, 9000, L(D, "10:00:00")), T("W-D", c[2].W, 9500, L(D, "11:00:00")), T("W-D", c[2].W, 9600, L(D, "12:00:00")),
            T("W-D", c[3].W, 10500, L(D, "10:00:00")), T("W-D", c[3].W, 10500, L(D, "11:00:00")), T("W-D", c[3].W, 10500, L(D, "12:00:00")),
            T("AGT-TILL", c[4].W, 9500, L(D, "10:00:00"), "CashIn", channel: "Agent"), T("W-BANK", c[4].W, 9700, L(D, "11:00:00"), "BankTransfer", channel: "Bank"), T("W-CARD", c[4].W, 9900, L(D, "12:00:00"), "CardLoad", channel: "Card"),
        });
        await Run(rule, L(D, "00:00:00"), L(D, "00:00:00", 6));
        Result("TM-STR-01-01", (await Alerts(rule, c[0].Id)).Count == 1, $"9,500 / 9,800 / 9,900 within 4 days → {(await Alerts(rule, c[0].Id)).Count} structuring alert.");
        Result("TM-STR-01-02", (await Alerts(rule, c[1].Id)).Count == 0, $"Two in band + 8,999 → {(await Alerts(rule, c[1].Id)).Count} alerts.");
        Result("TM-STR-01-03", (await Alerts(rule, c[2].Id)).Count == 1, $"9,000 (exactly 90%) counted (inclusive lower bound) with two others → {(await Alerts(rule, c[2].Id)).Count} alert.");
        Result("TM-STR-01-04", (await Alerts(rule, c[3].Id)).Count == 0, $"Three deposits of 10,500 → {(await Alerts(rule, c[3].Id)).Count} structuring alerts (handled by single-large rule).");
        Result("TM-STR-01-05", (await Alerts(rule, c[4].Id)).Count == 1, $"Deposits via agent, bank transfer and card aggregated across channels → {(await Alerts(rule, c[4].Id)).Count} alert.");
        Done();
    }

    [Fact]
    public async Task STR_02_split_cash_in_agents()
    {
        var cashIn = F_("txn.type", "eq", "CashIn");
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = And(new { aggregate = "distinctCounterparties", filter = cashIn, direction = "in", @operator = "gte", value = 3 }, Sum(cashIn, "gte", 3000, "in")) }, "High");
        const int D = 11;
        var agA = await Cust("Agent", name: "Tm Agent A"); var agB = await Cust("Agent", name: "Tm Agent B (sub-agent of A)"); var agC = await Cust("Agent", name: "Tm Agent C");
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust(); var c4 = await Cust();
        await Send(new[]
        {
            T(agA.W, c1.W, 1000, L(D, "09:00:00"), "CashIn", channel: "Agent"), T(agB.W, c1.W, 1000, L(D, "12:00:00"), "CashIn", channel: "Agent"), T(agC.W, c1.W, 1100, L(D, "16:00:00"), "CashIn", channel: "Agent"),
            T(agA.W, c2.W, 3100, L(D, "09:00:00"), "CashIn", channel: "Agent"),
            T(agA.W, c3.W, 2000, L(D, "09:00:00"), "CashIn", channel: "Agent"), T(agC.W, c3.W, 1500, L(D, "12:00:00"), "CashIn", channel: "Agent"),
            T(agA.W, c4.W, 1000, L(D, "09:00:00"), "CashIn", channel: "Agent"), T(agB.W, c4.W, 1000, L(D, "10:00:00"), "CashIn", channel: "Agent"), T(agC.W, c4.W, 1000, L(D, "11:00:00"), "CashIn", channel: "Agent"),
        });
        await Run(rule, L(D, "00:00:00"), L(D, "23:59:59"));
        Result("TM-STR-02-01", (await Alerts(rule, c1.Id)).Count == 1, $"1,000 / 1,000 / 1,100 cash-in at 3 agents same day → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-STR-02-02", (await Alerts(rule, c2.Id)).Count == 0, $"3,100 at one agent → {(await Alerts(rule, c2.Id)).Count} alerts for this rule.");
        Result("TM-STR-02-03", (await Alerts(rule, c3.Id)).Count == 0, $"2 distinct agents → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Result("TM-STR-02-04", (await Alerts(rule, c4.Id)).Count == 1, $"Agent B (sub-agent of A) counted as a distinct agent (separate agent customer/till) → {(await Alerts(rule, c4.Id)).Count} alert.");
        Done();
    }

    [Fact]
    public async Task STR_03_smurfing()
    {
        var rule = await Rule(new { scope = new { customerTypes = new[] { "Individual" } }, window = new { type = "Rolling", days = 3 }, condition = new { aggregate = "distinctCounterparties", filter = F_("txn.baseAmount", "lt", 500), direction = "in", @operator = "gte", value = 10 } }, "High");
        const int D = 12;
        var c1 = await Cust(); var c2 = await Cust(); var merchant = await Cust("Merchant"); var c4 = await Cust();
        var senders = new List<(long Id, string W)>();
        for (var i = 0; i < 12; i++) senders.Add(await Cust());
        var nid = "9" + Random.Shared.NextInt64(100000000, 999999999);
        foreach (var s in senders.Take(3)) await (await As("manager")).PostOk($"/api/customers/{s.Id}/identifications", new { idType = "Passport", idNumber = nid, issuingCountry = "JO" });
        await (await As("officer")).PostOk("/api/governance/entity-resolution/run");
        await Send(senders.Select((s, i) => T(s.W, c1.W, 450, L(D, "09:00:00").AddHours(i * 3)))
            .Concat(senders.Take(9).Select((s, i) => T(s.W, c2.W, 450, L(D, "09:00:00").AddHours(i * 3))))
            .Concat(senders.Select((s, i) => T(s.W, merchant.W, 450, L(D, "09:00:00").AddHours(i * 3), "MerchantPayment")))
            .Concat(senders.Select((s, i) => T(s.W, c4.W, 450, L(D, "09:00:00").AddHours(i * 3)))));
        await Run(rule, L(D, "00:00:00"), L(D, "00:00:00", 3));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-STR-03-01", a1.Count == 1 && a1[0].EvidenceJson.Contains("distinctCounterparties.counterparties"), $"12 different senders × 450 within 2 days → {a1.Count} alert; sender list in evidence.");
        Result("TM-STR-03-02", (await Alerts(rule, c2.Id)).Count == 0, $"9 senders → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-STR-03-03", (await Alerts(rule, merchant.Id)).Count == 0, $"Receiver is a registered merchant (rule scoped to individuals) → {(await Alerts(rule, merchant.Id)).Count} alerts.");
        var a4 = await Alerts(rule, c4.Id);
        var flagged = a4.Count == 1 && a4[0].EvidenceJson.Contains("linkedParties") && a4[0].EvidenceJson.Contains("SameIdentity");
        Result("TM-STR-03-04", flagged, $"Alert raised ({a4.Count}); 3 senders share one ID token and are linked by entity resolution, alert evidence {(flagged ? "flags" : "does NOT flag")} the linked parties (linkedParties: link type and confidence).");
        Done();
    }

    [Fact]
    public async Task STR_04_multiple_own_wallets()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = Sum(null, "gte", 5000, "in") }, "High");
        const int D = 13;
        var p = await Cust(); var w2 = await Wallet(p.Id); var w3 = await Wallet(p.Id);
        var single = await Cust();
        var d1 = await Cust(); var d2 = await Cust(); var d3 = await Cust();
        var device = "DEV-STR04-" + U();
        foreach (var x in new[] { d1, d2, d3 }) await (await As("manager")).PostOk($"/api/customers/{x.Id}/device-signals", new { deviceId = device, eventType = "Login", occurredAt = L(D, "08:00:00") });
        await (await As("officer")).PostOk("/api/governance/entity-resolution/run");
        var entityRule = await Rule(new { subject = "Entity", entityMinConfidence = 0.6m, window = new { type = "Calendar", unit = "Day" }, condition = Sum(null, "gte", 5000, "in") }, "High");
        await Send(new[] { T("W-F", p.W, 2000, L(D, "09:00:00")), T("W-F", w2, 2000, L(D, "10:00:00")), T("W-F", w3, 2000, L(D, "11:00:00")), T("W-F", single.W, 2000, L(D, "09:00:00")),
            T("W-F", d1.W, 2000, L(D, "09:00:00")), T("W-F", d2.W, 2000, L(D, "10:00:00")), T("W-F", d3.W, 2000, L(D, "11:00:00")) });
        await Run(rule, L(D, "00:00:00"), L(D, "23:59:59"));
        Result("TM-STR-04-01", (await Alerts(rule, p.Id)).Count == 1, $"Same person, 3 wallets each receiving 2,000 → aggregated by resolved customer → {(await Alerts(rule, p.Id)).Count} person-level alert.");
        Result("TM-STR-04-02", (await Alerts(rule, single.Id)).Count == 0, $"Single wallet receives 2,000 → {(await Alerts(rule, single.Id)).Count} alerts.");
        var linkConf = await F.WithDb(db => db.Set<EntityLink>().Where(l => (l.CustomerAId == d1.Id || l.CustomerBId == d1.Id) && l.LinkType == "SharedDevice").Select(l => l.Confidence).FirstOrDefaultAsync());
        await Run(entityRule, L(D, "00:00:00"), L(D, "23:59:59"));
        var ea = await F.WithDb(db => db.Set<AmlAlert>().Where(a => a.RuleId == entityRule && (a.PrimaryCustomerId == d1.Id || a.PrimaryCustomerId == d2.Id || a.PrimaryCustomerId == d3.Id)).ToListAsync());
        var shows = ea.Count == 1 && ea[0].EvidenceJson.Contains("subjectContext") && ea[0].EvidenceJson.Contains("SharedDevice") && ea[0].EvidenceJson.Contains("Confidence");
        Result("TM-STR-04-03", shows, $"Three customer records linked by a shared device (entity link, confidence {linkConf}) each receiving 2,000 → entity-level rule (subject Entity, minimum confidence 0.6) aggregates 6,000 → {ea.Count} alert; members, link type and confidence in evidence: {shows}.");
        Done();
    }

    [Fact]
    public async Task STR_05_just_below_own_limit()
    {
        var rule = await Rule(new { window = new { type = "Rolling", days = 7 }, condition = Cnt(F_("txn.baseAmount", "between", new[] { 980m, 1000m }), "gte", 3, "out") });
        const int D = 14;
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        await Send(new[] { T(c1.W, "W-L", 990, L(D, "10:00:00")), T(c1.W, "W-L", 995, L(D, "10:00:00", 2)), T(c1.W, "W-L", 999, L(D, "10:00:00", 4)),
            T(c2.W, "W-L", 500, L(D, "10:00:00")), T(c2.W, "W-L", 500, L(D, "11:00:00")), T(c2.W, "W-L", 500, L(D, "12:00:00")),
            T(c3.W, "W-L", 999, L(D, "10:00:00")), T(c3.W, "W-L", 999, L(D, "11:00:00")) });
        await Run(rule, L(D, "00:00:00"), L(D, "00:00:00", 6));
        Result("TM-STR-05-01", (await Alerts(rule, c1.Id)).Count == 1, $"Limit 1,000 (98–100% band configured in the rule); 990/995/999 within 5 days → {(await Alerts(rule, c1.Id)).Count} alert. Note: a per-customer limit attribute cannot be referenced; the band is a rule parameter.");
        Result("TM-STR-05-02", (await Alerts(rule, c2.Id)).Count == 0, $"Transfers of 500 → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-STR-05-03", (await Alerts(rule, c3.Id)).Count == 0, $"Two transfers at 999 → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Done();
    }
}

[Collection("tm")]
public class RapidMovementFanTests : TmBase
{
    public RapidMovementFanTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact]
    public async Task RMV_01_pass_through()
    {
        var rule = await Rule(new { window = new { type = "Rolling", hours = 2 }, condition = new { aggregate = "rapidMovement", filter = F_("txn.baseAmount", "gte", 500), maxMinutes = 60, @operator = "gte", value = 90 } }, "High");
        const int D = 15;
        var c = new List<(long Id, string W)>();
        for (var i = 0; i < 5; i++) c.Add(await Cust());
        await Send(new[]
        {
            T("W-IN", c[0].W, 2000, L(D, "10:00:00")), T(c[0].W, "W-OUT", 1900, L(D, "10:25:00")),
            T("W-IN", c[1].W, 2000, L(D, "10:00:00")), T(c[1].W, "W-OUT", 1900, L(D, "11:01:00")),
            T("W-IN", c[2].W, 2000, L(D, "10:00:00")), T(c[2].W, "W-OUT", 1780, L(D, "10:25:00")),
            T("W-IN", c[3].W, 300, L(D, "10:00:00")), T(c[3].W, "W-OUT", 290, L(D, "10:25:00")),
            T("W-IN", c[4].W, 2000, L(D, "10:00:00")), T(c[4].W, "W-O1", 500, L(D, "10:10:00")), T(c[4].W, "W-O2", 500, L(D, "10:20:00")), T(c[4].W, "W-O3", 450, L(D, "10:30:00")), T(c[4].W, "W-O4", 450, L(D, "10:40:00")),
        });
        await Run(rule, L(D, "00:00:00"), L(D, "23:59:59"));
        var a0 = await Alerts(rule, c[0].Id);
        Result("TM-RMV-01-01", a0.Count == 1 && await TxnCount(a0[0].Id) == 2, $"Received 2,000 at 10:00, sent 1,900 at 10:25 → {a0.Count} alert; in/out pair linked in evidence.");
        Result("TM-RMV-01-02", (await Alerts(rule, c[1].Id)).Count == 0, $"Outgoing at 61 minutes → {(await Alerts(rule, c[1].Id)).Count} alerts.");
        Result("TM-RMV-01-03", (await Alerts(rule, c[2].Id)).Count == 0, $"Outgoing 89% of incoming → {(await Alerts(rule, c[2].Id)).Count} alerts.");
        Result("TM-RMV-01-04", (await Alerts(rule, c[3].Id)).Count == 0, $"Received 300 (< minimum 500), sent 290 → {(await Alerts(rule, c[3].Id)).Count} alerts.");
        Result("TM-RMV-01-05", (await Alerts(rule, c[4].Id)).Count == 1, $"Outflow split into 4 transfers totalling 95% within 60 min → aggregated → {(await Alerts(rule, c[4].Id)).Count} alert.");
        Done();
    }

    [Fact]
    public async Task RMV_03_incoming_then_cash_out()
    {
        var rule = await Rule(new
        {
            window = new { type = "Rolling", hours = 3 },
            condition = new { aggregate = "rapidMovement", filter = F_("txn.type", "in", new[] { "P2P", "BankTransfer" }), maxMinutes = 120, @operator = "gte", value = 80 },
            action = new { indicators = new[] { new { label = "Geographic: cash-out abroad", condition = Cnt(And(F_("txn.channel", "eq", "ATM"), F_("txn.sourceCountry", "neq", "JO")), "gte", 1, "out") } } }
        }, "High");
        const int D = 16;
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        await Send(new[]
        {
            T("W-IN", c1.W, 1500, L(D, "10:00:00")), T(c1.W, "AGT-TILL", 1400, L(D, "10:40:00"), "CashOut", channel: "Agent"),
            T("W-IN", c2.W, 1500, L(D, "10:00:00")), T(c2.W, "AGT-TILL", 1400, L(D, "10:00:00", 3), "CashOut", channel: "Agent"),
            T("W-IN", c3.W, 1500, L(D, "10:00:00")), T(c3.W, "ATM-IST", 1400, L(D, "10:40:00"), "CashOut", channel: "ATM", src: "TR", dst: "TR"),
        });
        await Run(rule, L(D, "00:00:00"), L(D, "00:00:00", 4));
        Result("TM-RMV-03-01", (await Alerts(rule, c1.Id)).Count == 1, $"Received 1,500 transfer, cashed out 1,400 at agent 40 min later → {(await Alerts(rule, c1.Id)).Count} alert. Note: the outgoing leg cannot be restricted to cash-out type in the DSL (any outflow counts).");
        Result("TM-RMV-03-02", (await Alerts(rule, c2.Id)).Count == 0, $"Withdrawal after 3 days → {(await Alerts(rule, c2.Id)).Count} alerts.");
        var a3 = await Alerts(rule, c3.Id);
        const string flag = "\"indicators\":[\"Geographic: cash-out abroad\"]";
        var flag3 = a3.Count == 1 && a3[0].EvidenceJson.Contains(flag) && !(await Alerts(rule, c1.Id))[0].EvidenceJson.Contains(flag);
        Result("TM-RMV-03-03", flag3, $"Cash-out at ATM abroad → {a3.Count} alert carrying the indicator flag 'Geographic: cash-out abroad' (rule action.indicators); the domestic case has no flag: {flag3}.");
        Done();
    }

    [Fact]
    public async Task RMV_05_loan_disbursement_moved_out()
    {
        var rule = await Rule(new { window = new { type = "Rolling", hours = 25 }, condition = new { aggregate = "rapidMovement", filter = F_("txn.type", "eq", "LoanDisbursement"), maxMinutes = 1440, @operator = "gte", value = 90 } });
        const int D = 17;
        var c1 = await Cust(); var c2 = await Cust();
        await Send(new object[] { T("W-LENDER", c1.W, 500, L(D, "09:00:00"), "LoanDisbursement"), T(c1.W, "W-THIRD", 480, L(D, "11:00:00")),
            T("W-LENDER", c2.W, 500, L(D, "09:00:00"), "LoanDisbursement") }
            .Concat(Enumerable.Range(1, 10).Select(i => T(c2.W, "W-MERCHANT", 45, L(D, "12:00:00", i), "MerchantPayment"))));
        await Run(rule, L(D, "00:00:00"), L(D, "00:00:00", 12));
        Result("TM-RMV-05-01", (await Alerts(rule, c1.Id)).Count == 1, $"Nano-loan 500 disbursed, 480 sent to third party after 2 h (96%) → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-RMV-05-02", (await Alerts(rule, c2.Id)).Count == 0, $"Disbursement spent gradually at merchants over 10 days → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task FAN_01_fan_in()
    {
        await Field("TM_COLLECTION_APPROVED", "Boolean", "Corporate");
        var rule = await Rule(new
        {
            dedupKey = new[] { "rule", "version", "subject" }, window = new { type = "Rolling", days = 7 },
            condition = Or(And(F_("field:TM_COLLECTION_APPROVED", "neq", true), new { aggregate = "distinctCounterparties", direction = "in", @operator = "gte", value = 20 }),
                And(F_("field:TM_COLLECTION_APPROVED", "eq", true), new { aggregate = "distinctCounterparties", direction = "in", @operator = "gte", value = 500 })),
            action = new { indicators = new[] { new { label = "Senders are new accounts (< 7 days)", severity = "Critical", condition = Cnt(F_("counterparty.accountAgeDays", "lt", 7), "gte", 10, "in") } } }
        }, "High");
        const int D = 18;
        var c1 = await Cust(); var c2 = await Cust(); var charity = await Cust("Corporate", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "87654321", ["TM_COLLECTION_APPROVED"] = true }); var c4 = await Cust();
        await Backdate(charity.Id);
        var newSenders = new List<(long Id, string W)>();
        for (var i = 0; i < 22; i++) newSenders.Add(await Cust(registrationDate: L(D, "00:00:00").AddDays(-2).ToString("yyyy-MM-dd")));
        await Send(Enumerable.Range(0, 22).Select(i => T($"W-FI{i}", c1.W, 30, L(D, "09:00:00").AddHours(i * 5)))
            .Concat(Enumerable.Range(0, 19).Select(i => T($"W-FI{i}", c2.W, 30, L(D, "09:00:00").AddHours(i * 5))))
            .Concat(Enumerable.Range(0, 22).Select(i => T($"W-FI{i}", charity.W, 30, L(D, "09:00:00").AddHours(i * 5))))
            .Concat(newSenders.Select((x, i) => T(x.W, c4.W, 30, L(D, "09:00:00").AddHours(i * 5)))));
        await Run(rule, L(D, "00:00:00"), L(D, "00:00:00", 7));
        Result("TM-FAN-01-01", (await Alerts(rule, c1.Id)).Count == 1, $"22 distinct senders within 5 days → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-FAN-01-02", (await Alerts(rule, c2.Id)).Count == 0, $"19 senders → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-FAN-01-03", (await Alerts(rule, charity.Id)).Count == 0, $"Registered charity collection wallet with approved profile (dynamic field TM_COLLECTION_APPROVED) and 22 senders → {(await Alerts(rule, charity.Id)).Count} alerts; the approved-profile threshold (500 senders) is applied by a second rule branch. Note: the threshold is maintained in the rule, not stored on the customer's profile.");
        var a4 = await Alerts(rule, c4.Id);
        var sev1 = (await Alerts(rule, c1.Id)).FirstOrDefault()?.Severity;
        Result("TM-FAN-01-04", a4.Count == 1 && a4[0].Severity == "Critical" && sev1 == "High",
            $"22 senders registered 2 days earlier → alert severity raised High → {a4.FirstOrDefault()?.Severity} by the indicator 'Senders are new accounts (< 7 days)' (counterparty.accountAgeDays < 7 for ≥ 10 senders); established senders stay {sev1}.");
        Done();
    }

    [Fact]
    public async Task FAN_02_fan_out()
    {
        var rule = await Rule(new { scope = new { customerTypes = new[] { "Individual" } }, dedupKey = new[] { "rule", "version", "subject" }, window = new { type = "Rolling", days = 7 }, condition = new { aggregate = "distinctCounterparties", direction = "out", filter = F_("txn.type", "neq", "Payroll"), @operator = "gte", value = 20 },
            action = new { indicators = new[] { new { label = "Geographic: beneficiaries in high-risk area", condition = Cnt(F_("txn.destinationCountry", "in", new[] { "SY", "YE", "SO" }), "gte", 20, "out") } } } }, "High");
        const int D = 19;
        var c1 = await Cust(); var c2 = await Cust(); var corp = await Cust("Corporate", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "11223344" }); var c4 = await Cust();
        await Send(Enumerable.Range(0, 25).Select(i => T(c1.W, $"W-FO{i}", 30, L(D, "09:00:00").AddHours(i * 2)))
            .Concat(Enumerable.Range(0, 19).Select(i => T(c2.W, $"W-FO{i}", 30, L(D, "09:00:00").AddHours(i * 2))))
            .Concat(Enumerable.Range(0, 25).Select(i => T(corp.W, $"W-EMP{i}", 400, L(D, "09:00:00").AddMinutes(i), "Payroll")))
            .Concat(Enumerable.Range(0, 25).Select(i => T(c4.W, $"W-HR{i}", 30, L(D, "09:00:00").AddHours(i * 2), dst: "SY"))));
        await Run(rule, L(D, "00:00:00"), L(D, "00:00:00", 7));
        Result("TM-FAN-02-01", (await Alerts(rule, c1.Id)).Count == 1, $"25 distinct beneficiaries within ~2 days → {(await Alerts(rule, c1.Id)).Count} alert (dedupKey rule/version/subject; with the default per-day key the pattern spanning two local days produced one alert per day).");
        Result("TM-FAN-02-02", (await Alerts(rule, c2.Id)).Count == 0, $"19 beneficiaries → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-FAN-02-03", (await Alerts(rule, corp.Id)).Count == 0, $"Corporate payroll to 25 employees excluded by customer type and Payroll sub-type → {(await Alerts(rule, corp.Id)).Count} alerts.");
        var a24 = await Alerts(rule, c4.Id);
        const string geo = "\"indicators\":[\"Geographic: beneficiaries in high-risk area\"]";
        Result("TM-FAN-02-04", a24.Count == 1 && a24[0].EvidenceJson.Contains(geo) && !(await Alerts(rule, c1.Id))[0].EvidenceJson.Contains(geo), $"Beneficiaries all in a high-risk area → {a24.Count} alert with the geographic indicator flag in evidence.");
        Done();
    }

    [Fact]
    public async Task FAN_04_fan_in_then_fan_out()
    {
        var rule = await Rule(new { window = new { type = "Rolling", hours = 48 }, condition = And(new { aggregate = "distinctCounterparties", direction = "in", @operator = "gte", value = 10 }, new { aggregate = "distinctCounterparties", direction = "out", @operator = "gte", value = 10 }) }, "High");
        const int D = 20;
        var c1 = await Cust(); var c2 = await Cust();
        await Send(Enumerable.Range(0, 12).Select(i => T($"W-CI{i}", c1.W, 100, L(D, "08:00:00").AddHours(i)))
            .Concat(Enumerable.Range(0, 11).Select(i => T(c1.W, $"W-CO{i}", 100, L(D, "20:00:00").AddHours(i))))
            .Concat(Enumerable.Range(0, 12).Select(i => T($"W-CI{i}", c2.W, 100, L(D, "08:00:00").AddHours(i)))));
        await Run(rule, L(D, "00:00:00"), L(D, "00:00:00", 3));
        Result("TM-FAN-04-01", (await Alerts(rule, c1.Id)).Count == 1, $"12 senders in, then 11 receivers out within 30 h → {(await Alerts(rule, c1.Id)).Count} collector/distributor alert (both legs required in the 48 h window; ordering of the legs is not checked by the DSL).");
        Result("TM-FAN-04-02", (await Alerts(rule, c2.Id)).Count == 0, $"Fan-in only → {(await Alerts(rule, c2.Id)).Count} alerts from this rule.");
        Done();
    }
}
