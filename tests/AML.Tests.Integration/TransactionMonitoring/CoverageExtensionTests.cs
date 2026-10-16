using System.Text.Json.Nodes;
using AML.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.TransactionMonitoring;

/// <summary>
/// Capabilities added for the "AML TMS Rules and Scenarios Summary" catalogue: end-of-day balances (TM-02), multi-hop chains
/// (TM-08), peer percentiles by segment (TM-05), industry benchmarks and invoice data (TM-11), thresholds weighted by
/// customer or jurisdiction risk (TM-04), maintained lookup lists, household aggregation and cash transaction reports (CTR).
/// Results are reported as "TMRESULT|COV-..." lines.
/// </summary>
[Collection("tm")]
public class CoverageExtensionTests : TmBase
{
    public CoverageExtensionTests(TmFactory f, ITestOutputHelper o) : base(f, o) { }

    private static readonly string[] PerSubject = { "rule", "version", "subject" };
    private static object Agg(string aggregate, string op, object value, object? extra = null) =>
        extra is null ? new { aggregate, @operator = op, value } : Merge(new { aggregate, @operator = op, value }, extra);

    private static Dictionary<string, object?> Merge(object a, object b)
    {
        var d = new Dictionary<string, object?>();
        foreach (var o in new[] { a, b })
            foreach (var p in o.GetType().GetProperties()) d[p.Name] = p.GetValue(o);
        return d;
    }

    [Fact]
    public async Task Balances_ingestion_and_pass_through_with_low_retained_balance()
    {
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        // Ingestion contract: replace on the same date, rejected currency, unknown wallet stored with a data-quality item.
        var r = await Balances(Bal(c1.W, "2026-04-05", 70), Bal(c1.W, "2026-04-05", 50), Bal(c1.W, "2026-04-06", 60), Bal(c2.W, "2026-04-05", 3000),
            Bal(c2.W, "2026-04-06", 3200), Bal(c1.W, "2026-04-07", 10, "XXX"), Bal("W-UNKNOWN-" + Guid.NewGuid().ToString("N")[..6], "2026-04-05", 5));
        var recs = r["records"]!.AsArray();
        var okIngest = r["stored"]!.GetValue<int>() == 5 && r["replaced"]!.GetValue<int>() == 1 && r["rejected"]!.GetValue<int>() == 1 &&
                       recs[5]!["error"]!.GetValue<string>().Contains("unsupported currency") && recs[6]!["error"]!.GetValue<string>().Contains("not linked");
        var stored = await F.WithDb(db => db.Set<WalletBalanceSnapshot>().Where(b => b.ExternalWalletId == c1.W).OrderBy(b => b.BusinessDate).ToListAsync());
        Result("COV-BAL-01", okIngest && stored.Count == 2 && stored[0].Balance == 50 && stored[0].BaseBalance == 50 && stored[0].CustomerId == c1.Id,
            $"7 balances → stored {r["stored"]}, replaced {r["replaced"]}, rejected {r["rejected"]}; same-date report replaced (2026-04-05 = {stored.FirstOrDefault()?.Balance}); unknown currency rejected; unknown wallet stored unlinked.");

        // TM-02: ≥ 80 % of inflow leaves within 5 days and the average end-of-day balance stays ≤ 100 (≤ 5 % of the inflow).
        var rule = await Rule(new
        {
            dedupKey = PerSubject, window = new { type = "Rolling", days = 5 },
            condition = And(Agg("rapidMovement", "gte", 80, new { maxMinutes = 7200 }), Agg("avgBalance", "lte", 100), Agg("balanceRetentionPct", "lte", 5))
        }, "High");
        var t = new List<object>();
        foreach (var c in new[] { c1, c2, c3 })
        {
            t.Add(T("W-SRC-" + c.Id, c.W, 5000, At(2026, 4, 5, "10:00:00")));
            t.Add(T(c.W, "W-DST-" + c.Id, 4500, At(2026, 4, 6, "09:00:00")));
        }
        await Send(t);
        await Run(rule, At(2026, 4, 5, "00:00:00"), At(2026, 4, 8, "00:00:00"));
        var a1 = await Alerts(rule, c1.Id);
        Result("COV-BAL-02", a1.Count == 1 && a1[0].EvidenceJson.Contains("avgBalance") && a1[0].EvidenceJson.Contains("balanceRetentionPct"),
            $"In 5,000 → out 4,500 next day, end-of-day balances 50 / 60 → {a1.Count} alert (average balance and retention in the evidence).");
        Result("COV-BAL-03", (await Alerts(rule, c2.Id)).Count == 0, $"Same flows, balances 3,000 / 3,200 retained → {(await Alerts(rule, c2.Id)).Count} alerts.");
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().AnyAsync(d => d.IssueType == "RuleInputMissing" && d.EntityId == c3.Id.ToString() && d.Description.Contains("balance")));
        Result("COV-BAL-04", (await Alerts(rule, c3.Id)).Count == 0 && dq, $"No balances reported → {(await Alerts(rule, c3.Id)).Count} alerts and a RuleInputMissing data-quality item (no silent pass).");

        // Share of the previous end-of-day balance (e.g. TF-04 / ATO: withdrawal of ≥ 90 % of the balance).
        var c4 = await Cust(); var c5 = await Cust();
        await Balances(Bal(c4.W, "2026-04-09", 1000), Bal(c5.W, "2026-04-09", 1000));
        var share = await Rule(new { condition = And(F_("txn.direction", "eq", "out"), F_("txn.balanceSharePct", "gte", 90)) });
        await Send(new[] { T(c4.W, "W-OUT", 950, At(2026, 4, 10, "11:00:00")), T(c5.W, "W-OUT", 500, At(2026, 4, 10, "11:00:00")) });
        await Run(share, At(2026, 4, 10, "00:00:00"), At(2026, 4, 11, "00:00:00"));
        Result("COV-BAL-05", (await Alerts(share, c4.Id)).Count == 1 && (await Alerts(share, c5.Id)).Count == 0,
            $"Previous balance 1,000: transfer 950 (95 %) → {(await Alerts(share, c4.Id)).Count} alert; 500 (50 %) → {(await Alerts(share, c5.Id)).Count} alerts.");
        Done();
    }

    [Fact]
    public async Task Multi_hop_chain_layering()
    {
        var p = new List<(long Id, string W)>();
        for (var i = 0; i < 11; i++) p.Add(await Cust());
        var rule = await Rule(new
        {
            dedupKey = PerSubject, window = new { type = "Rolling", days = 2 },
            condition = Agg("chain", "gte", 3, new { maxDepth = 6, maxMinutes = 240, minHopPct = 80 })
        }, "High");
        var d = At(2026, 4, 12, "09:00:00");
        await Send(new[]
        {
            // A → B → C → D, each hop within 4 h carrying ≥ 80 % of the previous hop.
            T(p[0].W, p[1].W, 10000, d), T(p[1].W, p[2].W, 9500, d.AddHours(1)), T(p[2].W, p[3].W, 9000, d.AddHours(2)),
            // E → F → G: two hops only.
            T(p[4].W, p[5].W, 10000, d), T(p[5].W, p[6].W, 9800, d.AddHours(1)),
            // H → I → J → K but the second hop keeps only 30 % of the first.
            T(p[7].W, p[8].W, 10000, d), T(p[8].W, p[9].W, 3000, d.AddHours(1)), T(p[9].W, p[10].W, 2900, d.AddHours(2)),
        });
        await Run(rule, At(2026, 4, 12, "00:00:00"), At(2026, 4, 13, "00:00:00"));
        var a = await Alerts(rule, p[0].Id);
        var mid = await Alerts(rule, p[1].Id);
        Result("COV-CHN-01", a.Count == 1 && a[0].EvidenceJson.Contains("chain.path") && mid.Count == 1,
            $"A→B→C→D (3 hops, 95 % / 95 % retained) → origin {a.Count} alert, intermediary {mid.Count} alert; hop path in the evidence.");
        Result("COV-CHN-02", (await Alerts(rule, p[4].Id)).Count == 0, $"E→F→G (2 hops) → {(await Alerts(rule, p[4].Id)).Count} alerts.");
        Result("COV-CHN-03", (await Alerts(rule, p[7].Id)).Count == 0, $"H→I→J→K with the second hop at 30 % of the first → {(await Alerts(rule, p[7].Id)).Count} alerts (chain broken).");
        var errors = Validate(new { window = new { type = "Rolling", days = 1 }, condition = Agg("chain", "gte", 3, new { maxDepth = 20 }) });
        Result("COV-CHN-04", errors.Any(e => e.Contains("maxDepth")) && errors.Any(e => e.Contains("maxMinutes")), $"Invalid chain parameters rejected: {string.Join(" | ", errors.Take(2))}.");
        Done();
    }

    [Fact]
    public async Task Peer_percentile_by_segment()
    {
        var region = "PEER-" + Guid.NewGuid().ToString("N")[..6];
        var small = "PEERS-" + Guid.NewGuid().ToString("N")[..6];
        var peers = new List<(long Id, string W)>();
        for (var i = 0; i < 11; i++) peers.Add(await Cust(region: region));
        var lone = new List<(long Id, string W)>();
        for (var i = 0; i < 3; i++) lone.Add(await Cust(region: small));
        var rule = await Rule(new
        {
            dedupKey = PerSubject, window = new { type = "Calendar", unit = "Month" },
            condition = Agg("peerPercentile", "gte", 99, new { direction = "out", peerBy = new[] { "customer.type", "customer.region" }, minPeers = 10 })
        });
        var ratio = await Rule(new
        {
            dedupKey = PerSubject, window = new { type = "Calendar", unit = "Month" },
            condition = Agg("peerRatio", "gte", 10, new { direction = "out", peerBy = new[] { "customer.region" }, minPeers = 10 })
        });
        var t = new List<object>();
        for (var i = 0; i < 10; i++) t.Add(T(peers[i].W, "W-PEER", 1000 + i * 10, At(2026, 4, 14, "10:00:00")));
        t.Add(T(peers[10].W, "W-PEER", 50000, At(2026, 4, 15, "10:00:00")));
        t.AddRange(lone.Select((c, i) => T(c.W, "W-PEER", i == 0 ? 50000 : 1000, At(2026, 4, 14, "10:00:00"))));
        await Send(t);
        foreach (var r in new[] { rule, ratio }) await Run(r, At(2026, 4, 1, "00:00:00"), At(2026, 5, 1, "00:00:00"));
        var top = await Alerts(rule, peers[10].Id);
        Result("COV-PEER-01", top.Count == 1 && top[0].EvidenceJson.Contains("peerCount") && top[0].EvidenceJson.Contains(region),
            $"Monthly outflow 50,000 vs 10 peers (same type and region) around 1,000 → percentile 100 → {top.Count} alert; peer group and count in the evidence.");
        Result("COV-PEER-02", (await Alerts(rule, peers[0].Id)).Count == 0, $"Peer at the bottom of its group → {(await Alerts(rule, peers[0].Id)).Count} alerts.");
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().AnyAsync(d => d.IssueType == "RuleInputMissing" && d.EntityId == lone[0].Id.ToString() && d.Description.Contains("peerGroup")));
        Result("COV-PEER-03", (await Alerts(rule, lone[0].Id)).Count == 0 && dq, $"Segment with 2 peers (< minPeers 10) → {(await Alerts(rule, lone[0].Id)).Count} alerts and a data-quality item instead of a silent result.");
        Result("COV-PEER-04", (await Alerts(ratio, peers[10].Id)).Count == 1 && (await Alerts(ratio, peers[5].Id)).Count == 0,
            $"peerRatio by region: 50,000 ÷ peer median ≈ 1,045 → {(await Alerts(ratio, peers[10].Id)).Count} alert; typical peer → {(await Alerts(ratio, peers[5].Id)).Count} alerts.");
        var errors = Validate(new { window = new { type = "Rolling", days = 30 }, condition = Agg("peerPercentile", "gte", 99, new { peerBy = new[] { "customer.shoeSize" } }) });
        Result("COV-PEER-05", errors.Any(e => e.Contains("Calendar")) && errors.Any(e => e.Contains("customer.shoeSize")), $"Rolling window / unknown peer attribute rejected: {string.Join(" | ", errors.Take(2))}.");
        Done();
    }

    [Fact]
    public async Task Industry_benchmark_and_invoice_variance()
    {
        var segment = "BENCH-" + Guid.NewGuid().ToString("N")[..6];
        var manager = await As("manager");
        var b = await manager.PostOk("/api/admin/benchmarks", new { metric = "CASH_SHARE_PCT", segmentField = "customer.region", segmentValue = segment, value = 20, source = "Synthetic test benchmark (to be confirmed)", effectiveFrom = "2026-01-01T00:00:00Z" });
        var noSource = await manager.Post("/api/admin/benchmarks", new { metric = "CASH_SHARE_PCT", segmentField = "customer.region", segmentValue = segment, value = 20 });
        Result("COV-BEN-01", b["id"] is not null && (int)noSource.Status == 400, $"Benchmark created with its source (id {b["id"]}); without a source → HTTP {(int)noSource.Status}.");
        var m1 = await Cust(region: segment); var m2 = await Cust(region: segment); var m3 = await Cust(region: "NO-BENCH-" + segment);
        // Cash share of incoming value at least double the segment benchmark (deviation ≥ 100 %).
        var rule = await Rule(new
        {
            dedupKey = PerSubject, window = new { type = "Calendar", unit = "Month" },
            condition = Agg("benchmarkDeviation", "gte", 100, new { benchmark = "CASH_SHARE_PCT", direction = "in", measure = "amount", numerator = F_("txn.type", "eq", "CashIn") })
        });
        var d = At(2026, 4, 16, "10:00:00");
        await Send(new[]
        {
            T("W-AGT", m1.W, 8000, d, type: "CashIn"), T("W-PAYER", m1.W, 2000, d),
            T("W-AGT", m2.W, 2500, d, type: "CashIn"), T("W-PAYER", m2.W, 7500, d),
            T("W-AGT", m3.W, 8000, d, type: "CashIn"), T("W-PAYER", m3.W, 2000, d),
        });
        await Run(rule, At(2026, 4, 1, "00:00:00"), At(2026, 5, 1, "00:00:00"));
        var a1 = await Alerts(rule, m1.Id);
        Result("COV-BEN-02", a1.Count == 1 && a1[0].EvidenceJson.Contains("Synthetic test benchmark"),
            $"Cash 80 % of inflow vs segment benchmark 20 % (deviation 300 %) → {a1.Count} alert; benchmark value and source in the evidence.");
        Result("COV-BEN-03", (await Alerts(rule, m2.Id)).Count == 0, $"Cash 25 % (deviation 25 %) → {(await Alerts(rule, m2.Id)).Count} alerts.");
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().AnyAsync(x => x.IssueType == "RuleInputMissing" && x.EntityId == m3.Id.ToString() && x.Description.Contains("benchmark:CASH_SHARE_PCT")));
        Result("COV-BEN-04", (await Alerts(rule, m3.Id)).Count == 0 && dq, $"Customer in a segment without a benchmark → {(await Alerts(rule, m3.Id)).Count} alerts and a data-quality item.");

        // Trade data: payment ≥ 20 % above the declared invoice.
        var trader = await Cust(type: "Corporate");
        var inv = await Rule(new { condition = And(F_("txn.hasInvoice", "eq", true), F_("txn.invoiceVariancePct", "gte", 20)) });
        object Pay(string id, decimal amount, decimal invoice, string invCur) => new
        {
            sourceTransactionId = id, type = "P2P", status = "SUCC", eventTime = At(2026, 4, 17, "10:00:00"), channel = "Web", amount, currency = "JOD",
            senderWallet = trader.W, receiverWallet = "W-SUPPLIER", sourceCountry = "JO", destinationCountry = "JO", invoiceNumber = "INV-" + id, invoiceAmount = invoice,
            invoiceCurrency = invCur, goodsCategory = "HS-85"
        };
        var ids = new[] { "COV-INV-" + Guid.NewGuid().ToString("N")[..8], "COV-INV-" + Guid.NewGuid().ToString("N")[..8], "COV-INV-" + Guid.NewGuid().ToString("N")[..8] };
        await Ingest(Pay(ids[0], 12000, 10000, "JOD"), Pay(ids[1], 10100, 10000, "JOD"), Pay(ids[2], 12000, 10000, "USD"));
        var badInvoice = await Ingest(new { sourceTransactionId = "COV-INV-BAD-" + Guid.NewGuid().ToString("N")[..6], type = "P2P", status = "SUCC", eventTime = At(2026, 4, 17, "10:00:00"),
            amount = 10, currency = "JOD", senderWallet = trader.W, receiverWallet = "W-S", invoiceAmount = 10 });
        await Run(inv, At(2026, 4, 17, "00:00:00"), At(2026, 4, 18, "00:00:00"));
        var hits = await F.WithDb(db => db.Set<AmlAlertTransaction>().Where(x => db.Set<AmlAlert>().Any(a => a.Id == x.AlertId && a.RuleId == inv))
            .Join(db.Set<Transaction>(), x => x.TransactionId, t => t.Id, (x, t) => t.SourceTransactionId).ToListAsync());
        Result("COV-INV-01", hits.Contains(ids[0]) && !hits.Contains(ids[1]) && !hits.Contains(ids[2]),
            $"Payment 12,000 vs invoice 10,000 → alerted; 10,100 (+1 %) → not alerted; invoice in another currency → not compared (no alert).");
        Result("COV-INV-02", badInvoice["records"]![0]!["status"]!.GetValue<string>() == "DeadLetter",
            $"invoiceAmount without invoiceCurrency → {badInvoice["records"]![0]!["status"]} ({badInvoice["records"]![0]!["error"]}).");
        Done();
    }

    [Fact]
    public async Task Risk_weighted_thresholds_and_lookup_lists()
    {
        var high = await Cust(); var low = await Cust(); var low2 = await Cust();
        // Test-data setup: current risk profiles (normally produced by the risk assessment).
        foreach (var (id, level) in new[] { (high.Id, "High"), (low.Id, "Low"), (low2.Id, "Low") })
            await F.ExecSql($"DELETE FROM CurrentCustomerRiskProfiles WHERE CustomerId = {id}; INSERT INTO CurrentCustomerRiskProfiles (TenantId, CustomerId, Score, Level, CalculatedScore, CalculatedLevel, MonitoringLevel, OverrideApplied, SanctionsWorkflow, UpdatedAt) SELECT TenantId, Id, 50, '{level}', 50, '{level}', 'Standard', 0, 0, SYSUTCDATETIME() FROM Customers WHERE Id = {id};");
        var byRisk = await Rule(new { condition = new { field = "txn.amount", @operator = "gte", value = 10000, valueBy = new { field = "customer.riskLevel", values = new Dictionary<string, decimal> { ["High"] = 3000, ["Critical"] = 1000 } } } });
        var d = At(2026, 4, 18, "10:00:00");
        await Send(new[] { T(high.W, "W-X", 3500, d), T(low.W, "W-X", 3500, d), T(low2.W, "W-X", 12000, d) });
        await Run(byRisk, At(2026, 4, 18, "00:00:00"), At(2026, 4, 19, "00:00:00"));
        var ah = await Alerts(byRisk, high.Id);
        Result("COV-RW-01", ah.Count == 1 && ah[0].EvidenceJson.Contains("customer.riskLevel"),
            $"High-risk customer 3,500 (threshold 3,000 for High) → {ah.Count} alert; chosen threshold and risk level in the evidence.");
        Result("COV-RW-02", (await Alerts(byRisk, low.Id)).Count == 0 && (await Alerts(byRisk, low2.Id)).Count == 1,
            $"Low-risk customer 3,500 (default 10,000) → {(await Alerts(byRisk, low.Id)).Count} alerts; 12,000 → {(await Alerts(byRisk, low2.Id)).Count} alert.");

        // Jurisdiction weighting from the COUNTRY lookup (HighRisk / Elevated attributes) and list membership.
        var c = new List<(long Id, string W)>();
        for (var i = 0; i < 4; i++) c.Add(await Cust());
        var byCountry = await Rule(new
        {
            condition = And(F_("txn.isCrossBorder", "eq", true),
                new { field = "txn.amount", @operator = "gte", value = 10000, valueBy = new { field = "txn.destinationCountryRisk", values = new Dictionary<string, decimal> { ["HighRisk"] = 500, ["Elevated"] = 2000 } } })
        });
        var list = await Rule(new { condition = F_("txn.destinationCountry", "inList", "COUNTRY:HighRisk") });
        var d2 = At(2026, 4, 19, "10:00:00");
        await Send(new[] { T(c[0].W, "W-IR", 600, d2, dst: "IR"), T(c[1].W, "W-IQ", 1500, d2, dst: "IQ"), T(c[2].W, "W-IQ", 2500, d2, dst: "IQ"), T(c[3].W, "W-AE", 6000, d2, dst: "AE") });
        foreach (var r in new[] { byCountry, list }) await Run(r, At(2026, 4, 19, "00:00:00"), At(2026, 4, 20, "00:00:00"));
        var n = new List<int>();
        foreach (var x in c) n.Add((await Alerts(byCountry, x.Id)).Count);
        Result("COV-RW-03", n.SequenceEqual(new[] { 1, 0, 1, 0 }),
            $"Thresholds by destination risk (HighRisk 500, Elevated 2,000, default 10,000): IR 600 → {n[0]}, IQ 1,500 → {n[1]}, IQ 2,500 → {n[2]}, AE 6,000 → {n[3]} alerts.");
        var l0 = (await Alerts(list, c[0].Id)).Count; var l3 = (await Alerts(list, c[3].Id)).Count;
        Result("COV-LIST-01", l0 == 1 && l3 == 0, $"destinationCountry inList COUNTRY:HighRisk (maintained lookup, not literals in the rule): IR → {l0} alert, AE → {l3} alerts.");
        var errors = Validate(new { condition = F_("txn.destinationCountry", "inList", "high risk countries") });
        var errors2 = Validate(new { condition = new { field = "txn.amount", @operator = "between", value = new[] { 1, 2 }, valueBy = new { field = "txn.amount", values = new Dictionary<string, decimal> { ["x"] = 1 } } } });
        Result("COV-LIST-02", errors.Any(e => e.Contains("lookup reference")) && errors2.Any(e => e.Contains("valueBy")),
            $"Malformed list reference and invalid valueBy rejected: {errors.FirstOrDefault()} | {errors2.FirstOrDefault()}.");
        Done();
    }

    [Fact]
    public async Task Household_aggregation()
    {
        var hh = "HH-" + Guid.NewGuid().ToString("N")[..8];
        var m1 = await Cust(household: hh); var m2 = await Cust(household: hh); var s1 = await Cust(); var s2 = await Cust();
        var detail = await (await As("manager")).GetOk($"/api/customers/{m1.Id}");
        var rule = await Rule(new { subject = "Household", window = new { type = "Calendar", unit = "Day" }, condition = Sum(null, "gte", 10000, "in") });
        var d = At(2026, 4, 20, "10:00:00");
        await Send(new[]
        {
            T("W-AGT", m1.W, 6000, d, type: "CashIn"), T("W-AGT", m2.W, 6000, d.AddHours(1), type: "CashIn"),
            T(m1.W, m2.W, 3000, d.AddMinutes(30)),
            T("W-AGT", s1.W, 6000, d, type: "CashIn"), T("W-AGT", s2.W, 6000, d.AddHours(1), type: "CashIn"),
        });
        await Run(rule, At(2026, 4, 20, "00:00:00"), At(2026, 4, 21, "00:00:00"));
        var a = await AlertsBySubject(rule, $"household:{hh}");
        Result("COV-HH-01", detail["householdId"]?.GetValue<string>() == hh && a.Count == 1 && a[0].EvidenceJson.Contains(m2.Id.ToString()),
            $"Two members of household {hh} each receive 6,000 cash → {a.Count} household alert (members in the subject context).");
        var hsum = a.Count == 1 ? JsonNode.Parse(a[0].EvidenceJson)!["evidence"]!["sum"]!.GetValue<decimal>() : 0;
        Result("COV-HH-02", a.Count == 1 && await TxnCount(a[0].Id) == 2 && hsum == 12000,
            $"Transfer of 3,000 between the members is internal to the household → not counted (household inflow {hsum}, {(a.Count == 1 ? await TxnCount(a[0].Id) : 0)} contributing transactions).");
        Result("COV-HH-03", (await AlertsBySubject(rule, $"customer:{s1.Id}")).Count == 0 && (await AlertsBySubject(rule, $"customer:{s2.Id}")).Count == 0,
            "Two unrelated customers (no household) with 6,000 each → 0 alerts (each is its own household).");
        Done();
    }

    [Fact]
    public async Task Cash_transaction_reports()
    {
        var c1 = await Cust(); var c2 = await Cust(); var c3 = await Cust();
        var agent = await Cust(type: "Agent");
        var day = new DateOnly(2026, 4, 22);
        var d = At(2026, 4, 22, "09:00:00");
        var rev = "COV-CTR-R-" + Guid.NewGuid().ToString("N")[..8];
        await Send(new[]
        {
            T(agent.W, c1.W, 6000, d, type: "CashIn"), T(c1.W, agent.W, 5000, d.AddHours(2), type: "CashOut"),
            T(agent.W, c2.W, 9000, d, type: "CashIn"),
            T(agent.W, c3.W, 7000, d, type: "CashIn", id: rev), T(agent.W, c3.W, 4000, d.AddHours(1), type: "CashIn"),
            T(c1.W, c2.W, 50000, d.AddHours(3)),
        });
        // Partial reversal of 2,000 on c3's first cash-in: net 5,000 + 4,000 = 9,000 (< 10,000).
        await Send(new[] { T(c3.W, agent.W, 2000, d.AddHours(4), type: "CashIn", original: rev, reversal: true) });
        var officer = await As("officer");
        var gen = await officer.PostOk("/api/ctr-reports/generate", new { businessDate = day });
        var list = (await officer.GetOk($"/api/ctr-reports?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).AsArray();
        var mine = list.Where(x => new[] { c1.Id, c2.Id, c3.Id, agent.Id }.Contains(x!["customerId"]!.GetValue<long>())).ToList();
        var r1 = mine.FirstOrDefault(x => x!["customerId"]!.GetValue<long>() == c1.Id);
        Result("COV-CTR-01", mine.Count == 1 && r1 is not null && r1["totalBaseAmount"]!.GetValue<decimal>() == 11000 && r1["transactionCount"]!.GetValue<int>() == 2 && r1["status"]!.GetValue<string>() == "Draft",
            $"Cash in 6,000 + cash out 5,000 on one day → CTR draft {r1?["reportNumber"]} total {r1?["totalBaseAmount"]} ({r1?["transactionCount"]} transactions); 9,000 and net-of-reversal 9,000 → no report; the agent is not a subject; the 50,000 transfer is not cash.");
        var again = await officer.PostOk("/api/ctr-reports/generate", new { businessDate = day });
        Result("COV-CTR-02", again["created"]!.GetValue<int>() == 0 && again["updated"]!.GetValue<int>() >= 1, $"Second generation for the same date → created {again["created"]}, refreshed {again["updated"]} (idempotent).");
        var id = r1!["id"]!.GetValue<long>();
        var mlro = await As("mlro");
        var export0 = await mlro.Get($"/api/ctr-reports/{id}/export");
        await mlro.PostOk($"/api/ctr-reports/{id}/approve", new { comment = "reviewed" });
        var export = await mlro.Get($"/api/ctr-reports/{id}/export?format=xml");
        var xml = await export.Response.Content.ReadAsStringAsync();
        var csv = await (await mlro.Get($"/api/ctr-reports/{id}/export?format=csv")).Response.Content.ReadAsStringAsync();
        Result("COV-CTR-03", (int)export0.Status == 409 && (int)export.Status == 200 && xml.Contains(r1["reportNumber"]!.GetValue<string>()) && xml.Contains("placeholder-to-be-confirmed") && csv.Split('\n').Length >= 3,
            $"Export before approval → HTTP {(int)export0.Status}; after MLRO approval XML ({xml.Length} chars, placeholder schema flagged) and CSV exported.");
        var sub = await mlro.PostOk($"/api/ctr-reports/{id}/manual-submission", new { reference = "FIU-MANUAL-REF-TEST" });
        var third = await officer.PostOk("/api/ctr-reports/generate", new { businessDate = day });
        Result("COV-CTR-04", sub["report"]!["status"]!.GetValue<string>() == "Submitted" && third["unchangedFiled"]!.GetValue<int>() >= 1,
            $"Manual submission recorded (status {sub["report"]!["status"]}); regeneration leaves the filed report unchanged ({third["unchangedFiled"]} filed). Nothing is sent to an FIU automatically.");
        var noPerm = await (await As("officer")).Post($"/api/ctr-reports/{id}/approve", new { });
        Result("COV-CTR-05", (int)noPerm.Status == 403, $"Compliance officer approving a CTR → HTTP {(int)noPerm.Status}.");
        gen["threshold"]!.GetValue<decimal>().Should().Be(10000);
        Done();
    }
}
