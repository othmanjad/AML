using System.Text.Json.Nodes;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.TransactionMonitoring;

/// <summary>
/// Workbook scenarios that were Blocked (capability gaps) and became configurable with end-of-day balances, multi-hop chains
/// and peer percentiles: AMT-06, RMV-02, RMV-04, TF-04, ATO-01 and ATO-02.
/// </summary>
[Collection("tm")]
public class BalanceChainPeerScenarioTests : TmBase
{
    public BalanceChainPeerScenarioTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private static readonly string[] PerSubject = { "rule", "version", "subject" };

    private static Dictionary<string, object?> Agg(string aggregate, string op, object value, object? extra = null)
    {
        var d = new Dictionary<string, object?> { ["aggregate"] = aggregate, ["operator"] = op, ["value"] = value };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) d[p.Name] = p.GetValue(extra);
        return d;
    }

    private Task<bool> MissingInput(long customer, string field) =>
        F.WithDb(db => db.Set<DataQualityIssue>().AnyAsync(d => d.IssueType == "RuleInputMissing" && d.EntityId == customer.ToString() && d.Description.Contains(field)));

    [Fact]
    public async Task AMT_06_peer_group_percentile()
    {
        var region = "AMT6-" + Guid.NewGuid().ToString("N")[..6];
        var small = "AMT6S-" + Guid.NewGuid().ToString("N")[..6];
        var student = new Dictionary<string, object> { ["OCCUPATION"] = "STUDENT", ["SOURCE_OF_INCOME"] = "SALARY" };
        var group = new List<(long Id, string W)>();
        for (var i = 0; i < 61; i++) group.Add(await Cust(region: region, attributes: student));
        var tiny = new List<(long Id, string W)>();
        for (var i = 0; i < 20; i++) tiny.Add(await Cust(region: small, attributes: student));
        await Backdate(group.Concat(tiny).Select(c => c.Id).ToArray());
        // P = 99th percentile; peer group = same type, segment (occupation) and region; at least 50 peers.
        var rule = await Rule(new
        {
            dedupKey = PerSubject, window = new { type = "Calendar", unit = "Month" },
            condition = Agg("peerPercentile", "gte", 99, new { direction = "out", peerBy = new[] { "customer.type", "field:OCCUPATION", "customer.region" }, minPeers = 50 })
        });
        var t = new List<object>();
        for (var i = 0; i < 60; i++) t.Add(T(group[i].W, "W-AMT6", 100 + i * 10, At(2026, 5, 10, "10:00:00")));
        t.Add(T(group[60].W, "W-AMT6", 5000, At(2026, 5, 11, "10:00:00")));
        t.AddRange(tiny.Select((c, i) => T(c.W, "W-AMT6", i == 0 ? 5000 : 100, At(2026, 5, 10, "10:00:00"))));
        await Send(t);
        await Run(rule, At(2026, 5, 1, "00:00:00"), At(2026, 6, 1, "00:00:00"));
        var top = await Alerts(rule, group[60].Id);
        var ev = top.Count == 1 ? JsonNode.Parse(top[0].EvidenceJson)!["evidence"] : null;
        Result("TM-AMT-06-01", top.Count == 1 && ev?["peerPercentile.peerCount"]?.GetValue<int>() == 60 && ev?["peerPercentile.peerMedian"] is not null,
            $"Student customer above all 60 peers of its segment (same type, occupation, region) → percentile {ev?["peerPercentile"]} → {top.Count} alert; peer count {ev?["peerPercentile.peerCount"]}, median {ev?["peerPercentile.peerMedian"]} and P90 {ev?["peerPercentile.peerP90"]} in the evidence. Note: percentile = share of peers strictly below the customer, so 99.5 and 100 both satisfy P = 99.");
        var p95 = await Alerts(rule, group[57].Id);
        Result("TM-AMT-06-02", p95.Count == 0, $"Customer with 57 of 60 peers below (95th percentile) → {p95.Count} alerts.");
        var notEval = (await Alerts(rule, tiny[0].Id)).Count == 0 && await MissingInput(tiny[0].Id, "peerGroup (fewer than 50 peers)");
        Result("TM-AMT-06-03", notEval, "Peer group of 20 members (19 peers < 50) → rule not evaluated for the customer (no alert) and logged as a RuleInputMissing data-quality item.");
        Done();
    }

    [Fact]
    public async Task RMV_02_balance_kept_near_zero()
    {
        // B = 10 (average end-of-day balance), A = 5,000 (monthly throughput).
        var rule = await Rule(new
        {
            dedupKey = PerSubject, window = new { type = "Calendar", unit = "Month" },
            condition = And(Sum(null, "gte", 5000, "in"), Agg("avgBalance", "lte", 10))
        });
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        var bal = new List<object>();
        for (var d = 1; d <= 20; d++)
        {
            bal.Add(Bal(c1.W, $"2026-05-{d:D2}", 2 + d % 3));
            bal.Add(Bal(c2.W, $"2026-05-{d:D2}", 2 + d % 3));
        }
        await Balances(bal.ToArray());
        var t = new List<object>();
        foreach (var (c, amount) in new[] { (c1, 1500m), (c2, 250m), (c3, 1500m) })
            for (var k = 0; k < 4; k++)
            {
                t.Add(T("W-RMV2-IN", c.W, amount, At(2026, 5, 3 + k * 4, "10:00:00")));
                t.Add(T(c.W, "W-RMV2-OUT", amount, At(2026, 5, 3 + k * 4, "16:00:00")));
            }
        await Send(t);
        await Run(rule, At(2026, 5, 1, "00:00:00"), At(2026, 6, 1, "00:00:00"));
        var a1 = await Alerts(rule, c1.Id);
        Result("TM-RMV-02-01", a1.Count == 1 && a1[0].EvidenceJson.Contains("avgBalance.daysWithBalance"),
            $"Monthly in/out 6,000 with end-of-day balance between 2 and 4 → {a1.Count} alert (average balance and the daily balances in the evidence).");
        Result("TM-RMV-02-02", (await Alerts(rule, c2.Id)).Count == 0, $"Throughput 1,000 with the same balances → {(await Alerts(rule, c2.Id)).Count} alerts.");
        var dq = await MissingInput(c3.Id, "'balance'");
        Result("TM-RMV-02-03", (await Alerts(rule, c3.Id)).Count == 0 && dq,
            $"Throughput 6,000 but no balance data from the core → {(await Alerts(rule, c3.Id)).Count} alerts; marked not evaluated with a RuleInputMissing data-quality item.");
        Done();
    }

    [Fact]
    public async Task RMV_04_layering_chain()
    {
        // H = 3 hops, P = 85 % retained per hop, T = 24 h (per-hop limit, window 24 h).
        var rule = await Rule(new
        {
            dedupKey = PerSubject, window = new { type = "Rolling", hours = 24 },
            condition = Agg("chain", "gte", 3, new { maxDepth = 6, maxMinutes = 1440, minHopPct = 85 })
        }, "High");
        var p = new List<(long Id, string W)>();
        for (var i = 0; i < 14; i++) p.Add(await Cust());
        var d = At(2026, 5, 20, "08:00:00");
        var clr = "W-CLR-" + Guid.NewGuid().ToString("N")[..6];
        await (await As("manager")).PostOk("/api/admin/wallet-mappings", new { sourceSystem = "CORE", externalWalletId = clr, mappingCategory = "Clearing", effectiveFrom = "2020-01-01T00:00:00Z", reason = "tm" });
        var reference = U("REF");
        await Send(new[]
        {
            T(p[0].W, p[1].W, 1000, d), T(p[1].W, p[2].W, 950, d.AddHours(2)), T(p[2].W, p[3].W, 920, d.AddHours(5)),
            T(p[4].W, p[5].W, 1000, d), T(p[5].W, p[6].W, 950, d.AddDays(3)), T(p[6].W, p[7].W, 920, d.AddDays(6)),
            T(p[8].W, p[9].W, 1000, d), T(p[9].W, p[10].W, 950, d.AddHours(2)),
            // Second hop routed through an internal clearing wallet (two legs with a shared reference).
            T(p[11].W, p[12].W, 1000, d), T(p[12].W, clr, 950, d.AddHours(1), reference: reference), T(clr, p[13].W, 950, d.AddHours(1).AddSeconds(5), reference: reference),
            T(p[13].W, p[0].W, 900, d.AddHours(3)),
        });
        await Run(rule, At(2026, 5, 20, "00:00:00"), At(2026, 5, 28, "00:00:00"));
        var a = await Alerts(rule, p[0].Id);
        var path = a.Count > 0 ? JsonNode.Parse(a[0].EvidenceJson)!["evidence"]!["chain.path"]!.AsArray().Count : 0;
        Result("TM-RMV-04-01", a.Count == 1 && path >= 3,
            $"A→B 1,000; B→C 950; C→D 920 within 6 hours (95 % / 97 % retained) → {a.Count} alert on A with {path} hops in the evidence (B, C and D are alerted as chain members too).");
        Result("TM-RMV-04-02", (await Alerts(rule, p[4].Id)).Count == 0, $"Hops separated by 3 days → {(await Alerts(rule, p[4].Id)).Count} alerts.");
        Result("TM-RMV-04-03", (await Alerts(rule, p[8].Id)).Count == 0, $"Only 2 hops → {(await Alerts(rule, p[8].Id)).Count} alerts.");
        var e = await Alerts(rule, p[11].Id);
        var ePath = e.Count > 0 ? string.Join(" | ", JsonNode.Parse(e[0].EvidenceJson)!["evidence"]!["chain.path"]!.AsArray().Select(x => x!.GetValue<string>())) : "";
        Result("TM-RMV-04-04", e.Count == 1 && !ePath.Contains(clr) && ePath.Contains($"{p[12].Id}->{p[13].Id}"),
            $"One hop through internal clearing wallet: the two legs resolve to one economic transfer {p[12].Id}->{p[13].Id}; clearing wallet skipped, chain still detected ({e.Count} alert).");
        Done();
    }

    [Fact]
    public async Task TF_04_liquidation_before_travel()
    {
        // P = 90 % of balance, T = 3 days, long inactivity = no activity in the 180 days before the window.
        var rule = await Rule(new
        {
            dedupKey = PerSubject, lookbackDays = 180, window = new { type = "Rolling", days = 3 },
            condition = And(F_("txn.isCrossBorder", "eq", true), F_("txn.direction", "eq", "out"),
                Cnt(F_("txn.balanceSharePct", "gte", 90), "gte", 1, "out"), Agg("historyCount", "eq", 0))
        });
        var c1 = await Cust(); var c2 = await Cust();
        await Balances(Bal(c1.W, "2026-05-31", 4000), Bal(c2.W, "2026-05-31", 4000), Bal(c1.W, "2026-06-01", 300), Bal(c2.W, "2026-06-01", 2500));
        await Send(new[]
        {
            T(c1.W, "W-OLD", 50, At(2025, 11, 1, "10:00:00")), T(c2.W, "W-OLD", 50, At(2025, 11, 1, "10:00:00")),
            T(c1.W, "W-AGT", 3700, At(2026, 6, 1, "10:00:00"), type: "CashOut"), T(c1.W, "W-TR", 250, At(2026, 6, 2, "18:00:00"), dst: "TR"),
            T(c2.W, "W-AGT", 1500, At(2026, 6, 1, "10:00:00"), type: "CashOut"), T(c2.W, "W-TR", 250, At(2026, 6, 2, "18:00:00"), dst: "TR"),
        });
        await Run(rule, At(2026, 6, 1, "00:00:00"), At(2026, 6, 4, "00:00:00"));
        Result("TM-TF-04-01", (await Alerts(rule, c1.Id)).Count == 1,
            $"Dormant 7 months, withdraws 3,700 of a 4,000 balance (92.5 %) and remits abroad within 2 days → {(await Alerts(rule, c1.Id)).Count} alert.");
        Result("TM-TF-04-02", (await Alerts(rule, c2.Id)).Count == 0, $"Partial withdrawal (1,500 of 4,000) then remittance → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task ATO_01_password_reset_then_balance_transfer()
    {
        // P = 70 % of balance, T = 6 hours.
        var rule = await Rule(new
        {
            window = new { type = "Rolling", hours = 6 },
            condition = And(F_("txn.direction", "eq", "out"), F_("txn.balanceSharePct", "gte", 70), Agg("minutesSinceEvent", "lte", 360, new { @event = "PasswordReset" }))
        }, "High");
        var c1 = await Cust(); var c2 = await Cust();
        await Balances(Bal(c1.W, "2026-06-09", 1000), Bal(c2.W, "2026-06-09", 1000));
        await Signal(c1.Id, "PasswordReset", At(2026, 6, 10, "21:00:00"));
        await Signal(c2.Id, "PasswordReset", At(2026, 6, 10, "21:00:00"));
        await Send(new[] { T(c1.W, "W-ATO1", 900, At(2026, 6, 10, "21:20:00")) });
        await Run(rule, At(2026, 6, 10, "00:00:00"), At(2026, 6, 11, "00:00:00"));
        var a = await Alerts(rule, c1.Id);
        Result("TM-ATO-01-01", a.Count == 1 && a[0].EvidenceJson.Contains("PasswordReset"),
            $"Password reset 21:00; 900 of a 1,000 balance (90 %) sent at 21:20 → {a.Count} alert (reset time in the evidence).");
        Result("TM-ATO-01-02", (await Alerts(rule, c2.Id)).Count == 0, $"Reset, no transfer → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task ATO_02_new_beneficiary_full_balance_transfer()
    {
        // P = 80 % of balance, T = 30 minutes since a beneficiary was added.
        var rule = await Rule(new
        {
            window = new { type = "Rolling", minutes = 30 },
            condition = And(F_("txn.direction", "eq", "out"), F_("txn.balanceSharePct", "gte", 80), Agg("minutesSinceEvent", "lte", 30, new { @event = "BeneficiaryAdded" }))
        }, "High");
        var c1 = await Cust(); var c2 = await Cust();
        await Balances(Bal(c1.W, "2026-06-14", 2000), Bal(c2.W, "2026-06-14", 2000));
        await Signal(c1.Id, "BeneficiaryAdded", At(2026, 6, 15, "10:00:00"));
        await Signal(c2.Id, "BeneficiaryAdded", At(2026, 2, 1, "10:00:00"));
        await Send(new[] { T(c1.W, "W-ATO2-NEW", 1900, At(2026, 6, 15, "10:05:00")), T(c2.W, "W-ATO2-OLD", 1900, At(2026, 6, 15, "10:05:00")) });
        await Run(rule, At(2026, 6, 15, "00:00:00"), At(2026, 6, 16, "00:00:00"));
        Result("TM-ATO-02-01", (await Alerts(rule, c1.Id)).Count == 1,
            $"Beneficiary added 10:00; 1,900 of a 2,000 balance (95 %) sent at 10:05 → {(await Alerts(rule, c1.Id)).Count} alert. Note: signals do not identify the beneficiary, so the rule tests the most recent beneficiary addition.");
        Result("TM-ATO-02-02", (await Alerts(rule, c2.Id)).Count == 0, $"Long-standing beneficiary (added months earlier) → {(await Alerts(rule, c2.Id)).Count} alerts.");
        Done();
    }
}
