using System.Diagnostics;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.TransactionMonitoring;

[Collection("tm")]
public class EngineEdgeCaseTests : TmBase
{
    public EngineEdgeCaseTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private static readonly object Wd = F_("txn.type", "eq", "Withdrawal");
    private static object DailyCount(int n) => new { window = new { type = "Calendar", unit = "Day" }, condition = Cnt(Wd, "gte", n, "out") };

    [Fact]
    public async Task ENG_01_status_handling()
    {
        var rule = await Rule(DailyCount(18));
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        var day = At(2025, 12, 2, "09:00:00");
        var pend = Enumerable.Range(0, 3).Select(i => $"E01P-{c1.Id}-{i}").ToList();
        await Send(Enumerable.Range(0, 15).Select(i => T(c1.W, "ATM-E01", 20, day.AddMinutes(i * 5), "Withdrawal", channel: "ATM"))
            .Concat(pend.Select((id, i) => T(c1.W, "ATM-E01", 20, day.AddHours(3).AddMinutes(i), "Withdrawal", "PEND", "ATM", id: id)))
            .Concat(Enumerable.Range(0, 2).Select(i => T(c1.W, "ATM-E01", 20, day.AddHours(4).AddMinutes(i), "Withdrawal", "FAIL", "ATM"))));
        await Run(rule, At(2025, 12, 2, "00:00:00"), At(2025, 12, 3, "00:00:00"));
        var before = (await Alerts(rule, c1.Id)).Count;
        await Send(pend.Select((id, i) => T(c1.W, "ATM-E01", 20, day.AddHours(5).AddMinutes(i), "Withdrawal", "SUCC", "ATM", id: id)));
        await Run(rule, At(2025, 12, 2, "00:00:00"), At(2025, 12, 3, "00:00:00"));
        var after = (await Alerts(rule, c1.Id)).Count;
        Result("TM-ENG-01-01", before == 0 && after == 1,
            $"15 Completed + 3 Pending + 2 Failed → {before} alerts; after the 3 Pending became Completed (status update) and the rule re-ran → {after} alert. Note: N set to 18 so that completion of the 3 pending reaches the threshold (failed never count); re-evaluation is a rule re-run (scheduled or manual), not event-driven.");

        var cancelId = $"E01C-{c2.Id}";
        await Send(Enumerable.Range(0, 17).Select(i => T(c2.W, "ATM-E01", 20, day.AddMinutes(i * 5), "Withdrawal", channel: "ATM"))
            .Append(T(c2.W, "ATM-E01", 20, day.AddHours(3), "Withdrawal", "PEND", "ATM", id: cancelId)));
        await Send(new[] { T(c2.W, "ATM-E01", 20, day.AddHours(4), "Withdrawal", "CANCELLED", "ATM", id: cancelId) });
        await Run(rule, At(2025, 12, 2, "00:00:00"), At(2025, 12, 3, "00:00:00"));
        var st = await F.WithDb(db => db.Set<Transaction>().Where(t => t.SourceTransactionId == cancelId).Select(t => t.Status).FirstAsync());
        Result("TM-ENG-01-02", (await Alerts(rule, c2.Id)).Count == 0, $"17 Completed + 1 Pending later Cancelled (normalised status now '{st}') → {(await Alerts(rule, c2.Id)).Count} alerts; never counted.");

        var unk = $"E01U-{c3.Id}";
        await Send(Enumerable.Range(0, 17).Select(i => T(c3.W, "ATM-E01", 20, day.AddMinutes(i * 5), "Withdrawal", channel: "ATM"))
            .Append(T(c3.W, "ATM-E01", 20, day.AddHours(3), "Withdrawal", "ZZ_UNMAPPED", "ATM", id: unk)));
        await Run(rule, At(2025, 12, 2, "00:00:00"), At(2025, 12, 3, "00:00:00"));
        var u = await F.WithDb(db => db.Set<Transaction>().Where(t => t.SourceTransactionId == unk).Select(t => new { t.Status, t.SourceStatus, t.DataQualityStatus }).FirstAsync());
        Result("TM-ENG-01-03", (await Alerts(rule, c3.Id)).Count == 0 && u.DataQualityStatus.Contains("UnknownStatus"),
            $"Unknown source status 'ZZ_UNMAPPED' → stored normalised status {u.Status} (source status kept), data-quality flag '{u.DataQualityStatus}' (filterable in transaction search), excluded from the count ({(await Alerts(rule, c3.Id)).Count} alerts). Note: normalised as Pending + UnknownStatus flag rather than a literal 'Unknown' status.");
        Done();
    }

    [Fact]
    public async Task ENG_02_reversal_netting()
    {
        var sum = Sum(null, "gte", 5000, "out");
        var net = await Rule(new { reversalHandling = "Net", window = new { type = "Calendar", unit = "Day" }, condition = sum });
        var gross = await Rule(new { reversalHandling = "Include", window = new { type = "Calendar", unit = "Day" }, condition = sum });
        var c = await Cust(); var c3 = await Cust();
        var day = At(2025, 12, 5, "09:00:00");
        var ids = Enumerable.Range(0, 3).Select(i => $"E02-{c.Id}-{i}").ToList();
        await Send(ids.Select((id, i) => T(c.W, "W-E02", 2000, day.AddHours(i), id: id)));
        await Send(new[] { T("W-E02", c.W, 2000, day.AddHours(5), "Reversal", original: ids[0], reversal: true) });
        await Run(net, At(2025, 12, 5, "00:00:00"), At(2025, 12, 6, "00:00:00"));
        await Run(gross, At(2025, 12, 5, "00:00:00"), At(2025, 12, 6, "00:00:00"));
        Result("TM-ENG-02-01", (await Alerts(net, c.Id)).Count == 0, $"Daily sum 6,000 with 2,000 reversed, net policy → {(await Alerts(net, c.Id)).Count} alerts (net 4,000).");
        Result("TM-ENG-02-02", (await Alerts(gross, c.Id)).Count == 1, $"Same with gross (Include) policy → {(await Alerts(gross, c.Id)).Count} alert.");

        var ids3 = Enumerable.Range(0, 3).Select(i => $"E02L-{c3.Id}-{i}").ToList();
        await Send(ids3.Select((id, i) => T(c3.W, "W-E02", 2000, day.AddHours(i), id: id)));
        await Run(net, At(2025, 12, 5, "00:00:00"), At(2025, 12, 6, "00:00:00"));
        var a = (await Alerts(net, c3.Id)).Single();
        var hist0 = await F.WithDb(db => db.Set<AlertStatusHistory>().CountAsync(h => h.AlertId == a.Id));
        await Send(new[] { T("W-E02", c3.W, 2000, day.AddDays(2), "Reversal", original: ids3[0], reversal: true) });
        await Run(net, At(2025, 12, 5, "00:00:00"), At(2025, 12, 8, "00:00:00"));
        var a2 = await F.WithDb(db => db.Set<AmlAlert>().FirstAsync(x => x.Id == a.Id));
        var hist1 = await F.WithDb(db => db.Set<AlertStatusHistory>().CountAsync(h => h.AlertId == a.Id));
        var noteAdded = hist1 > hist0;
        Result("TM-ENG-02-03", a2.EvidenceJson == a.EvidenceJson && a2.Status == a.Status && noteAdded,
            $"Reversal arrived 2 days after the alert → evidence unchanged: {a2.EvidenceJson == a.EvidenceJson}, status {a.Status}→{a2.Status} (no automatic closure), note/history entries added: {hist1 - hist0}. DEFECT: a later reversal of an alerted transaction is not noted on the alert.");
        Done();
    }

    [Fact]
    public async Task ENG_03_time_zone_windows()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = Cnt(null, "gte", 4, "out") });
        var c1 = await Cust(); var c2 = await Cust();
        var utc = (int y, int m, int d, int h, int mi) => new DateTime(y, m, d, h, mi, 0, DateTimeKind.Utc);
        await Send(Enumerable.Range(0, 2).Select(i => T(c1.W, "W-E03", 10, utc(2025, 12, 9, 21, 30 + i)))
            .Concat(Enumerable.Range(0, 2).Select(i => T(c1.W, "W-E03", 10, utc(2025, 12, 10, 10, i))))
            .Concat(Enumerable.Range(0, 2).Select(i => T(c2.W, "W-E03", 10, utc(2025, 12, 9, 20, 30 + i))))
            .Concat(Enumerable.Range(0, 2).Select(i => T(c2.W, "W-E03", 10, utc(2025, 12, 10, 10, i)))));
        await Run(rule, utc(2025, 12, 9, 0, 0), utc(2025, 12, 11, 0, 0));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-ENG-03-01", a1.Count == 1 && a1[0].EvidenceJson.Contains("2025-12-10") && (await Alerts(rule, c2.Id)).Count == 0,
            $"2 transactions at 21:30 UTC (00:30 Amman next day) + 2 next morning → {a1.Count} alert for local day 2025-12-10; control customer with 20:30 UTC (23:30 local, previous day) → {(await Alerts(rule, c2.Id)).Count} alerts.");

        // Jordan DST ended 2021-10-29 01:00 local (UTC+3 → UTC+2) under the IANA rules in force at that time.
        var dst = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = Cnt(null, "gte", 2, "out") });
        var d1 = await Cust(); var d2 = await Cust();
        await Send(new[]
        {
            T(d1.W, "W-E03D", 10, utc(2021, 10, 28, 21, 30)), T(d1.W, "W-E03D", 10, utc(2021, 10, 29, 21, 30)),
            T(d2.W, "W-E03D", 10, utc(2021, 10, 28, 21, 30)), T(d2.W, "W-E03D", 10, utc(2021, 10, 29, 22, 30))
        });
        await Run(dst, utc(2021, 10, 28, 0, 0), utc(2021, 10, 31, 0, 0));
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
        var off1 = tz.GetUtcOffset(utc(2021, 10, 28, 21, 30)); var off2 = tz.GetUtcOffset(utc(2021, 10, 29, 21, 30));
        var x1 = await Alerts(dst, d1.Id); var x2 = await Alerts(dst, d2.Id);
        Result("TM-ENG-03-02", x1.Count == 1 && x2.Count == 0,
            $"Offset change date 2021-10-29 (UTC{off1.Hours:+0;-0} → UTC{off2.Hours:+0;-0}): 21:30 UTC on the 28th (00:30 local 29th) and 21:30 UTC on the 29th (23:30 local 29th) → {x1.Count} alert for local day 29; 22:30 UTC on the 29th (00:30 local 30th) correctly falls on the next day → {x2.Count} alerts. Windows follow IANA rules.");
        Done();
    }

    [Fact]
    public async Task ENG_04_multi_currency()
    {
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = Sum(null, "gte", 5000, "out") });
        var c1 = await Cust(); var c2 = await Cust();
        var usdId = "E04U-" + c1.Id; var xyzId = "E04X-" + c2.Id;
        await Send(new[]
        {
            T(c1.W, "W-E04", 3000, At(2025, 12, 12, "10:00:00")), T(c1.W, "W-E04", 3000, At(2025, 12, 12, "11:00:00"), currency: "USD", id: usdId),
            T(c2.W, "W-E04", 3000, At(2025, 12, 12, "10:00:00")), T(c2.W, "W-E04", 3000, At(2025, 12, 12, "11:00:00"), currency: "XYZ", id: xyzId)
        });
        await Run(rule, At(2025, 12, 12, "00:00:00"), At(2025, 12, 13, "00:00:00"));
        var u = await F.WithDb(db => db.Set<Transaction>().Where(t => t.SourceTransactionId == usdId).Select(t => new { t.BaseAmount, t.FxRate, t.FxStatus }).FirstAsync());
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-ENG-04-01", a1.Count == 1 && u.BaseAmount is > 2100 and < 2150 && u.FxRate is not null,
            $"3,000 JOD + 3,000 USD → USD converted at stored rate {u.FxRate} = {u.BaseAmount:0.###} JOD (total ≈ {3000 + (u.BaseAmount ?? 0):0.###} JOD) → {a1.Count} alert; rate stored on the transaction ({u.FxStatus}).");
        var x = await F.WithDb(db => db.Set<Transaction>().Where(t => t.SourceTransactionId == xyzId).Select(t => new { t.BaseAmount, t.FxStatus }).FirstAsync());
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().CountAsync(d => d.IssueType == "MissingFxRate" && d.EntityId == xyzId));
        await Run(rule, At(2025, 12, 12, "00:00:00"), At(2025, 12, 13, "00:00:00"));
        var exec = await F.WithDb(db => db.Set<AmlRuleExecution>().Where(e => e.RuleId == rule).OrderByDescending(e => e.Id).FirstAsync());
        Result("TM-ENG-04-02", x.BaseAmount is null && dq == 1 && (await Alerts(rule, c2.Id)).Count == 0,
            $"Currency without rate → base amount {(x.BaseAmount is null ? "null (not defaulted to 1)" : x.BaseAmount.ToString())}, FX status {x.FxStatus}, {dq} MissingFxRate data-quality item, {(await Alerts(rule, c2.Id)).Count} alerts. Note: the rule result is not labelled 'incomplete' (execution status {exec.Status}); the excluded transaction is listed as 'excludedMissingFx' only in the evidence of matches.");
        Done();
    }

    [Fact]
    public async Task ENG_05_dedup_across_reruns()
    {
        var rule = await Rule(DailyCount(5));
        var c = await Cust();
        await Send(Enumerable.Range(0, 6).Select(i => T(c.W, "ATM-E05", 20, At(2025, 12, 15, "09:00:00").AddMinutes(i * 10), "Withdrawal", channel: "ATM")));
        for (var i = 0; i < 4; i++) await Run(rule, At(2025, 12, 15, "00:00:00"), At(2025, 12, 16, "00:00:00"));
        var a = await Alerts(rule, c.Id);
        Result("TM-ENG-05-01", a.Count == 1, $"Rule re-run 4 times for the same day → {a.Count} alert (deduplication key rule|version|subject|day).");
        var n0 = await TxnCount(a[0].Id);
        await Send(Enumerable.Range(0, 3).Select(i => T(c.W, "ATM-E05", 20, At(2025, 12, 15, "15:00:00").AddMinutes(i * 10), "Withdrawal", channel: "ATM")));
        await Run(rule, At(2025, 12, 15, "00:00:00"), At(2025, 12, 16, "00:00:00"));
        var a2 = await Alerts(rule, c.Id);
        var n1 = await TxnCount(a2[0].Id);
        Result("TM-ENG-05-02", a2.Count == 1 && n1 > n0, $"3 new transactions in the same window → still {a2.Count} alert; linked transactions {n0} → {n1} (existing alert updated, not duplicated).");
        Done();
    }

    [Fact]
    public async Task ENG_06_late_arrivals()
    {
        var rule = await Rule(DailyCount(20));
        var c = await Cust();
        var d = At(2025, 12, 18, "09:00:00");
        await Send(Enumerable.Range(0, 19).Select(i => T(c.W, "ATM-E06", 20, d.AddMinutes(i * 10), "Withdrawal", channel: "ATM")));
        // Scheduler semantics: each run covers (last window end, now]; day D was evaluated, then the 20th withdrawal of day D arrives on D+1.
        await Run(rule, At(2025, 12, 18, "00:00:00"), At(2025, 12, 19, "00:00:00"));
        await Send(new[] { T(c.W, "ATM-E06", 20, d.AddHours(8), "Withdrawal", channel: "ATM") });
        await Run(rule, At(2025, 12, 19, "00:00:00"), At(2025, 12, 20, "00:00:00"));
        var a = await Alerts(rule, c.Id);
        Result("TM-ENG-06-01", a.Count == 1,
            $"20th withdrawal of day D ingested on D+1; next scheduled window (D+1) → {a.Count} alerts. DEFECT: scheduled runs select transactions by event time from the last window end, so late arrivals for an already-evaluated window are never re-evaluated (no re-evaluation lookback by ingestion time).");
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().CountAsync(x => x.Description.Contains("late") || x.IssueType == "LateArrival"));
        Result("TM-ENG-06-02", dq > 0, $"Late arrival outside any re-evaluated window → {dq} 'not evaluated' records. DEFECT: late arrivals are not logged as not evaluated and are not visible in operations.");
        Done();
    }

    [Fact]
    public async Task ENG_07_customer_vs_wallet_level()
    {
        var rule = await Rule(DailyCount(20));
        var c = await Cust(); var w2 = await Wallet(c.Id);
        await Send(Enumerable.Range(0, 10).Select(i => T(c.W, "ATM-E07", 20, At(2025, 12, 21, "09:00:00").AddMinutes(i * 5), "Withdrawal", channel: "ATM"))
            .Concat(Enumerable.Range(0, 10).Select(i => T(w2, "ATM-E07", 20, At(2025, 12, 21, "11:00:00").AddMinutes(i * 5), "Withdrawal", channel: "ATM"))));
        await Run(rule, At(2025, 12, 21, "00:00:00"), At(2025, 12, 22, "00:00:00"));
        var a = await Alerts(rule, c.Id);
        var walletDef = new { subject = "Wallet", window = new { type = "Calendar", unit = "Day" }, condition = Cnt(Wd, "gte", 20, "out") };
        var walletErrors = Validate(walletDef);
        var walletAlerts = -1;
        if (walletErrors.Count == 0)
        {
            var wr = await Rule(walletDef);
            await Run(wr, At(2025, 12, 21, "00:00:00"), At(2025, 12, 22, "00:00:00"));
            walletAlerts = (await Alerts(wr, c.Id)).Count;
        }
        Result("TM-ENG-07-01", a.Count == 1 && walletAlerts == 0,
            $"10 withdrawals on W1 + 10 on W2 of the same customer → customer-level rule {a.Count} alert; wallet-level rule: " +
            (walletErrors.Count > 0 ? $"cannot be configured (validator: {string.Join("; ", walletErrors)})" : $"subject 'Wallet' accepted but evaluated per customer → {walletAlerts} alert(s)") +
            ". GAP: no per-rule aggregation dimension (customer vs wallet).");
        var unknown = "W-UNRESOLVED-" + Guid.NewGuid().ToString("N")[..6];
        var ut = Enumerable.Range(0, 20).Select(i => $"E07U-{unknown}-{i}").ToList();
        await Send(ut.Select((id, i) => T(unknown, "ATM-E07", 20, At(2025, 12, 21, "09:00:00").AddMinutes(i * 5), "Withdrawal", channel: "ATM", id: id)));
        await Run(rule, At(2025, 12, 21, "00:00:00"), At(2025, 12, 22, "00:00:00"));
        var tid = await TxnId(ut[0]);
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().CountAsync(x => x.IssueType == "UnresolvedParty" && x.EntityId == tid.ToString()));
        var ua = await F.WithDb(db => db.Set<AmlAlert>().CountAsync(x => x.RuleId == rule && x.SubjectKey != null && x.SubjectKey.Contains(unknown)));
        Result("TM-ENG-07-02", dq > 0 && ua > 0,
            $"20 withdrawals from an unresolved wallet → {dq} UnresolvedParty data-quality item, {ua} wallet-level alerts. GAP: transactions whose party is unresolved are not evaluated at wallet level (customer subjects only).");
        Done();
    }

    [Fact]
    public async Task ENG_08_duplicates_and_09_versioning()
    {
        var rule = await Rule(DailyCount(20));
        var c = await Cust();
        var batch = Enumerable.Range(0, 10).Select(i => T(c.W, "ATM-E08", 20, At(2025, 12, 23, "09:00:00").AddMinutes(i * 5), "Withdrawal", channel: "ATM", id: $"E08-{c.Id}-{i}")).ToList();
        await Send(batch); await Send(batch);
        await Run(rule, At(2025, 12, 23, "00:00:00"), At(2025, 12, 24, "00:00:00"));
        var stored = await F.WithDb(db => db.Set<Transaction>().CountAsync(t => t.SourceTransactionId.StartsWith($"E08-{c.Id}-")));
        Result("TM-ENG-08-01", stored == 10 && (await Alerts(rule, c.Id)).Count == 0, $"Same 10 withdrawals ingested twice → {stored} stored (idempotent by source + transaction ID), {(await Alerts(rule, c.Id)).Count} alerts.");

        var r9 = await Rule(DailyCount(20));
        var a = await Cust(); var b = await Cust();
        await Send(Enumerable.Range(0, 20).Select(i => T(a.W, "ATM-E09", 20, At(2025, 12, 24, "09:00:00").AddMinutes(i * 5), "Withdrawal", channel: "ATM")));
        await Run(r9, At(2025, 12, 24, "00:00:00"), At(2025, 12, 25, "00:00:00"));
        var old = (await Alerts(r9, a.Id)).Single();
        await NewVersion(r9, DailyCount(15));
        await Send(Enumerable.Range(0, 16).Select(i => T(b.W, "ATM-E09", 20, At(2025, 12, 25, "09:00:00").AddMinutes(i * 5), "Withdrawal", channel: "ATM")));
        await Run(r9, At(2025, 12, 25, "00:00:00"), At(2025, 12, 26, "00:00:00"));
        var oldAfter = await F.WithDb(db => db.Set<AmlAlert>().FirstAsync(x => x.Id == old.Id));
        var nb = await Alerts(r9, b.Id);
        Result("TM-ENG-09-01", oldAfter.RuleVersion == 1 && oldAfter.EvidenceJson == old.EvidenceJson && old.EvidenceJson.Contains("\"value\":20") && nb.Count == 1 && nb[0].RuleVersion == 2,
            $"Threshold changed 20 → 15 after alert: old alert keeps v{oldAfter.RuleVersion} evidence (value 20, unchanged); new run uses v{nb.FirstOrDefault()?.RuleVersion} and alerts on 16 withdrawals ({nb.Count}).");
        var before = await F.WithDb(db => db.Set<AmlAlert>().CountAsync(x => x.RuleId == r9));
        var bt = await (await As("officer")).PostOk($"/api/admin/aml-rules/{r9}/run-backtest", new { from = At(2025, 9, 26, "00:00:00"), to = At(2025, 12, 26, "00:00:00") });
        var after = await F.WithDb(db => db.Set<AmlAlert>().CountAsync(x => x.RuleId == r9));
        Result("TM-ENG-09-02", after == before && bt["matches"]!.GetValue<int>() > 0,
            $"Backtest of v2 over 90 days → {bt["matches"]} matches reported, operational alerts {before} → {after} (results only).");
        Done();
    }

    [Fact]
    public async Task ENG_10_fees_and_clearing()
    {
        var rule = await Rule(new { scope = new { transactionTypes = new[] { "Withdrawal", "CashOut", "P2P" } }, window = new { type = "Calendar", unit = "Day" }, condition = Cnt(null, "gte", 20, "out") });
        var c = await Cust();
        var d = At(2025, 12, 27, "09:00:00");
        await Send(Enumerable.Range(0, 20).SelectMany(i => new[] { T(c.W, "ATM-E10", 20, d.AddMinutes(i * 5), "Withdrawal", channel: "ATM"), T(c.W, "FEE-INCOME", 0.5m, d.AddMinutes(i * 5).AddSeconds(1), "Fee", channel: "System") }));
        await Run(rule, At(2025, 12, 27, "00:00:00"), At(2025, 12, 28, "00:00:00"));
        var a = await Alerts(rule, c.Id);
        var m = a.Count == 0 ? null : (await Matches(a[0].Id)).First();
        Result("TM-ENG-10-01", a.Count == 1 && m!.MatchedConditionsJson.Contains("\"20\""), $"20 withdrawals each with a separate fee record → count {(m is null ? "n/a" : "20 (fees excluded by the rule's transaction-type scope)")}, {a.Count} alert.");

        var clr = "W-CLR-" + Guid.NewGuid().ToString("N")[..6];
        await (await As("manager")).PostOk("/api/admin/wallet-mappings", new { sourceSystem = "CORE", externalWalletId = clr, mappingCategory = "Clearing", effectiveFrom = "2020-01-01T00:00:00Z", reason = "tm" });
        var countRule = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = Cnt(null, "gte", 2, "out") });
        var pa = await Cust(); var pb = await Cust();
        var reference = U("REF");
        await Send(new[] { T(pa.W, clr, 500, At(2025, 12, 28, "10:00:00"), reference: reference), T(clr, pb.W, 500, At(2025, 12, 28, "10:00:05"), reference: reference) });
        await Run(countRule, At(2025, 12, 28, "00:00:00"), At(2025, 12, 29, "00:00:00"));
        var legs = await F.WithDb(db => db.Set<Transaction>().Where(t => t.OriginalReference == reference).Select(t => new { t.SenderCustomerId, t.ReceiverCustomerId }).ToListAsync());
        var outA = legs.Count(l => l.SenderCustomerId == pa.Id);
        var dbl = (await Alerts(countRule, pa.Id)).Count;
        Result("TM-ENG-10-02", outA == 1 && dbl == 0,
            $"A → clearing wallet → B (2 legs, shared reference): both legs resolved to economic parties A→B; A's outgoing count = {outA}, rule 'count ≥ 2' → {dbl} alerts. DEFECT: both clearing legs are attributed to the same economic transfer, so A→B is counted twice.");
        Done();
    }

    [Fact]
    public async Task ENG_11_realtime_batch_consistency()
    {
        var ch = "TMENG11";
        var def = new { realtimeCapable = true, window = new { type = "Calendar", unit = "Day" }, condition = And(F_("txn.channel", "eq", ch), Cnt(null, "gte", 20, "out")) };
        await RealtimeRule("TMENG11RT", def);
        var batch = await Rule(new { window = new { type = "Calendar", unit = "Day" }, condition = And(F_("txn.channel", "eq", ch), Cnt(F_("txn.channel", "eq", ch), "gte", 20, "out")) });
        var c = await Cust();
        var now = DateTime.UtcNow.AddMinutes(-2);
        await Send(Enumerable.Range(0, 19).Select(i => T(c.W, "ATM-E11", 20, now.AddSeconds(i), "Withdrawal", channel: ch)));
        now = DateTime.UtcNow.AddMinutes(-1);
        var txId = "E11-" + c.Id;
        var dec = await Decide(new { sourceTransactionId = txId, transactionType = "Withdrawal", channel = ch, amount = 20, currency = "JOD", senderWallet = c.W, receiverWallet = "ATM-E11", eventTime = now.AddMinutes(1) });
        await Send(new[] { T(c.W, "ATM-E11", 20, now.AddMinutes(1), "Withdrawal", channel: ch, id: txId) });
        await Run(batch, now.AddHours(-1), DateTime.UtcNow.AddMinutes(1));
        var a = await Alerts(batch, c.Id);
        var holdAlerts = await F.WithDb(db => db.Set<AmlAlert>().CountAsync(x => x.PrimaryCustomerId == c.Id && x.Source == "Realtime"));
        var linked = a.Count == 1 && a[0].EvidenceJson.Contains(dec["decisionId"]!.GetValue<string>());
        Result("TM-ENG-11-01", dec["decision"]!.GetValue<string>() == "HoldForReview" && a.Count == 1 && linked,
            $"20th withdrawal evaluated inline → {dec["decision"]}; nightly batch on the same data → {a.Count} alert, real-time alerts {holdAlerts}, linked to the real-time decision: {linked}. GAP: real-time holds are not recorded as alerts and batch alerts are not linked to the real-time decision (consistent decision, no duplicate, but no link).");
        Done();
    }
}

[Collection("tm")]
public class EngineScaleTests : TmBase
{
    public EngineScaleTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact]
    public async Task ENG_12_scale()
    {
        Blocked("TM-ENG-12-01", "2 million transactions/day with all active rules requires a production-sized SQL Server and data generator; not executable in this test container. The engine loads all transactions of the window into memory (no pre-aggregated batch path), so the target is not demonstrated.");
        var c = await Cust();
        const int template = 200, copies = 249;
        var start = At(2026, 1, 1, "00:00:01");
        var sw = Stopwatch.StartNew();
        await Send(Enumerable.Range(0, template).Select(i => T(c.W, $"W-SCALE-{i % 50}", 1, start.AddSeconds(i * 60), id: $"E12-{c.Id}-{i}")));
        var ingest = sw.Elapsed;
        // Test-data seeding: clone the API-ingested rows (all columns) to reach the target volume within one calendar month.
        var sql = "DECLARE @cols nvarchar(max) = (SELECT STRING_AGG(CAST(QUOTENAME(name) AS nvarchar(max)), ',') FROM sys.columns WHERE object_id = OBJECT_ID('Transactions') AND is_identity = 0 AND is_computed = 0 " +
                  "AND system_type_id <> 189 AND name NOT IN ('SourceTransactionId', 'TimestampUtc', 'BusinessDate')); " +
                  "DECLARE @sql nvarchar(max) = N'INSERT INTO Transactions(' + @cols + N', SourceTransactionId, TimestampUtc, BusinessDate) SELECT ' + @cols + " +
                  "N', t.SourceTransactionId + ''-'' + CAST(x.n AS varchar(10)), DATEADD(minute, x.n * 170, t.TimestampUtc), CAST(DATEADD(hour, 3, DATEADD(minute, x.n * 170, t.TimestampUtc)) AS date) " +
                  $"FROM Transactions t CROSS JOIN (SELECT TOP ({copies}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x " +
                  $"WHERE t.SourceTransactionId LIKE ''E12-{c.Id}-%'''; EXEC sp_executesql @sql;";
        await F.ExecSql(sql);
        var stored = await F.WithDb(db => db.Set<Transaction>().CountAsync(t => t.SenderCustomerId == c.Id));
        var rule = await Rule(new { window = new { type = "Calendar", unit = "Month" }, dedupKey = new[] { "rule", "version", "subject", "window" }, condition = Cnt(null, "gte", 49_000, "out") });
        sw.Restart();
        string outcome;
        bool ok;
        try
        {
            var run = Run(rule, At(2026, 1, 1, "00:00:00"), At(2026, 2, 1, "00:00:00"));
            if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(5))) != run) throw new TimeoutException("no response within 5 minutes");
            await run;
            var a = await Alerts(rule, c.Id);
            ok = a.Count == 1;
            outcome = $"synchronous rule run completed in {sw.Elapsed.TotalSeconds:0.0} s with {a.Count} alert";
        }
        catch (Exception ex)
        {
            var clientWait = sw.Elapsed;
            AmlRuleExecution? e = null;
            for (var i = 0; i < 12; i++)
            {
                e = await F.WithDb(db => db.Set<AmlRuleExecution>().Where(x => x.RuleId == rule && x.Mode != "Simulation").OrderByDescending(x => x.Id).FirstOrDefaultAsync());
                if (e is not null && e.Status != "Running") break;
                await Task.Delay(5000);
            }
            var a = await Alerts(rule, c.Id);
            ok = false;
            outcome = $"the synchronous run API call failed after {clientWait.TotalSeconds:0} s ({ex.GetType().Name}); server-side execution status {e?.Status}, " +
                      $"duration {(e?.CompletedAt is { } done ? (done - e.StartedAt).TotalSeconds.ToString("0") + " s" : "still running (not finished " + (DateTime.UtcNow - e!.StartedAt).TotalMinutes.ToString("0") + " min after start)")}, {a.Count} alert";
        }
        Result("TM-ENG-12-02", ok, $"Customer with {stored:N0} transactions in one calendar-month window ({template} ingested through the API in {ingest.TotalSeconds:0} s, the rest cloned in SQL): {outcome}.");
        Done();
    }
}
