using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.TransactionMonitoring;

/// <summary>
/// Scenarios that became configurable with the extended rule DSL: account age and customer age, counterparty attributes,
/// device/session/contact-change signals, baselines over the customer's own history, distinct-value counts,
/// reciprocal transfers, consecutive-day bands, new-counterparty share, shared sender devices and entity links.
/// </summary>
[Collection("tm")]
public class ExtendedCapabilityTests : TmBase
{
    public ExtendedCapabilityTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private static readonly string[] Subject = { "rule", "version", "subject" };
    private static readonly string[] PerWindow = { "rule", "version", "subject", "window" };
    private static object Agg(string aggregate, string op, object value, string? direction = null, object? filter = null) => new { aggregate, filter, direction, @operator = op, value };

    [Fact]
    public async Task VEL_06_activity_right_after_registration()
    {
        var rule = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", hours = 24 }, condition = And(F_("customer.accountAgeDays", "lte", 1), Cnt(null, "gte", 10)) }, "High");
        var reg = "2025-11-03";
        var c1 = await Cust(registrationDate: reg); var c2 = await Cust(registrationDate: reg); var c3 = await Cust(registrationDate: "2023-11-03"); var c4 = await Cust();
        // Legacy/migrated record without a registration date (the API always sets one).
        await F.ExecSql($"UPDATE Customers SET RegistrationDate = NULL WHERE Id = {c4.Id}");
        var t = new List<object>();
        t.AddRange(Enumerable.Range(0, 10).Select(i => T(c1.W, $"W-V6-{i}", 20, At(2025, 11, 3, "10:30:00").AddMinutes(i * 45))));
        t.AddRange(Enumerable.Range(0, 9).Select(i => T(c2.W, $"W-V6-{i}", 20, At(2025, 11, 3, "10:00:00").AddMinutes(i * 10))));
        t.Add(T(c2.W, "W-V6-X", 20, At(2025, 11, 4, "10:01:00")));
        t.AddRange(Enumerable.Range(0, 10).Select(i => T(c3.W, $"W-V6-{i}", 20, At(2025, 11, 3, "10:30:00").AddMinutes(i * 45))));
        t.AddRange(Enumerable.Range(0, 10).Select(i => T(c4.W, $"W-V6-{i}", 20, At(2025, 11, 3, "10:30:00").AddMinutes(i * 45))));
        await Send(t);
        await Run(rule, At(2025, 11, 3, "00:00:00"), At(2025, 11, 6, "00:00:00"));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-VEL-06-01", a1.Count == 1 && a1[0].EvidenceJson.Contains("customer.accountAgeDays"), $"Wallet registered on the day, 10 transactions 10:30–17:15 → {a1.Count} alert; account age (customer.accountAgeDays = 0) in the matched conditions.");
        Result("TM-VEL-06-02", (await Alerts(rule, c2.Id)).Count == 0, $"9 transactions from 10:00 and the 10th at 24 h 01 m → {(await Alerts(rule, c2.Id)).Count} alerts (rolling 24 h window). Note: registration is stored as a date, so account age has day (not minute) precision.");
        Result("TM-VEL-06-03", (await Alerts(rule, c3.Id)).Count == 0, $"Two-year-old customer with 10 transactions in a day → {(await Alerts(rule, c3.Id)).Count} alerts.");
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().Where(d => d.IssueType == "RuleInputMissing" && d.EntityId == c4.Id.ToString() && d.Description.Contains("customer.accountAgeDays")).Select(d => d.Description).FirstOrDefaultAsync());
        Result("TM-VEL-06-04", dq is not null && (await Alerts(rule, c4.Id)).Count == 0, $"Registration date missing → no silent pass: condition not met and data-quality item \"{dq}\".");
        Done();
    }

    [Fact]
    public async Task VEL_08_many_channels()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = And(new { aggregate = "distinctCount", of = "channel", @operator = "gte", value = 4 }, Sum(null, "gte", 1000)) }, "Low");
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        string[] four = { "Mobile", "Agent", "ATM", "Web" };
        await Send(four.Select((ch, i) => T(c1.W, "W-V8", 375, At(2025, 11, 5, "09:00:00").AddHours(i * 2), channel: ch))
            .Concat(four.Select((ch, i) => T(c2.W, "W-V8", 75, At(2025, 11, 5, "09:00:00").AddHours(i * 2), channel: ch)))
            .Concat(four.Take(3).Select((ch, i) => T(c3.W, "W-V8", 700, At(2025, 11, 5, "09:00:00").AddHours(i * 2), channel: ch))));
        await Run(rule, At(2025, 11, 5, "00:00:00"), At(2025, 11, 6, "00:00:00"));
        Result("TM-VEL-08-01", (await Alerts(rule, c1.Id)).Count == 1, $"App, Agent, ATM and Web on one day totalling 1,500 → {(await Alerts(rule, c1.Id)).Count} alert (distinctCount of channel).");
        Result("TM-VEL-08-02", (await Alerts(rule, c2.Id)).Count == 0, $"4 channels, total 300 → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-VEL-08-03", (await Alerts(rule, c3.Id)).Count == 0, $"3 channels, total 2,100 → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task AMT_05_deviation_from_own_average()
    {
        // 12 full weeks of history (lookback 84 days) so the weekly average is exact for the boundary case.
        var rule = await Rule(new
        {
            dedupKey = Subject, lookbackDays = 84, window = new { type = "Rolling", days = 7 },
            applicability = F_("customer.accountAgeDays", "gte", 30), condition = Agg("historyRatio", "gte", 5, "out")
        });
        var c1 = await Cust(registrationDate: "2025-01-01"); var c2 = await Cust(registrationDate: "2025-01-01");
        var c3 = await Cust(registrationDate: "2025-11-01"); var c4 = await Cust(registrationDate: "2025-01-01");
        var w = At(2025, 11, 10, "10:00:00");
        var t = new List<object>();
        foreach (var c in new[] { c1, c2 }) t.AddRange(Enumerable.Range(1, 12).Select(k => T(c.W, "W-A5-H", 200, w.AddDays(-7 * k).AddHours(-1))));
        t.AddRange(new[] { T(c1.W, "W-A5", 400, w), T(c1.W, "W-A5", 400, w.AddDays(1)), T(c1.W, "W-A5", 400, w.AddDays(2)) });
        t.AddRange(new[] { T(c2.W, "W-A5", 400, w), T(c2.W, "W-A5", 300, w.AddDays(1)), T(c2.W, "W-A5", 300, w.AddDays(2)) });
        t.AddRange(new[] { T(c3.W, "W-A5", 2000, w), T(c4.W, "W-A5", 2000, w) });
        await Send(t);
        await Run(rule, w.AddHours(-1), w.AddDays(3));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-AMT-05-01", a1.Count == 1 && a1[0].EvidenceJson.Contains("historyRatio.historicalAverage"), $"Average weekly outflow 200 (12 weeks), this week 1,200 (6×) → {a1.Count} alert; baseline (historical average, history sum, lookback) in evidence.");
        Result("TM-AMT-05-02", (await Alerts(rule, c2.Id)).Count == 1, $"This week 1,000 = exactly 5× → {(await Alerts(rule, c2.Id)).Count} alert (operator gte).");
        var exec = await F.WithDb(db => db.Set<AmlRuleExecution>().Where(e => e.RuleId == rule && e.Mode == "Manual").OrderByDescending(e => e.Id).Select(e => e.ResultJson).FirstAsync());
        var recorded = exec != null && exec.Contains($"\"customerId\":{c3.Id},") && exec.Contains("customer.accountAgeDays");
        Result("TM-AMT-05-03", (await Alerts(rule, c3.Id)).Count == 0 && recorded, $"Customer with 10 days of history and a 2,000 week → {(await Alerts(rule, c3.Id)).Count} alerts; rule applicability (accountAgeDays ≥ 30) not met, recorded in the execution result as not applicable: {recorded}.");
        var a4 = await Alerts(rule, c4.Id);
        Result("TM-AMT-05-04", a4.Count == 0, $"Baseline 0 (no prior activity), 2,000 this week → {a4.Count} alerts; ratio undefined (no divide-by-zero, execution succeeded); first-time activity is covered by the dormancy/new-account rules.");
        Done();
    }

    [Fact]
    public async Task AMT_07_round_amounts()
    {
        var rule = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 7 }, condition = Cnt(F_("txn.amount", "multipleOf", 100), "gte", 5, "out") }, "Low");
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        var d = At(2025, 11, 12, "10:00:00");
        decimal[] round = { 500, 1000, 700, 900, 300 }, odd = { 503.25m, 1012.10m, 707.5m, 911m, 299.99m };
        await Send(round.Select((a, i) => T(c1.W, "W-A7", a, d.AddDays(i))).Concat(odd.Select((a, i) => T(c2.W, "W-A7", a, d.AddDays(i))))
            .Concat(round.Take(4).Select((a, i) => T(c3.W, "W-A7", a, d.AddDays(i)))));
        await Run(rule, d.AddHours(-1), d.AddDays(6));
        Result("TM-AMT-07-01", (await Alerts(rule, c1.Id)).Count == 1, $"500, 1,000, 700, 900, 300 within 5 days → {(await Alerts(rule, c1.Id)).Count} alert (operator multipleOf 100).");
        Result("TM-AMT-07-02", (await Alerts(rule, c2.Id)).Count == 0, $"503.25, 1,012.10, ... → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-AMT-07-03", (await Alerts(rule, c3.Id)).Count == 0, $"4 round transfers → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task STR_06_consecutive_days_below_limit()
    {
        var rule = await Rule(new { dedupKey = Subject, lookbackDays = 10, window = new { type = "Calendar", unit = "Day" }, condition = new { aggregate = "consecutiveDays", direction = "out", dailyRange = new[] { 4500m, 5000m }, @operator = "gte", value = 4 } });
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        decimal[] totals = { 4600, 4800, 4700, 4950 };
        var d = At(2025, 11, 17, "11:00:00");
        await Send(totals.Select((a, i) => T(c1.W, "W-S6", a, d.AddDays(i)))
            .Concat(totals.Select((a, i) => T(c2.W, "W-S6", a, d.AddDays(i * 2))))
            .Concat(totals.Take(3).Select((a, i) => T(c3.W, "W-S6", a, d.AddDays(i)))));
        await Run(rule, d.AddHours(-1), d.AddDays(8));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-STR-06-01", a1.Count == 1 && a1[0].EvidenceJson.Contains("consecutiveDays.dailyTotals"), $"Daily totals 4,600, 4,800, 4,700, 4,950 (limit 5,000, band 90–100%) on 4 consecutive days → {a1.Count} alert; daily totals in evidence.");
        Result("TM-STR-06-02", (await Alerts(rule, c2.Id)).Count == 0, $"Same totals on alternate days → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Result("TM-STR-06-03", (await Alerts(rule, c3.Id)).Count == 0, $"3 consecutive days → {(await Alerts(rule, c3.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task FAN_03_and_05_new_counterparties_shared_devices()
    {
        var r3 = await Rule(new { dedupKey = Subject, lookbackDays = 60, window = new { type = "Rolling", days = 7 }, condition = And(Agg("newCounterpartyShare", "gte", 80, "out"), Cnt(null, "gte", 10, "out")) });
        var r5 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 7 }, condition = Agg("sharedDeviceSenders", "gte", 5, "in") }, "High");
        var c1 = await Cust(); var c2 = await Cust(); var c13 = await Cust(); var c14 = await Cust();
        var d = At(2025, 11, 20, "10:00:00");
        var t = new List<object> { T(c1.W, "W-REG-0", 50, d.AddDays(-30)) };
        t.AddRange(Enumerable.Range(0, 12).Select(i => T(c1.W, i == 0 ? "W-REG-0" : $"W-NEWB-{i}", 50, d.AddHours(i * 6))));
        t.AddRange(Enumerable.Range(0, 12).Select(i => T(c2.W, $"W-CONTACT-{i}", 50, d.AddDays(-40).AddDays(i))));
        t.AddRange(Enumerable.Range(0, 12).Select(i => T(c2.W, $"W-CONTACT-{i}", 50, d.AddHours(i * 6))));
        var shared = "DEV-F5-" + Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 6; i++)
        {
            var s1 = await Cust(); await Signal(s1.Id, "Registration", d.AddDays(-10), shared);
            t.Add(T(s1.W, c13.W, 100, d.AddHours(i)));
            var s2 = await Cust(); await Signal(s2.Id, "Registration", d.AddDays(-10), "DEV-F5-OWN-" + i + Guid.NewGuid().ToString("N")[..4]);
            t.Add(T(s2.W, c14.W, 100, d.AddHours(i)));
        }
        await Send(t);
        await Run(r3, d.AddHours(-1), d.AddDays(4));
        await Run(r5, d.AddHours(-1), d.AddDays(1));
        Result("TM-FAN-03-01", (await Alerts(r3, c1.Id)).Count == 1, $"12 transfers, 11 to beneficiaries never seen in the previous 60 days (92%) → {(await Alerts(r3, c1.Id)).Count} alert.");
        Result("TM-FAN-03-02", (await Alerts(r3, c2.Id)).Count == 0, $"12 transfers to regular contacts → {(await Alerts(r3, c2.Id)).Count} alerts.");
        var a5 = await Alerts(r5, c13.Id);
        Result("TM-FAN-05-01", a5.Count == 1 && a5[0].EvidenceJson.Contains(shared), $"6 senders registered on the same device send to C13 → {a5.Count} alert; device and sender list in evidence.");
        Result("TM-FAN-05-02", (await Alerts(r5, c14.Id)).Count == 0, $"Senders with distinct devices → {(await Alerts(r5, c14.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task GEO_02_and_06_countries_and_session()
    {
        var r2 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", hours = 24 }, condition = new { aggregate = "distinctCount", of = "sourceCountry", filter = F_("txn.channel", "in", new[] { "ATM", "Agent", "POS" }), @operator = "gte", value = 3 } }, "High");
        var r6 = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = And(F_("txn.loginCountryMismatch", "eq", true), F_("txn.amount", "gte", 500)) });
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust(); var c4 = await Cust(); var g1 = await Cust(); var g2 = await Cust(); var g3 = await Cust();
        var d = At(2025, 11, 24, "09:00:00");
        await F.SetSetting("Monitoring:RoamingIpPrefixes", "10.77.");
        await Signal(g3.Id, "Login", d.AddMinutes(-30), "DEV-G6-3", "TR", "10.77.1.5");
        await Signal(g1.Id, "Login", d.AddMinutes(-30), "DEV-G6-1", "TR", "203.0.113.7");
        await Signal(g2.Id, "Login", d.AddMinutes(-30), "DEV-G6-2", "JO");
        var x4 = "G2-MISSING-" + c4.Id;
        await Send(new[]
        {
            T(c1.W, "ATM-JO", 50, d, "Withdrawal", channel: "ATM", src: "JO", dst: "JO"), T(c1.W, "ATM-AE", 50, d.AddHours(6), "Withdrawal", channel: "ATM", src: "AE", dst: "AE"),
            T(c1.W, "ATM-EG", 50, d.AddHours(13), "Withdrawal", channel: "ATM", src: "EG", dst: "EG"),
            T(c2.W, "ATM-JO", 50, d, "Withdrawal", channel: "ATM", src: "JO", dst: "JO"), T(c2.W, "ATM-AE", 50, d.AddHours(6), "Withdrawal", channel: "ATM", src: "AE", dst: "AE"),
            T(c3.W, "ATM-JO", 50, d, "Withdrawal", channel: "ATM", src: "JO", dst: "JO"), T(c3.W, "ATM-AE", 50, d.AddDays(2), "Withdrawal", channel: "ATM", src: "AE", dst: "AE"),
            T(c3.W, "ATM-EG", 50, d.AddDays(4), "Withdrawal", channel: "ATM", src: "EG", dst: "EG"),
            T(c4.W, "ATM-JO", 50, d, "Withdrawal", channel: "ATM", src: "JO", dst: "JO"), T(c4.W, "ATM-AE", 50, d.AddHours(6), "Withdrawal", channel: "ATM", src: "AE", dst: "AE"),
            T(c4.W, "ATM-XX", 50, d.AddHours(13), "Withdrawal", channel: "ATM", src: null, dst: null, id: x4),
            T(g1.W, "W-G6", 800, d, channel: "Mobile", src: "JO", dst: "JO"), T(g2.W, "W-G6", 800, d, channel: "Mobile", src: "JO", dst: "JO"),
            T(g3.W, "W-G6", 800, d, channel: "Mobile", src: "JO", dst: "JO")
        });
        await Run(r2, d.AddHours(-1), d.AddDays(5));
        await Run(r6, d.AddHours(-10), d.AddHours(14));
        var a1 = await Alerts(r2, c1.Id);
        Result("TM-GEO-02-01", a1.Count == 1 && a1[0].EvidenceJson.Contains("distinctCount.values"), $"Withdrawals in JO 09:00, AE 15:00 and EG 22:00 → {a1.Count} alert; countries in evidence.");
        Result("TM-GEO-02-02", (await Alerts(r2, c2.Id)).Count == 0, $"2 countries → {(await Alerts(r2, c2.Id)).Count} alerts.");
        Result("TM-GEO-02-03", (await Alerts(r2, c3.Id)).Count == 0, $"3 countries over 5 days → {(await Alerts(r2, c3.Id)).Count} alerts.");
        var tid = (await TxnId(x4)).ToString();
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().Where(x => x.IssueType == "MissingLocation" && (x.EntityId == tid || x.EntityId == x4)).Select(x => x.Description).FirstOrDefaultAsync());
        Result("TM-GEO-02-04", (await Alerts(r2, c4.Id)).Count == 0 && dq is not null, $"Country missing on one of three withdrawals → excluded from the count ({(await Alerts(r2, c4.Id)).Count} alerts, evidence 'excludedMissing') and data-quality item MissingLocation: \"{dq}\".");
        Result("TM-GEO-06-01", (await Alerts(r6, g1.Id)).Count == 1, $"App transfer of 800 (source JO) 30 minutes after a login geolocated to TR → {(await Alerts(r6, g1.Id)).Count} alert (txn.loginCountryMismatch).");
        Result("TM-GEO-06-02", (await Alerts(r6, g2.Id)).Count == 0, $"Login and transaction both JO → {(await Alerts(r6, g2.Id)).Count} alerts.");
        Result("TM-GEO-06-03", (await Alerts(r6, g3.Id)).Count == 0, $"Login geolocated to TR from a mobile-carrier roaming IP (allow-listed prefix, setting Monitoring:RoamingIpPrefixes) → {(await Alerts(r6, g3.Id)).Count} alerts (location treated as unreliable).");
        Done();
    }

    [Fact]
    public async Task DEV_02_05_06_device_and_sim_signals()
    {
        var r2 = await Rule(new
        {
            window = new { type = "Calendar", unit = "Day" }, condition = And(new { aggregate = "minutesSinceEvent", @event = "NewDevice", @operator = "lte", value = 120 }, F_("txn.amount", "gte", 1000)),
            action = new { indicators = new[] { new { label = "Approved device change (second authentication)", severity = "Low", severityMode = "Set", condition = (object)new { aggregate = "minutesSinceEvent", @event = "DeviceChangeApproved", @operator = "lte", value = 120 } } } }
        }, "High");
        var r5 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", hours = 48 }, condition = And(new { aggregate = "minutesSinceEvent", @event = "SimChange", @operator = "lte", value = 2880 }, Sum(null, "gte", 300, "out")) }, "High");
        var r6 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 7 }, condition = new { aggregate = "distinctCount", of = "device", @operator = "gte", value = 4 } });
        var a = await Cust(); var a3 = await Cust(); var b = await Cust(); var s1 = await Cust(); var s2 = await Cust(); var m1 = await Cust(); var m2 = await Cust();
        var d = At(2025, 11, 26, "20:00:00");
        await Signal(a.Id, "Login", d.AddDays(-30), "DEV-OLD-A"); await Signal(a.Id, "Login", d, "DEV-NEW-A");
        await Signal(a3.Id, "Login", d.AddDays(-30), "DEV-OLD-A3"); await Signal(a3.Id, "Login", d, "DEV-NEW-A3"); await Signal(a3.Id, "DeviceChangeApproved", d.AddMinutes(1), "DEV-NEW-A3");
        await Signal(b.Id, "Login", d.AddDays(-30), "DEV-OLD-B");
        await Signal(s1.Id, "SimChange", d.AddHours(-12), "DEV-S1"); await Signal(s2.Id, "SimChange", d.AddHours(-12), "DEV-S2");
        await Send(new[]
        {
            T(a.W, "W-D2", 1500, d.AddMinutes(30), device: "DEV-NEW-A"), T(a3.W, "W-D2", 1500, d.AddMinutes(30), device: "DEV-NEW-A3"), T(b.W, "W-D2", 1500, d.AddDays(-27), device: "DEV-OLD-B"),
            T(s1.W, "W-D5", 450, d.AddHours(-10)), T(s1.W, "W-D5", 450, d.AddHours(-9)), T("W-D5-IN", s2.W, 450, d.AddHours(-10))
        }.Concat(Enumerable.Range(0, 5).Select(i => T(m1.W, "W-D6", 20, d.AddDays(i), device: $"DEV-M1-{i}")))
         .Concat(Enumerable.Range(0, 5).Select(i => T(m2.W, "W-D6", 20, d.AddDays(i), device: $"DEV-M2-{i % 2}"))));
        await Run(r2, d.AddDays(-28), d.AddDays(1));
        await Run(r5, d.AddDays(-1), d);
        await Run(r6, d.AddHours(-1), d.AddDays(6));
        var x = await Alerts(r2, a.Id);
        Result("TM-DEV-02-01", x.Count == 1 && x[0].EvidenceJson.Contains("DEV-NEW-A"), $"First login on a new device 20:00, transfer 1,500 at 20:30 → {x.Count} alert (minutesSinceEvent NewDevice = 30 ≤ 120); device in evidence.");
        Result("TM-DEV-02-02", (await Alerts(r2, b.Id)).Count == 0, $"Transfer 3 days after the device's first login → {(await Alerts(r2, b.Id)).Count} alerts.");
        var x3 = await Alerts(r2, a3.Id);
        Result("TM-DEV-02-03", x.Count == 1 && x[0].Severity == "High" && x3.Count == 1 && x3[0].Severity == "Low",
            $"Same pattern after an approved Change-Device journey with second authentication (signal DeviceChangeApproved) → alert severity {x3.FirstOrDefault()?.Severity} (indicator with severityMode Set) vs {x.FirstOrDefault()?.Severity} without it.");
        Result("TM-DEV-05-01", (await Alerts(r5, s1.Id)).Count == 1, $"SIM changed 08:00, transfers totalling 900 by 11:00 → {(await Alerts(r5, s1.Id)).Count} alert.");
        Result("TM-DEV-05-02", (await Alerts(r5, s2.Id)).Count == 0, $"SIM change, no outgoing transactions → {(await Alerts(r5, s2.Id)).Count} alerts.");
        Result("TM-DEV-06-01", (await Alerts(r6, m1.Id)).Count == 1, $"Customer uses 5 devices within 5 days → {(await Alerts(r6, m1.Id)).Count} alert (distinctCount of device).");
        Result("TM-DEV-06-02", (await Alerts(r6, m2.Id)).Count == 0, $"2 devices → {(await Alerts(r6, m2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task BEH_02_05_08_behaviour()
    {
        var r2 = await Rule(new { dedupKey = Subject, lookbackDays = 90, window = new { type = "Rolling", days = 30 }, condition = And(Agg("typeShareChange", "gte", 60, "out"), Cnt(null, "gte", 5, "out")) });
        var r5 = await Rule(new { dedupKey = PerWindow, window = new { type = "Calendar", unit = "Month" }, condition = And(Or(F_("customer.age", "lt", 18), F_("customer.age", "gte", 75)), Sum(F_("txn.type", "in", new[] { "P2P", "Remittance" }), "gte", 2000, "out")) });
        var r8 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 3 }, condition = And(new { aggregate = "minutesSinceEvent", @event = "PhoneChange", @operator = "lte", value = 4320 }, Sum(null, "gte", 2000, "out")) });
        var h1 = await Cust(); var h2 = await Cust();
        var e1 = await Cust(dob: "1946-05-01"); var e2 = await Cust(dob: "1946-05-01");
        var p1 = await Cust(); var p2 = await Cust();
        var d = At(2025, 12, 1, "10:00:00");
        await Signal(p1.Id, "PhoneChange", At(2025, 12, 1, "09:00:00"), "DEV-P1");
        await Signal(p2.Id, "PhoneChange", At(2025, 10, 2, "09:00:00"), "DEV-P2");
        var t = new List<object>();
        t.AddRange(Enumerable.Range(0, 10).Select(i => T(h1.W, "BILLER", 30, d.AddDays(-80 + i * 5), i < 9 ? "BillPayment" : "CashOut")));
        t.AddRange(Enumerable.Range(0, 10).Select(i => T(h1.W, "AGT", 30, d.AddDays(i), i < 8 ? "CashOut" : "BillPayment")));
        t.AddRange(Enumerable.Range(0, 10).Select(i => T(h2.W, "BILLER", 30, d.AddDays(-80 + i * 5), i < 9 ? "BillPayment" : "CashOut")));
        t.AddRange(Enumerable.Range(0, 10).Select(i => T(h2.W, "BILLER", 30, d.AddDays(i), i < 9 ? "BillPayment" : "CashOut")));
        t.AddRange(Enumerable.Range(0, 3).Select(i => T(e1.W, $"W-NEWBEN-{i}", 1000, d.AddDays(i))));
        t.AddRange(Enumerable.Range(0, 5).Select(i => T(e2.W, $"UTILITY-{i}", 500, d.AddDays(i), "BillPayment")));
        t.Add(T(p1.W, "W-B8", 2500, At(2025, 12, 2, "11:00:00")));
        t.Add(T(p2.W, "W-B8", 2500, At(2025, 12, 2, "11:00:00")));
        await Send(t);
        await Run(r2, d.AddHours(-1), d.AddDays(11));
        await Run(r5, At(2025, 12, 1, "00:00:00"), At(2026, 1, 1, "00:00:00"));
        await Run(r8, At(2025, 12, 1, "00:00:00"), At(2025, 12, 3, "00:00:00"));
        var a2 = await Alerts(r2, h1.Id);
        Result("TM-BEH-02-01", a2.Count == 1 && a2[0].EvidenceJson.Contains("typeShareChange.shares"), $"History 90% bill payments, now 80% cash-out → {a2.Count} alert; type shares before/now in evidence.");
        Result("TM-BEH-02-02", (await Alerts(r2, h2.Id)).Count == 0, $"Stable mix → {(await Alerts(r2, h2.Id)).Count} alerts.");
        Result("TM-BEH-05-01", (await Alerts(r5, e1.Id)).Count == 1, $"Customer aged 79 sends 3,000 to new beneficiaries → {(await Alerts(r5, e1.Id)).Count} alert (customer.age).");
        Result("TM-BEH-05-02", (await Alerts(r5, e2.Id)).Count == 0, $"Customer aged 79 paying utility bills only → {(await Alerts(r5, e2.Id)).Count} alerts.");
        Result("TM-BEH-08-01", (await Alerts(r8, p1.Id)).Count == 1, $"Phone changed Monday 09:00, 2,500 transferred Tuesday → {(await Alerts(r8, p1.Id)).Count} alert (minutesSinceEvent PhoneChange ≤ 3 days).");
        Result("TM-BEH-08-02", (await Alerts(r8, p2.Id)).Count == 0, $"Phone changed 60 days earlier → {(await Alerts(r8, p2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task CSH_03_CIR_02_03_MUL_02_04()
    {
        var electronicIn = And(F_("txn.direction", "eq", "in"), F_("txn.type", "neq", "CashIn"));
        var r3 = await Rule(new
        {
            dedupKey = Subject, window = new { type = "Rolling", days = 30 },
            condition = And(new { aggregate = "ratio", measure = "amount", numerator = And(F_("txn.direction", "eq", "out"), F_("txn.type", "in", new[] { "CashOut", "Withdrawal" })), denominator = electronicIn, @operator = "gte", value = 90 },
                Sum(F_("txn.type", "neq", "CashIn"), "gte", 3000, "in"))
        });
        var rc2 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 7 }, condition = Agg("reciprocalCount", "gte", 6) });
        var rc3 = await Rule(new { dedupKey = PerWindow, window = new { type = "Calendar", unit = "Month" }, condition = Sum(F_("txn.isLinkedParty", "eq", true), "gte", 3000) });
        var rm2 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 14 }, condition = And(F_("customer.accountAgeDays", "lte", 14), Sum(null, "gte", 2000, "in")) }, "High");
        var rm4 = await Rule(new
        {
            dedupKey = PerWindow, window = new { type = "Calendar", unit = "Month" },
            condition = And(F_("customer.age", "between", new[] { 18, 24 }), Sum(null, "gte", 2000, "in"),
                new { aggregate = "ratio", measure = "amount", numerator = F_("txn.direction", "eq", "out"), denominator = F_("txn.direction", "eq", "in"), @operator = "gte", value = 80 })
        });
        var x1 = await Cust(); var x2 = await Cust(); var ca = await Cust(); var cb = await Cust(); var f1 = await Cust(); var f2 = await Cust();
        var l1 = await Cust(); var l2 = await Cust(); var u1 = await Cust(); var u2 = await Cust();
        var n1 = await Cust(registrationDate: "2025-12-05"); var n2 = await Cust(registrationDate: "2023-12-01");
        var y1 = await Cust(dob: "2005-06-01"); var y2 = await Cust(dob: "2005-06-01");
        var token = "9" + Random.Shared.NextInt64(100000000, 999999999);
        foreach (var c in new[] { l1, l2 }) await (await As("manager")).PostOk($"/api/customers/{c.Id}/identifications", new { idType = "Passport", idNumber = token, issuingCountry = "JO" });
        await (await As("officer")).PostOk("/api/governance/entity-resolution/run");
        var d = At(2025, 12, 10, "10:00:00");
        await Send(new[]
        {
            T("W-EMPLOYER", x1.W, 5000, d, "BankTransfer"), T(x1.W, "AGT-C3", 4800, d.AddDays(1), "CashOut", channel: "Agent"),
            T("W-EMPLOYER", x2.W, 5000, d, "BankTransfer"), T(x2.W, "SHOP", 1200, d.AddDays(1), "MerchantPayment"),
            T(ca.W, cb.W, 300, d), T(cb.W, ca.W, 300, d.AddDays(1)), T(ca.W, cb.W, 300, d.AddDays(2)), T(cb.W, ca.W, 300, d.AddDays(3)), T(ca.W, cb.W, 300, d.AddDays(4)), T(cb.W, ca.W, 300, d.AddDays(4).AddHours(2)),
            T(f1.W, f2.W, 300, d), T(f1.W, f2.W, 300, d.AddDays(2)), T(f1.W, f2.W, 300, d.AddDays(4)),
            T(l1.W, l2.W, 2000, d), T(l2.W, l1.W, 2000, d.AddDays(3)), T(u1.W, u2.W, 2000, d), T(u2.W, u1.W, 2000, d.AddDays(3)),
            T("W-M2", n1.W, 3000, d), T("W-M2", n2.W, 3000, d),
            T("W-M4", y1.W, 3000, d), T(y1.W, "W-M4-OUT", 2900, d.AddDays(2)), T("W-M4", y2.W, 3000, d), T(y2.W, "SHOP", 500, d.AddDays(2), "MerchantPayment")
        });
        foreach (var r in new[] { r3, rc2, rc3, rm2, rm4 }) await Run(r, d.AddHours(-1), d.AddDays(6));
        Result("TM-CSH-03-01", (await Alerts(r3, x1.Id)).Count == 1, $"Receives 5,000 by transfer, withdraws 4,800 cash (96%) → {(await Alerts(r3, x1.Id)).Count} alert (ratio with independent numerator: cash-out out ÷ electronic in).");
        Result("TM-CSH-03-02", (await Alerts(r3, x2.Id)).Count == 0, $"Spends at merchants → {(await Alerts(r3, x2.Id)).Count} alerts.");
        Result("TM-CIR-02-01", (await Alerts(rc2, ca.Id)).Count == 1, $"A and B exchange 3 transfers each way within 5 days → {(await Alerts(rc2, ca.Id)).Count} alert (reciprocalCount 6).");
        Result("TM-CIR-02-02", (await Alerts(rc2, f1.Id)).Count == 0 && (await Alerts(rc2, f2.Id)).Count == 0, "One-way family support → 0 alerts.");
        var lc = await Alerts(rc3, l1.Id);
        Result("TM-CIR-03-01", lc.Count == 1, $"Two wallets of customers sharing an ID token exchange 4,000 → {lc.Count} alert (txn.isLinkedParty, hidden self-transfer).");
        Result("TM-CIR-03-02", (await Alerts(rc3, u1.Id)).Count == 0, $"Unlinked parties exchange 4,000 → {(await Alerts(rc3, u1.Id)).Count} alerts.");
        Result("TM-MUL-02-01", (await Alerts(rm2, n1.Id)).Count == 1, $"5-day-old wallet receives 3,000 → {(await Alerts(rm2, n1.Id)).Count} alert.");
        Result("TM-MUL-02-02", (await Alerts(rm2, n2.Id)).Count == 0, $"2-year-old wallet receives 3,000 → {(await Alerts(rm2, n2.Id)).Count} alerts.");
        Result("TM-MUL-04-01", (await Alerts(rm4, y1.Id)).Count == 1, $"20-year-old receives 3,000 and forwards 2,900 in the month → {(await Alerts(rm4, y1.Id)).Count} alert.");
        Result("TM-MUL-04-02", (await Alerts(rm4, y2.Id)).Count == 0, $"Same age, normal spend → {(await Alerts(rm4, y2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task MUL_03_VAG_ATO_03_counterparty_and_login()
    {
        await Field("TM_FRAUD_CONFIRMED", "Boolean");
        await Field("TM_MERCHANT_CATEGORY", "ShortText", "Merchant");
        await Field("TM_TRAVEL_NOTIFIED", "Boolean");
        var vasp = F_("counterparty.field:TM_MERCHANT_CATEGORY", "eq", "VASP");
        var rm3 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", days = 30 }, condition = Cnt(F_("counterparty.field:TM_FRAUD_CONFIRMED", "eq", true), "gte", 1, "in") }, "High");
        var rv1 = await Rule(new { dedupKey = PerWindow, window = new { type = "Calendar", unit = "Month" }, condition = Sum(vasp, "gte", 1000, "out") });
        var rv2 = await Rule(new { dedupKey = PerWindow, window = new { type = "Calendar", unit = "Month" }, condition = Sum(F_("counterparty.field:TM_MERCHANT_CATEGORY", "eq", "GAMBLING"), "gte", 500, "out") });
        var rv3 = await Rule(new { dedupKey = Subject, window = new { type = "Rolling", hours = 24 }, condition = And(Sum(vasp, "gte", 1000, "in"), new { aggregate = "rapidMovement", filter = vasp, maxMinutes = 1440, @operator = "gte", value = 80 }) }, "High");
        var ra3 = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = And(new { aggregate = "minutesSinceEvent", @event = "NewLoginCountry", @operator = "lte", value = 60 }, F_("field:TM_TRAVEL_NOTIFIED", "neq", true), F_("txn.direction", "eq", "out")) });
        Dictionary<string, object> Ind(params (string K, object V)[] kv) { var dd = new Dictionary<string, object> { ["OCCUPATION"] = "EMPLOYEE", ["SOURCE_OF_INCOME"] = "SALARY" }; foreach (var (k, v) in kv) dd[k] = v; return dd; }
        var fraud = await Cust(attributes: Ind(("TM_FRAUD_CONFIRMED", true))); var cleared = await Cust(attributes: Ind(("TM_FRAUD_CONFIRMED", false)));
        var m1 = await Cust(); var m2 = await Cust();
        var exch = await Cust("Merchant", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "61000001", ["TM_MERCHANT_CATEGORY"] = "VASP" }, name: "Tm Crypto Exchange");
        var casino = await Cust("Merchant", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "61000002", ["TM_MERCHANT_CATEGORY"] = "GAMBLING" }, name: "Tm Betting Merchant");
        var grocer = await Cust("Merchant", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "61000003", ["TM_MERCHANT_CATEGORY"] = "GROCERY" }, name: "Tm Grocery");
        var v1 = await Cust(); var v2 = await Cust(); var g1 = await Cust(); var g2 = await Cust(); var k1 = await Cust(); var k2 = await Cust();
        var t1 = await Cust(); var t2 = await Cust(attributes: Ind(("TM_TRAVEL_NOTIFIED", true)));
        await Backdate(fraud.Id, cleared.Id, exch.Id, casino.Id, grocer.Id, t2.Id);
        var d = At(2025, 12, 15, "10:00:00");
        foreach (var c in new[] { t1, t2 })
        {
            await Signal(c.Id, "Login", d.AddDays(-20), "DEV-T-" + c.Id, "JO");
            await Signal(c.Id, "Login", d.AddMinutes(-30), "DEV-T-" + c.Id, "TR");
        }
        await Send(new[]
        {
            T(fraud.W, m1.W, 500, d), T(cleared.W, m2.W, 500, d),
            T(v1.W, exch.W, 2500, d, "MerchantPayment"), T(v2.W, grocer.W, 2500, d, "MerchantPayment"),
            T(g1.W, casino.W, 700, d, "MerchantPayment"), T(g2.W, grocer.W, 700, d, "MerchantPayment"),
            T(exch.W, k1.W, 2000, d, "MerchantRefund"), T(k1.W, "AGT-V3", 1800, d.AddHours(3), "CashOut", channel: "Agent"), T(exch.W, k2.W, 2000, d, "MerchantRefund"),
            T(t1.W, "W-A3", 400, d), T(t2.W, "W-A3", 400, d)
        });
        foreach (var r in new[] { rm3, rv1, rv2, rv3, ra3 }) await Run(r, d.AddHours(-1), d.AddDays(1));
        Result("TM-MUL-03-01", (await Alerts(rm3, m1.Id)).Count == 1, $"Receives 500 from a customer with a confirmed fraud flag → {(await Alerts(rm3, m1.Id)).Count} alert (counterparty.field:TM_FRAUD_CONFIRMED).");
        Result("TM-MUL-03-02", (await Alerts(rm3, m2.Id)).Count == 0, $"Sender's flag closed as false positive → {(await Alerts(rm3, m2.Id)).Count} alerts.");
        Result("TM-VAG-01-01", (await Alerts(rv1, v1.Id)).Count == 1, $"Pays 2,500 to a merchant classified VASP → {(await Alerts(rv1, v1.Id)).Count} alert (counterparty classification as a dynamic merchant field).");
        Result("TM-VAG-01-02", (await Alerts(rv1, v2.Id)).Count == 0, $"No VASP counterparties → {(await Alerts(rv1, v2.Id)).Count} alerts.");
        Result("TM-VAG-02-01", (await Alerts(rv2, g1.Id)).Count == 1, $"Payments of 700 to a gambling merchant → {(await Alerts(rv2, g1.Id)).Count} alert.");
        Result("TM-VAG-02-02", (await Alerts(rv2, g2.Id)).Count == 0, $"No gambling merchants → {(await Alerts(rv2, g2.Id)).Count} alerts.");
        Result("TM-VAG-03-01", (await Alerts(rv3, k1.Id)).Count == 1, $"Receives 2,000 from the exchange, cashes out 1,800 three hours later → {(await Alerts(rv3, k1.Id)).Count} alert.");
        Result("TM-VAG-03-02", (await Alerts(rv3, k2.Id)).Count == 0, $"Funds kept → {(await Alerts(rv3, k2.Id)).Count} alerts.");
        Result("TM-ATO-03-01", (await Alerts(ra3, t1.Id)).Count == 1, $"First-ever login from TR, transfer 30 minutes later → {(await Alerts(ra3, t1.Id)).Count} alert (minutesSinceEvent NewLoginCountry).");
        Result("TM-ATO-03-02", (await Alerts(ra3, t2.Id)).Count == 0, $"Travel notified in profile (dynamic field TM_TRAVEL_NOTIFIED) → {(await Alerts(ra3, t2.Id)).Count} alerts.");
        Done();
    }
}
