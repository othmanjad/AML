using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.TransactionMonitoring;

[Collection("tm")]
public class GeographicTests : TmBase
{
    public GeographicTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact]
    public async Task GEO_03_high_risk_jurisdiction()
    {
        var v1 = new { window = new { type = "Calendar", unit = "Day" }, condition = F_("txn.destinationCountry", "in", new[] { "IR", "KP", "MM" }) };
        var rule = await Rule(v1, "High");
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        await Send(new[]
        {
            T(c1.W, "W-GEO3-IR", 500, At(2025, 4, 2, "10:00:00"), "Remittance", dst: "IR"),
            T(c2.W, "W-GEO3-TR", 500, At(2025, 4, 2, "10:00:00"), "Remittance", dst: "TR"),
            T(c3.W, "W-GEO3-SO", 500, At(2025, 4, 1, "10:00:00"), "Remittance", dst: "SO")
        });
        await Run(rule, At(2025, 4, 1, "00:00:00"), At(2025, 4, 3, "00:00:00"));
        var a1 = await Alerts(rule, c1.Id);
        var ev = a1.FirstOrDefault()?.EvidenceJson ?? "";
        Result("TM-GEO-03-01", a1.Count == 1 && ev.Contains("\"version\":1") && ev.Contains("\"IR\"") && ev.Contains("definitionHash"),
            $"Remittance to IR → {a1.Count} alert; evidence holds the list as part of rule version v1 with its definition hash (list version = rule version; there is no separate versioned country-list object).");
        Result("TM-GEO-03-02", (await Alerts(rule, c2.Id)).Count == 0, $"Remittance to TR → {(await Alerts(rule, c2.Id)).Count} alerts.");
        // List updated "today" (new rule version adds SO); the transaction was made "yesterday" under the old list.
        await NewVersion(rule, new { window = new { type = "Calendar", unit = "Day" }, condition = F_("txn.destinationCountry", "in", new[] { "IR", "KP", "MM", "SO" }) });
        await Run(rule, At(2025, 4, 1, "00:00:00"), At(2025, 4, 3, "00:00:00"));
        var a3 = await Alerts(rule, c3.Id);
        Result("TM-GEO-03-03", a3.Count == 0,
            a3.Count == 0 ? "List updated today (rule v2 adds SO); re-run over yesterday evaluated the earlier transaction with v1, the list version in force at transaction time → 0 alerts (run audit lists the version per period)." :
            $"Re-run after SO was added (rule v2) raised {a3.Count} alert on a transaction made before the list change. DEFECT: executions always use the currently active version; list/rule version effective at transaction time is not applied.");
        Done();
    }

    [Fact]
    public async Task GEO_04_sanctioned_country()
    {
        var cond = Or(F_("txn.destinationCountry", "in", new[] { "KP", "CU" }), F_("txn.remarks", "contains", "north korea"), F_("txn.remarks", "contains", "كوريا الشمالية"));
        await RealtimeRule("TMGEO04RT", new { realtimeCapable = true, window = new { type = "Calendar", unit = "Day" }, condition = cond }, "Critical");
        var batch = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = cond }, "Critical");
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        var d1 = await Decide(Rt(c1.W, 300, "Remittance", dst: "KP"));
        var d2 = await Decide(Rt(c2.W, 300, "Remittance", dst: "CN"));
        var d3 = await Decide(Rt(c3.W, 300, "Remittance", dst: "AE", remarks: "payment for goods shipped to North Korea"));
        await Send(new[]
        {
            T(c1.W, "W-GEO4-1", 300, At(2025, 4, 5, "10:00:00"), "Remittance", dst: "KP"),
            T(c2.W, "W-GEO4-2", 300, At(2025, 4, 5, "10:00:00"), "Remittance", dst: "CN"),
            T(c3.W, "W-GEO4-3", 300, At(2025, 4, 5, "10:00:00"), "Remittance", dst: "AE", remarks: "payment for goods shipped to North Korea")
        });
        await Run(batch, At(2025, 4, 5, "00:00:00"), At(2025, 4, 6, "00:00:00"));
        var a1 = await Alerts(batch, c1.Id);
        Result("TM-GEO-04-01", d1["decision"]!.GetValue<string>() == "HoldForReview" && a1.Count == 1 && a1[0].Severity == "Critical",
            $"Real-time decision {d1["decision"]} ({d1["reasonCodes"]!.ToJsonString()}); batch rule alert severity {a1.FirstOrDefault()?.Severity}. Note: the real-time hold and the batch alert are produced by two rules on the same condition.");
        var a2 = await Alerts(batch, c2.Id);
        Result("TM-GEO-04-02", d2["decision"]!.GetValue<string>() == "Allow" && a2.Count == 0, $"Transfer to CN → decision {d2["decision"]}, {a2.Count} alerts.");
        var a3 = await Alerts(batch, c3.Id);
        Result("TM-GEO-04-03", d3["decision"]!.GetValue<string>() == "HoldForReview" && a3.Count == 1,
            $"Country only in remarks → real-time {d3["decision"]}, batch {a3.Count} alert (remarks 'contains' keyword conditions, EN/AR, configured in the rule).");
        Done();
    }

    [Fact]
    public async Task GEO_05_location_mismatch()
    {
        await Field("TM_DOMESTIC_ONLY", "Boolean");
        await Field("TM_TRAVEL_EXCEPTION", "Boolean");
        var rule = await Rule(new
        {
            dedupKey = new[] { "rule", "version", "subject" }, window = new { type = "Rolling", days = 7 },
            condition = And(F_("field:TM_DOMESTIC_ONLY", "eq", true), F_("field:TM_TRAVEL_EXCEPTION", "neq", true), Cnt(F_("txn.sourceCountry", "neq", "JO"), "gte", 5, "out"))
        });
        var c1 = await Cust(attributes: new Dictionary<string, object> { ["OCCUPATION"] = "EMPLOYEE", ["SOURCE_OF_INCOME"] = "SALARY", ["TM_DOMESTIC_ONLY"] = true });
        var c2 = await Cust(attributes: new Dictionary<string, object> { ["OCCUPATION"] = "EMPLOYEE", ["SOURCE_OF_INCOME"] = "SALARY", ["TM_DOMESTIC_ONLY"] = true, ["TM_TRAVEL_EXCEPTION"] = true });
        await Backdate(c1.Id, c2.Id);
        await Send(new[] { c1, c2 }.SelectMany(c => Enumerable.Range(0, 6).Select(i => T(c.W, $"ATM-TR-{i}", 100, At(2025, 4, 8, "10:00:00").AddDays(i), "Withdrawal", channel: "ATM", src: "TR", dst: "TR"))));
        await Run(rule, At(2025, 4, 8, "00:00:00"), At(2025, 4, 16, "00:00:00"));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-GEO-05-01", a1.Count >= 1, $"Domestic-only Jordanian resident, 6 withdrawals in TR within 6 days → {a1.Count} alert(s). Note: 'different from residence' is configured as sourceCountry ≠ JO (rules cannot compare a transaction field with the customer's residence field).");
        var a2 = await Alerts(rule, c2.Id);
        Result("TM-GEO-05-02", a2.Count == 0, $"Customer with TM_TRAVEL_EXCEPTION=true (dynamic profile field) → {a2.Count} alerts; exception applied by the rule condition.");
        Done();
    }
}

[Collection("tm")]
public class DeviceTests : TmBase
{
    public DeviceTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact]
    public async Task DEV_01_many_customers_on_device()
    {
        var rule = await Rule(new { subject = "Device", dedupKey = new[] { "rule", "version", "subject" }, window = new { type = "Rolling", days = 30 }, condition = new { aggregate = "customersPerDevice", @operator = "gte", value = 5 } }, "High");
        var d1 = "DEV-A-" + Guid.NewGuid().ToString("N")[..8]; var d2 = "DEV-B-" + Guid.NewGuid().ToString("N")[..8]; var d3 = "DEV-C-" + Guid.NewGuid().ToString("N")[..8];
        var txns = new List<object>();
        for (var i = 0; i < 6; i++) { var c = await Cust(); txns.Add(T(c.W, "W-DV1", 20, At(2025, 4, 20, "10:00:00").AddDays(i * 2), device: d1)); }
        for (var i = 0; i < 4; i++) { var c = await Cust(); txns.Add(T(c.W, "W-DV2", 20, At(2025, 4, 20, "10:00:00").AddDays(i * 2), device: d2)); }
        for (var i = 0; i < 3; i++) { var c = await Cust(name: "Tm Family Member " + i); txns.Add(T(c.W, "W-DV3", 20, At(2025, 4, 20, "10:00:00").AddDays(i), device: d3)); }
        await Send(txns);
        await Run(rule, At(2025, 4, 19, "00:00:00"), At(2025, 5, 10, "00:00:00"));
        var a1 = await AlertsBySubject(rule, "device:" + d1);
        Result("TM-DEV-01-01", a1.Count == 1 && a1[0].EvidenceJson!.Contains("customersPerDevice.customers"),
            $"6 customers on one device within 10 days → {a1.Count} device alert; evidence lists the customer IDs (customersPerDevice.customers).");
        Result("TM-DEV-01-02", (await AlertsBySubject(rule, "device:" + d2)).Count == 0, $"4 customers → {(await AlertsBySubject(rule, "device:" + d2)).Count} alerts.");
        Result("TM-DEV-01-03", (await AlertsBySubject(rule, "device:" + d3)).Count == 0, $"Shared family device with 3 customers → {(await AlertsBySubject(rule, "device:" + d3)).Count} alerts (below threshold).");
        Done();
    }
}

[Collection("tm")]
public class BehaviourTests : TmBase
{
    public BehaviourTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact]
    public async Task BEH_01_dormant_reactivation()
    {
        var types = new[] { "P2P", "CashIn", "CashOut", "Withdrawal", "BillPayment", "Remittance", "MerchantPayment", "Salary" };
        var rule = await Rule(new
        {
            scope = new { transactionTypes = types }, lookbackDays = 400, window = new { type = "Rolling", days = 7 },
            condition = And(new { aggregate = "dormancyDays", @operator = "gte", value = 180 }, Sum(null, "gte", 2000))
        }, "High");
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust(); var c4 = await Cust();
        await Send(new[]
        {
            T(c1.W, "W-B1", 50, At(2024, 3, 1, "10:00:00")),
            T("W-B1-IN", c1.W, 3000, At(2024, 11, 1, "10:00:00")), T(c1.W, "W-B1-OUT", 3000, At(2024, 11, 3, "10:00:00")),
            T(c2.W, "W-B2", 50, At(2024, 5, 6, "10:00:00")),
            T("W-B2-IN", c2.W, 3000, At(2024, 11, 1, "10:00:00")),
            T(c3.W, "W-B3", 50, At(2024, 3, 1, "10:00:00")),
            T(c3.W, "BILLER-1", 20, At(2024, 11, 1, "10:00:00"), "BillPayment"),
            T(c4.W, "W-B4", 50, At(2024, 3, 1, "10:00:00")),
            T(c4.W, "FEES", 1, At(2024, 6, 1, "02:00:00"), "Fee", channel: "System"), T(c4.W, "FEES", 1, At(2024, 8, 1, "02:00:00"), "Fee", channel: "System"),
            T("W-B4-IN", c4.W, 3000, At(2024, 11, 1, "10:00:00"))
        });
        await Run(rule, At(2024, 10, 30, "00:00:00"), At(2024, 11, 8, "00:00:00"));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-BEH-01-01", a1.Count >= 1, $"Dormant ~245 days, then receives 3,000 and sends 3,000 within 2 days → {a1.Count} alert(s). Note: dormancy is measured at the reactivating transaction against the previous eligible transaction.");
        Result("TM-BEH-01-02", (await Alerts(rule, c2.Id)).Count == 0, $"Dormant 179 days → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-BEH-01-03", (await Alerts(rule, c3.Id)).Count == 0, $"Dormant then 20 JOD bill payment → {(await Alerts(rule, c3.Id)).Count} alerts.");
        var a4 = await Alerts(rule, c4.Id);
        Result("TM-BEH-01-04", a4.Count >= 1, $"System fee records during dormancy excluded by the rule's transaction-type scope → dormancy still ≥180 days and {a4.Count} alert raised.");
        Done();
    }

    [Fact]
    public async Task BEH_03_night_activity()
    {
        var night = And(F_("txn.localHour", "gte", 0), F_("txn.localHour", "lt", 5));
        // "when baseline has none": no night-time activity in the customer's own previous 60 days.
        var rule = await Rule(new
        {
            lookbackDays = 60, window = new { type = "Calendar", unit = "Day" },
            condition = And(Cnt(night, "gte", 5, "out"), Sum(night, "gte", 1000, "out"), new { aggregate = "historyCount", filter = night, direction = "out", @operator = "eq", value = 0 })
        }, "Low");
        var c1 = await Cust(); var c2 = await Cust();
        var txns = new List<object>();
        foreach (var c in new[] { c1, c2 }) txns.AddRange(Enumerable.Range(0, 6).Select(i => T(c.W, $"W-N{i}", 250, At(2025, 5, 3, "01:00:00").AddMinutes(i * 20))));
        txns.AddRange(Enumerable.Range(1, 30).SelectMany(d => Enumerable.Range(0, 2).Select(i => T(c2.W, $"W-NS{i}", 40, At(2025, 4, 2, "02:00:00").AddDays(d).AddMinutes(i * 30)))));
        await Send(txns);
        await Run(rule, At(2025, 5, 3, "00:00:00"), At(2025, 5, 4, "00:00:00"));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-BEH-03-01", a1.Count == 1, $"6 transfers totalling 1,500 between 01:00 and 02:40 local → {a1.Count} alert (no night-time activity in the previous 60 days).");
        var a2 = await Alerts(rule, c2.Id);
        Result("TM-BEH-03-02", a2.Count == 0,
            a2.Count == 0 ? "Night-shift customer with 30 days of nightly history → 0 alerts; baseline condition historyCount(night, 60 days) = 0 not met." : $"Night-shift customer with 30 days of nightly history → {a2.Count} alert. GAP: no per-customer baseline (historical time-of-day profile) can suppress the alert.");
        Done();
    }

    [Fact]
    public async Task BEH_04_occupation_mismatch()
    {
        var rule = await Rule(new { dedupKey = new[] { "rule", "version", "subject", "window" }, window = new { type = "Calendar", unit = "Month" }, condition = And(F_("field:OCCUPATION", "in", new[] { "STUDENT", "UNEMPLOYED", "HOUSEWIFE" }), Sum(null, "gte", 3000)) });
        var c1 = await Cust(attributes: new Dictionary<string, object> { ["OCCUPATION"] = "STUDENT", ["SOURCE_OF_INCOME"] = "SALARY" });
        var c2 = await Cust(attributes: new Dictionary<string, object> { ["OCCUPATION"] = "STUDENT", ["SOURCE_OF_INCOME"] = "SALARY" });
        var c3 = await Cust(attributes: new Dictionary<string, object> { ["SOURCE_OF_INCOME"] = "SALARY" });
        await Backdate(c1.Id, c2.Id, c3.Id);
        var dqBefore = await F.WithDb(db => db.Set<DataQualityIssue>().CountAsync(d => d.EntityId == c3.Id.ToString()));
        await Send(Enumerable.Range(0, 8).Select(i => T($"W-S{i}", c1.W, 500, At(2025, 6, 2, "10:00:00").AddDays(i * 3)))
            .Append(T("W-PARENT", c2.W, 300, At(2025, 6, 2, "10:00:00")))
            .Concat(Enumerable.Range(0, 8).Select(i => T($"W-S{i}", c3.W, 500, At(2025, 6, 2, "10:00:00").AddDays(i * 3)))));
        await Run(rule, At(2025, 6, 1, "00:00:00"), At(2025, 7, 1, "00:00:00"));
        Result("TM-BEH-04-01", (await Alerts(rule, c1.Id)).Count >= 1, $"Student receives 4,000 in the month from 8 senders → {(await Alerts(rule, c1.Id)).Count} alert(s).");
        Result("TM-BEH-04-02", (await Alerts(rule, c2.Id)).Count == 0, $"Student receives 300 from parent → {(await Alerts(rule, c2.Id)).Count} alerts.");
        var a3 = await Alerts(rule, c3.Id);
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().CountAsync(d => d.EntityId == c3.Id.ToString())) - dqBefore;
        Result("TM-BEH-04-03", dq > 0,
            $"Occupation missing, 4,000 received → {a3.Count} alerts and {dq} new data-quality items from the rule run (RuleInputMissing: condition not met, result incomplete).");
        Done();
    }

    [Fact]
    public async Task BEH_06_personal_wallet_business_use()
    {
        var rule = await Rule(new { scope = new { customerTypes = new[] { "Individual" } }, dedupKey = new[] { "rule", "version", "subject", "window" }, window = new { type = "Calendar", unit = "Month" }, condition = And(new { aggregate = "distinctCounterparties", direction = "in", @operator = "gte", value = 50 }, Sum(null, "gte", 5000, "in")) });
        var c1 = await Cust(); var m = await Cust("Merchant", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "55667788" });
        await Send(new[] { c1, m }.SelectMany(c => Enumerable.Range(0, 60).Select(i => T($"W-PAYER{i % 55}", c.W, 100, At(2025, 6, 2, "09:00:00").AddHours(i * 6)))));
        await Run(rule, At(2025, 6, 1, "00:00:00"), At(2025, 7, 1, "00:00:00"));
        Result("TM-BEH-06-01", (await Alerts(rule, c1.Id)).Count >= 1, $"Individual receives 60 payments from 55 payers totalling 6,000 → {(await Alerts(rule, c1.Id)).Count} alert(s) (dedupKey rule/version/subject/window: one alert per calendar month).");
        Result("TM-BEH-06-02", (await Alerts(rule, m.Id)).Count == 0, $"Registered merchant with the same pattern → {(await Alerts(rule, m.Id)).Count} alerts (rule scoped to Individual customers).");
        Done();
    }
}

[Collection("tm")]
public class CashTests : TmBase
{
    public CashTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact]
    public async Task CSH_01_high_cash_in()
    {
        var rule = await Rule(new { dedupKey = new[] { "rule", "version", "subject", "window" }, window = new { type = "Calendar", unit = "Month" }, condition = Sum(F_("txn.type", "eq", "CashIn"), "gte", 10000, "in") });
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        await Send(Enumerable.Range(0, 12).Select(i => T("W-AGENT-TILL", c1.W, i < 11 ? 900 : 1100, At(2025, 7, 2, "10:00:00").AddDays(i * 2), "CashIn", channel: "Agent"))
            .Concat(Enumerable.Range(0, 9).Select(i => T("W-AGENT-TILL", c2.W, i < 8 ? 1111 : 1111, At(2025, 7, 2, "10:00:00").AddDays(i * 2), "CashIn", channel: "Agent")))
            .Concat(Enumerable.Range(0, 11).Select(i => T("W-EMPLOYER", c3.W, 1000, At(2025, 7, 2, "10:00:00").AddDays(i * 2), "Salary", channel: "Bank"))));
        await Run(rule, At(2025, 7, 1, "00:00:00"), At(2025, 8, 1, "00:00:00"));
        Result("TM-CSH-01-01", (await Alerts(rule, c1.Id)).Count >= 1, $"12 cash-ins totalling 11,000 in July → {(await Alerts(rule, c1.Id)).Count} alert(s).");
        Result("TM-CSH-01-02", (await Alerts(rule, c2.Id)).Count == 0, $"Cash-ins totalling 9,999 → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-CSH-01-03", (await Alerts(rule, c3.Id)).Count == 0, $"11,000 via salary transfers → {(await Alerts(rule, c3.Id)).Count} alerts (not cash).");
        Done();
    }

    [Fact]
    public async Task CSH_02_cash_in_then_remit_abroad()
    {
        var rule = await Rule(new
        {
            window = new { type = "Rolling", hours = 24 },
            condition = And(new { aggregate = "rapidMovement", filter = And(F_("txn.type", "eq", "CashIn"), F_("txn.amount", "gte", 1000)), maxMinutes = 1440, @operator = "gte", value = 80 },
                Cnt(F_("txn.isCrossBorder", "eq", true), "gte", 1, "out"))
        }, "High");
        var c1 = await Cust(); var c2 = await Cust();
        await Send(new[]
        {
            T("W-AG-C2", c1.W, 2000, At(2025, 7, 5, "09:00:00"), "CashIn", channel: "Agent"), T(c1.W, "W-ABROAD", 1800, At(2025, 7, 5, "12:00:00"), "Remittance", dst: "EG"),
            T("W-AG-C2", c2.W, 2000, At(2025, 7, 5, "09:00:00"), "CashIn", channel: "Agent"), T(c2.W, "W-ABROAD", 1800, At(2025, 7, 10, "12:00:00"), "Remittance", dst: "EG")
        });
        await Run(rule, At(2025, 7, 5, "00:00:00"), At(2025, 7, 12, "00:00:00"));
        Result("TM-CSH-02-01", (await Alerts(rule, c1.Id)).Count >= 1, $"Cash-in 2,000 then 1,800 remitted abroad 3 h later (90%) → {(await Alerts(rule, c1.Id)).Count} alert(s). Note: the 80% is measured over all outflows within 24 h; a second condition requires a cross-border outflow in the window.");
        Result("TM-CSH-02-02", (await Alerts(rule, c2.Id)).Count == 0, $"Remittance 5 days later → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task CSH_04_low_denomination_cash_in()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = Cnt(And(F_("txn.type", "eq", "CashIn"), F_("txn.amount", "lte", 50)), "gte", 10, "in") }, "Low");
        var c1 = await Cust(); var c2 = await Cust();
        await Send(Enumerable.Range(0, 12).Select(i => T("W-AG-C4", c1.W, 20, At(2025, 7, 15, "09:00:00").AddMinutes(i * 15), "CashIn", channel: "Agent"))
            .Concat(Enumerable.Range(0, 3).Select(i => T("W-AG-C4", c2.W, 20, At(2025, 7, 15, "09:00:00").AddMinutes(i * 15), "CashIn", channel: "Agent"))));
        await Run(rule, At(2025, 7, 15, "00:00:00"), At(2025, 7, 16, "00:00:00"));
        Result("TM-CSH-04-01", (await Alerts(rule, c1.Id)).Count == 1, $"12 cash-ins of 20 at one agent in a day → {(await Alerts(rule, c1.Id)).Count} alert. Note: the '±1 identical amount' tolerance is not expressible; configured as count of cash-ins ≤ 50.");
        Result("TM-CSH-04-02", (await Alerts(rule, c2.Id)).Count == 0, $"3 cash-ins → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task CSH_05_many_atms()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = new { aggregate = "distinctCounterparties", direction = "out", filter = F_("txn.channel", "eq", "ATM"), @operator = "gte", value = 5 } });
        var c1 = await Cust(); var c2 = await Cust();
        await Send(Enumerable.Range(0, 6).Select(i => T(c1.W, $"ATM-AMM-{i:00}", 100, At(2025, 7, 18, "09:00:00").AddHours(i), "Withdrawal", channel: "ATM"))
            .Concat(Enumerable.Range(0, 4).Select(i => T(c2.W, $"ATM-AMM-{i % 2:00}", 100, At(2025, 7, 18, "09:00:00").AddHours(i), "Withdrawal", channel: "ATM"))));
        await Run(rule, At(2025, 7, 18, "00:00:00"), At(2025, 7, 19, "00:00:00"));
        Result("TM-CSH-05-01", (await Alerts(rule, c1.Id)).Count == 1, $"Withdrawals at 6 different ATMs in one day → {(await Alerts(rule, c1.Id)).Count} alert (ATM terminal = counterparty identifier).");
        Result("TM-CSH-05-02", (await Alerts(rule, c2.Id)).Count == 0, $"4 withdrawals at 2 ATMs → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }
}

[Collection("tm")]
public class AgentTests : TmBase
{
    public AgentTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private async Task<(long Id, string W, string Ext)> Agent(object? attributes = null)
    {
        var ext = U("AGT");
        var a = await Cust("Agent", attributes: attributes, externalId: ext);
        return (a.Id, a.W, ext);
    }

    private async Task Register(long agentId, int n, DateTime day)
    {
        var api = await As("manager");
        for (var i = 0; i < n; i++)
            await api.PostOk("/api/customers", new
            {
                sourceSystem = "CORE", externalId = U("REG"), customerType = "Individual", nameLatin = "Tm Registered " + Guid.NewGuid().ToString("N")[..6], nationalityCountry = "JO", residenceCountry = "JO",
                region = "CENTRAL", attributes = FullKycAttributes, registrationDate = day.AddHours(i % 48).ToString("yyyy-MM-dd"), onboardingAgentId = agentId
            });
    }

    [Fact]
    public async Task AGT_03_many_registrations()
    {
        await Field("TM_AGENT_CAMPAIGN", "Boolean", "Agent");
        var rule = await Rule(new
        {
            subject = "Agent", window = new { type = "Calendar", unit = "Week" },
            condition = Or(And(F_("field:TM_AGENT_CAMPAIGN", "neq", true), new { aggregate = "newCustomers", @operator = "gte", value = 50 }),
                And(F_("field:TM_AGENT_CAMPAIGN", "eq", true), new { aggregate = "newCustomers", @operator = "gte", value = 150 }))
        });
        var a1 = await Agent(); var a2 = await Agent(); var a3 = await Agent(new Dictionary<string, object> { ["TM_AGENT_CAMPAIGN"] = true });
        var day = new DateTime(2025, 8, 12);
        await Register(a1.Id, 70, day); await Register(a2.Id, 20, day); await Register(a3.Id, 70, day);
        await Backdate(a1.Id, a2.Id, a3.Id);
        var c = await Cust();
        await Send(new[] { a1, a2, a3 }.Select(a => T("W-AG-REG-" + a.Ext, c.W, 50, At(2025, 8, 14, "12:00:00"), "CashIn", channel: "Agent", agent: a.Ext)));
        await Run(rule, At(2025, 8, 14, "00:00:00"), At(2025, 8, 15, "00:00:00"));
        Result("TM-AGT-03-01", (await Alerts(rule, a1.Id)).Count == 1, $"Agent registered 70 customers in the week → {(await Alerts(rule, a1.Id)).Count} agent alert.");
        Result("TM-AGT-03-02", (await Alerts(rule, a2.Id)).Count == 0, $"20 registrations → {(await Alerts(rule, a2.Id)).Count} alerts.");
        Result("TM-AGT-03-03", (await Alerts(rule, a3.Id)).Count == 0, $"Campus agent with approved campaign flag (dynamic field TM_AGENT_CAMPAIGN) and 70 registrations → {(await Alerts(rule, a3.Id)).Count} alerts; campaign threshold 150 applied by the rule.");
        Done();
    }

    [Fact]
    public async Task AGT_05_outside_hours()
    {
        var rule = await Rule(new { subject = "Agent", window = new { type = "Calendar", unit = "Week" }, condition = Cnt(Or(F_("txn.localHour", "lt", 8), F_("txn.localHour", "gte", 22)), "gte", 10) }, "Low");
        var a1 = await Agent(); var a2 = await Agent(); var c = await Cust();
        await Send(Enumerable.Range(0, 12).Select(i => T(c.W, a1.W, 50, At(2025, 8, 19, "02:00:00").AddMinutes(i * 10), "CashOut", channel: "Agent", agent: a1.Ext))
            .Concat(Enumerable.Range(0, 12).Select(i => T(c.W, a2.W, 50, At(2025, 8, 19, "10:00:00").AddMinutes(i * 10), "CashOut", channel: "Agent", agent: a2.Ext))));
        await Run(rule, At(2025, 8, 19, "00:00:00"), At(2025, 8, 20, "00:00:00"));
        Result("TM-AGT-05-01", (await Alerts(rule, a1.Id)).Count >= 1, $"12 cash-outs between 02:00 and 03:50 local → {(await Alerts(rule, a1.Id)).Count} agent alert(s).");
        Result("TM-AGT-05-02", (await Alerts(rule, a2.Id)).Count == 0, $"12 cash-outs within operating hours → {(await Alerts(rule, a2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task AGT_07_agent_split_transactions()
    {
        var filter = And(F_("txn.type", "eq", "CashOut"), F_("txn.channel", "eq", "Agent"), F_("txn.amount", "lt", 200));
        var agentRule = await Rule(new { subject = "Agent", dedupKey = new[] { "rule", "version", "subject" }, window = new { type = "Rolling", minutes = 15 }, condition = Cnt(filter, "gte", 4) }, "High");
        var custRule = await Rule(new { dedupKey = new[] { "rule", "version", "subject" }, window = new { type = "Rolling", minutes = 15 }, condition = Cnt(filter, "gte", 4, "out") }, "High");
        var a1 = await Agent(); var a2 = await Agent(); var c1 = await Cust(); var c2 = await Cust();
        await Send(Enumerable.Range(0, 5).Select(i => T(c1.W, a1.W, 190, At(2025, 8, 21, "11:00:00").AddMinutes(i * 2), "CashOut", channel: "Agent", agent: a1.Ext))
            .Append(T(c2.W, a2.W, 190, At(2025, 8, 21, "11:00:00"), "CashOut", channel: "Agent", agent: a2.Ext)));
        await Run(agentRule, At(2025, 8, 21, "00:00:00"), At(2025, 8, 22, "00:00:00"));
        await Run(custRule, At(2025, 8, 21, "00:00:00"), At(2025, 8, 22, "00:00:00"));
        var ag = await Alerts(agentRule, a1.Id); var cu = await Alerts(custRule, c1.Id);
        Result("TM-AGT-07-01", ag.Count == 1 && cu.Count == 1,
            $"Agent performs 5 cash-outs of 190 for C15 within 8 minutes → agent alert {ag.Count}, customer alert {cu.Count}. Note: configured as two rules (agent subject and customer subject); the agent rule cannot additionally group by 'same customer'.");
        Result("TM-AGT-07-02", (await Alerts(agentRule, a2.Id)).Count == 0 && (await Alerts(custRule, c2.Id)).Count == 0, "1 transaction → 0 agent and 0 customer alerts.");
        Done();
    }
}
