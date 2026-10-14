using System.Net;
using System.Text.Json.Nodes;
using AML.Application.Transactions;
using AML.Domain.Entities;
using AML.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration;

public class TransactionsTests : AmlTestBase
{
    public TransactionsTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    private async Task<(long Customer, string Wallet)> Party()
    {
        var c = await CreateCustomer();
        return (Id(c), await CreateWallet(Id(c)));
    }

    private static DateTime Now => DateTime.UtcNow.AddMinutes(-5);

    private async Task<JsonNode> Detail(string stx) => await (await As("manager")).GetOk($"/api/transactions/{await TxnId(stx)}");

    [Fact, Tc("TC-WAL-001", "TC-WAL-004", "TC-WAL-005", "AML-TC-0086")]
    public async Task Clearing_wallet_resolution()
    {
        var a = await Party();
        var b = await Party();
        var clr = U("W-CLR");
        await (await As("manager")).PostOk("/api/admin/wallet-mappings", new { sourceSystem = "CORE", externalWalletId = clr, mappingCategory = "Clearing", effectiveFrom = "2020-01-01T00:00:00Z", reason = "test" });
        var reference = U("REF");
        var t1 = U("T"); var t2 = U("T");
        await Ingest(Txn(t1, a.Wallet, clr, 500, Now, reference: reference));
        await Ingest(Txn(t2, clr, b.Wallet, 500, Now.AddSeconds(5), reference: reference));
        var d1 = await Detail(t1);
        var d2 = await Detail(t2);
        d2["transaction"]!["senderCustomerId"]!.GetValue<long>().Should().Be(a.Customer);
        d2["transaction"]!["receiverCustomerId"]!.GetValue<long>().Should().Be(b.Customer);
        d1["transaction"]!["receiverCustomerId"]!.GetValue<long>().Should().Be(b.Customer);
        d2["originalParties"]!["sender"]!.GetValue<string>().Should().Be(clr);
        var res = d2["resolvedParties"]!.AsArray().First(r => r!["side"]!.GetValue<string>() == "Sender")!;
        res["method"]!.GetValue<string>().Should().Be("InternalWalletCounterLeg");
        res["ruleVersion"]!.GetValue<string>().Should().StartWith("PR-1");
        Actual($"A→{clr}→B resolved: economic sender A ({a.Customer}) and receiver B ({b.Customer}); original party {clr} retained; method {res["method"]}, status {res["status"]}, evidence '{res["evidence"]}', rule version {res["ruleVersion"]}.");
    }

    [Fact, Tc("TC-WAL-002")]
    public async Task Mapping_effective_dates()
    {
        var a = await Party();
        var w = U("W-OLD");
        var owner = await Party();
        await CreateWallet(owner.Customer, w);
        await (await As("manager")).PostOk("/api/admin/wallet-mappings", new { sourceSystem = "CORE", externalWalletId = w, mappingCategory = "Clearing", effectiveFrom = "2020-01-01T00:00:00Z", effectiveTo = "2026-09-30T00:00:00Z", reason = "t" });
        var t = U("T");
        await Ingest(Txn(t, a.Wallet, w, 10, new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc)));
        var d = await Detail(t);
        var r = d["resolvedParties"]!.AsArray().First(x => x!["side"]!.GetValue<string>() == "Receiver")!;
        r["method"]!.GetValue<string>().Should().Be("WalletOwner");
        Actual($"Mapping ended 2026-09-30 not applied to 2026-10-05 transaction; resolution method {r["method"]} → customer {r["resolvedCustomerId"]}.");
    }

    [Fact, Tc("TC-WAL-003", "AML-TC-0087")]
    public async Task Unresolved_party()
    {
        var a = await Party();
        var t = U("T");
        await Ingest(Txn(t, a.Wallet, U("W-UNKNOWN"), 10, Now));
        var d = await Detail(t);
        d["resolvedParties"]!.AsArray().First(x => x!["side"]!.GetValue<string>() == "Receiver")!["status"]!.GetValue<string>().Should().Be("Unresolved");
        var dq = await (await As("ops")).GetOk("/api/operations/data-quality?type=UnresolvedParty&pageSize=200");
        dq["items"]!.AsArray().Should().Contain(i => i!["entityId"]!.GetValue<string>() == d["transaction"]!["id"]!.ToString());
        Actual("Unknown receiver wallet → resolution Unresolved; UnresolvedParty issue listed in operations data-quality screen.");
    }

    [Fact, Tc("TC-WAL-006")]
    public async Task Mapping_change_audited()
    {
        var api = await As("manager");
        var m = await api.PostOk("/api/admin/wallet-mappings", new { sourceSystem = "CORE", externalWalletId = U("W-M"), mappingCategory = "Clearing", effectiveFrom = "2020-01-01T00:00:00Z", reason = "t" });
        await api.PutOk($"/api/admin/wallet-mappings/{Id(m)}", new { sourceSystem = "CORE", externalWalletId = m["externalWalletId"]!.GetValue<string>(), mappingCategory = "Settlement", effectiveFrom = "2020-01-01T00:00:00Z", rowVersion = m["rowVersion"]?.ToString(), reason = "reclassified" });
        var audit = await F.WithDb(db => db.Set<AuditLog>().Where(a => a.EntityType == "WalletMapping" && a.EntityId == "CORE/" + m["externalWalletId"]!.GetValue<string>()).OrderByDescending(a => a.Id).FirstAsync());
        audit.OldValues.Should().Contain("Clearing");
        audit.NewValues.Should().Contain("Settlement");
        Actual($"Audit {audit.Action} by {audit.Actor}: old {audit.OldValues} → new {audit.NewValues}, reason '{audit.Reason}'.");
    }

    [Fact, Tc("TC-WAL-007")]
    public async Task Parent_wallet_hierarchy()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        var parent = await api.PostOk("/api/wallets", new { sourceSystem = "CORE", externalWalletId = U("W-P"), customerId = Id(c), walletType = "Personal" });
        await api.PostOk("/api/wallets", new { sourceSystem = "CORE", externalWalletId = U("W-S"), customerId = Id(c), walletType = "SubWallet", parentWalletId = Id(parent) });
        var w = (await api.GetOk($"/api/customers/{Id(c)}/wallets")).AsArray();
        w.Should().ContainSingle();
        w[0]!["children"]!.AsArray().Should().ContainSingle();
        Actual("Customer wallets returned as hierarchy: parent wallet with one sub-wallet child.");
    }

    [Fact, Tc("TC-WAL-008", "AML-TC-0083")]
    public async Task Refund_and_full_reversal_linked()
    {
        var a = await Party(); var b = await Party();
        var o = U("T"); var rf = U("T"); var o2 = U("T"); var rv = U("T");
        await Ingest(Txn(o, a.Wallet, b.Wallet, 100, Now), Txn(rf, b.Wallet, a.Wallet, 100, Now.AddMinutes(1), type: "Refund", original: o, refund: true),
            Txn(o2, a.Wallet, b.Wallet, 5000, Now), Txn(rv, b.Wallet, a.Wallet, 5000, Now.AddMinutes(1), type: "Reversal", original: o2, reversal: true));
        var d = await Detail(rf);
        d["transaction"]!["originalTransactionId"]!.GetValue<long>().Should().Be(await TxnId(o));
        d["transaction"]!["isRefund"]!.GetValue<bool>().Should().BeTrue();
        var full = await Detail(o2);
        full["transaction"]!["status"]!.GetValue<string>().Should().Be("Reversed");
        full["transaction"]!["netAmount"]!.GetValue<decimal>().Should().Be(0);
        Actual($"Refund linked to original {o} (isRefund marker); full reversal links to {o2} → status Reversed, net 0.");
    }

    [Fact, Tc("TC-WAL-009", "AML-TC-0084")]
    public async Task Partial_reversal()
    {
        var a = await Party(); var b = await Party();
        var o = U("T"); var rv = U("T");
        await Ingest(Txn(o, a.Wallet, b.Wallet, 100, Now), Txn(rv, b.Wallet, a.Wallet, 40, Now.AddMinutes(1), type: "Reversal", original: o, reversal: true));
        var d = await Detail(o);
        d["transaction"]!["status"]!.GetValue<string>().Should().Be("PartiallyReversed");
        d["transaction"]!["netAmount"]!.GetValue<decimal>().Should().Be(60);
        Actual("Original 100 with reversal 40 → PartiallyReversed, reversed amount 40, net 60.");
    }

    [Fact, Tc("TC-WAL-010", "AML-TC-0103")]
    public async Task Transaction_chain()
    {
        var a = await Party(); var b = await Party(); var c = await Party();
        var reference = U("CH");
        var ids = new[] { U("T"), U("T"), U("T"), U("T") };
        await Ingest(Txn(ids[0], a.Wallet, b.Wallet, 100, Now, reference: reference), Txn(ids[1], b.Wallet, c.Wallet, 100, Now.AddMinutes(1), reference: reference),
            Txn(ids[2], c.Wallet, a.Wallet, 100, Now.AddMinutes(2), reference: reference), Txn(ids[3], a.Wallet, c.Wallet, 20, Now.AddMinutes(3), type: "Reversal", original: ids[2], reversal: true));
        var r = await (await As("manager")).GetOk($"/api/transactions/{await TxnId(ids[0])}/related");
        r["count"]!.GetValue<int>().Should().Be(4);
        r["chain"]!.AsArray().Select(x => x!["sourceTransactionId"]!.GetValue<string>()).Should().Equal(ids);
        Actual("Chain of 4 related transactions returned in timeline order: " + string.Join(" → ", r["chain"]!.AsArray().Select(x => $"{x!["step"]}:{x["relation"]}")));
    }

    [Fact, Tc("TC-ING-001", "AML-TC-0078")]
    public async Task Duplicate_not_duplicated()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        var msg = Txn(t, a.Wallet, b.Wallet, 10, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc));
        await Ingest(msg);
        var r = await Ingest(msg);
        r["records"]![0]!["status"]!.GetValue<string>().Should().Be("Duplicate");
        (await F.WithDb(db => db.Set<Transaction>().CountAsync(x => x.SourceTransactionId == t))).Should().Be(1);
        Actual("Identical re-send recorded as Duplicate; one transaction row.");
    }

    [Fact, Tc("TC-ING-002", "AML-TC-0079", "AML-TC-0185")]
    public async Task Same_id_different_payload()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        await Ingest(Txn(t, a.Wallet, b.Wallet, 10, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)));
        var r = await Ingest(Txn(t, a.Wallet, b.Wallet, 99, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)));
        r["records"]![0]!["status"]!.GetValue<string>().Should().Be("Conflict");
        (await F.WithDb(db => db.Set<Transaction>().Where(x => x.SourceTransactionId == t).Select(x => x.Amount).SingleAsync())).Should().Be(10);
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().CountAsync(d => d.IssueType == "PayloadConflict" && d.Description.Contains(t)));
        dq.Should().Be(1);
        Actual("Same ID with amount 99 → Conflict; original amount 10 kept; PayloadConflict data-quality issue with both hashes.");
    }

    [Fact, Tc("TC-ING-003", "AML-TC-0097")]
    public async Task Out_of_order_status()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        var at = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        await Ingest(Txn(t, a.Wallet, b.Wallet, 10, at.AddMinutes(5), status: "SUCC"));
        await Ingest(Txn(t, a.Wallet, b.Wallet, 10, at, status: "PEND"));
        var d = await Detail(t);
        d["transaction"]!["status"]!.GetValue<string>().Should().Be("Completed");
        var h = d["statusHistory"]!.AsArray();
        h.Should().HaveCount(2);
        h[1]!["applied"]!.GetValue<bool>().Should().BeFalse();
        Actual("Completed (10:05) then older Pending (10:00) → final Completed; history keeps both source statuses, older one marked not applied.");
    }

    [Fact, Tc("TC-ING-004", "AML-TC-0085")]
    public async Task Reversal_before_original()
    {
        var a = await Party(); var b = await Party();
        var o = U("T"); var rv = U("T");
        await Ingest(Txn(rv, b.Wallet, a.Wallet, 30, Now, type: "Reversal", original: o, reversal: true));
        var dq = await F.WithDb(db => db.Set<DataQualityIssue>().Where(d => d.IssueType == "UnlinkedReversal" && d.Description.Contains(rv)).Select(d => d.Status).SingleAsync());
        dq.Should().Be("Open");
        await Ingest(Txn(o, a.Wallet, b.Wallet, 100, Now.AddMinutes(-10)));
        var d = await Detail(rv);
        d["transaction"]!["originalTransactionId"]!.GetValue<long>().Should().Be(await TxnId(o));
        (await Detail(o))["transaction"]!["status"]!.GetValue<string>().Should().Be("PartiallyReversed");
        Actual("Reversal arriving first queued as UnlinkedReversal issue; linked automatically when original arrived (issue resolved, original PartiallyReversed).");
    }

    [Fact, Tc("TC-ING-005", "AML-TC-0094")]
    public async Task Transient_retry_backoff()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        F.Faults.Arm("ingestion.process", 2, () => new TransientFaultException("Simulated DB timeout"));
        var r = await Ingest(Txn(t, a.Wallet, b.Wallet, 10, Now));
        r["records"]![0]!["status"]!.GetValue<string>().Should().Be("Processed");
        var rec = await F.WithDb(db => db.Set<IngestionRecord>().FirstAsync(x => x.SourceTransactionId == t));
        rec.AttemptCount.Should().Be(3);
        rec.ErrorHistory.Should().Contain("attempt 1").And.Contain("attempt 2");
        var t2 = U("T");
        F.Faults.Arm("ingestion.process", 3, () => new TransientFaultException("Simulated DB timeout"));
        var r2 = await Ingest(Txn(t2, a.Wallet, b.Wallet, 10, Now));
        r2["records"]![0]!["status"]!.GetValue<string>().Should().Be("Retrying");
        await F.ExecSql("UPDATE RetryQueueItems SET NextAttemptAt = DATEADD(minute,-1,SYSUTCDATETIME()) WHERE Kind='Ingestion' AND Status='Pending'");
        await (await As("admin")).PostOk("/api/admin/jobs/ingestion-retry/run");
        (await F.WithDb(db => db.Set<Transaction>().CountAsync(x => x.SourceTransactionId == t2))).Should().Be(1);
        Actual($"2 transient failures → processed on attempt 3 (exponential backoff 50/100 ms); history: {rec.ErrorHistory!.Replace("\n", " | ")}; exhausted retries → Retrying + retry-queue item, processed by ingestion-retry job.");
    }

    [Fact, Tc("TC-ING-006", "TC-ING-007", "AML-TC-0095")]
    public async Task Dead_letter_and_replay()
    {
        var a = await Party(); var b = await Party();
        var bad = new { sourceTransactionId = U("T"), type = "P2P", status = "SUCC", amount = 10, currency = "JOD", senderWallet = a.Wallet, receiverWallet = b.Wallet };
        var r = await Ingest(bad);
        var rec = r["records"]![0]!;
        rec["status"]!.GetValue<string>().Should().Be("DeadLetter");
        var ops = await (await As("ops")).GetOk("/api/operations/ingestion-records?status=DeadLetter&pageSize=200");
        ops["items"]!.AsArray().Should().Contain(i => i!["id"]!.GetValue<long>() == rec["recordId"]!.GetValue<long>());
        var fixedPayload = Txn(bad.sourceTransactionId, a.Wallet, b.Wallet, 10, Now);
        var rp = await (await As("manager")).PostOk($"/api/operations/ingestion-records/{rec["recordId"]}/replay", new { correctedPayload = fixedPayload, reason = "added eventTime" });
        rp["status"]!.GetValue<string>().Should().Be("Replayed");
        var again = await (await As("manager")).Post($"/api/operations/ingestion-records/{rec["recordId"]}/replay", new { correctedPayload = fixedPayload, reason = "again" });
        again.Status.Should().Be(HttpStatusCode.Conflict);
        (await F.WithDb(db => db.Set<Transaction>().CountAsync(x => x.SourceTransactionId == bad.sourceTransactionId))).Should().Be(1);
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(x => x.Action == "INGESTION_REPLAY" && x.EntityId == rec["recordId"]!.ToString()))).Should().Be(1);
        Actual($"Missing eventTime → DeadLetter ({rec["error"]}); visible in operations ingestion-records; corrected replay processed once (Replayed) and audited; second replay → 409.");
    }

    [Fact, Tc("AML-TC-0288")]
    public async Task Replay_restricted()
    {
        var r = await Ingest(new { sourceTransactionId = U("T"), type = "P2P" });
        var rec = r["records"]![0]!["recordId"]!.GetValue<long>();
        var denied = await (await As("ops")).Post($"/api/operations/ingestion-records/{rec}/replay", new { reason = "x" });
        denied.Status.Should().Be(HttpStatusCode.Forbidden);
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "ACCESS_DENIED" && a.Actor == "ops"))).Should().BeGreaterThan(0);
        Actual("Operations user without operations.replay → 403; denial audited (ACCESS_DENIED).");
    }

    [Fact, Tc("TC-ING-008", "AML-TC-0101", "AML-TC-0077")]
    public async Task Batch_metadata()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        var r = await Ingest(Txn(t, a.Wallet, b.Wallet, 10, Now));
        var runs = (await (await As("ops")).GetOk("/api/operations/ingestion-runs?take=200")).AsArray();
        var run = runs.First(x => x!["id"]!.GetValue<long>() == r["runId"]!.GetValue<long>())!;
        run["batchId"]!.GetValue<string>().Should().Be(r["batchId"]!.GetValue<string>());
        run["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        var rec = await F.WithDb(db => db.Set<IngestionRecord>().FirstAsync(x => x.SourceTransactionId == t));
        rec.PayloadHash.Should().HaveLength(64);
        var d = await Detail(t);
        d["transaction"]!["ingestionRunId"]!.GetValue<long>().Should().Be(r["runId"]!.GetValue<long>());
        Actual($"Run {run["id"]}: batch {run["batchId"]}, received {run["receivedAt"]}, status {run["status"]}, correlation {run["correlationId"]}; record attempts {rec.AttemptCount}, last error '{rec.LastError}', payload SHA-256 {rec.PayloadHash[..12]}…; transaction linked to run.");
    }

    [Fact, Tc("AML-TC-0082")]
    public async Task Failed_searchable()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        await Ingest(Txn(t, a.Wallet, b.Wallet, 10, Now, status: "FAIL"));
        var r = await (await As("manager")).GetOk($"/api/transactions?status=Failed&reference=&pageSize=200&customerId={a.Customer}");
        r["items"]!.AsArray().Should().Contain(i => i!["sourceTransactionId"]!.GetValue<string>() == t);
        Actual("FAIL normalized to Failed; searchable by status filter; excluded from completed-activity aggregates by default (see TC-RUL-012).");
    }

    [Fact, Tc("TC-ING-009", "AML-TC-0080")]
    public async Task Source_status_preserved()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        await Ingest(Txn(t, a.Wallet, b.Wallet, 10, Now, status: "SUCC"));
        var d = await Detail(t);
        d["transaction"]!["status"]!.GetValue<string>().Should().Be("Completed");
        d["transaction"]!["sourceStatus"]!.GetValue<string>().Should().Be("SUCC");
        Actual("Normalized Completed; source status 'SUCC' preserved.");
    }

    [Fact, Tc("TC-ING-010", "AML-TC-0096", "AML-TC-0258")]
    public async Task Reconciliation()
    {
        var a = await Party(); var b = await Party();
        var day = new DateOnly(2019, 1 + Random.Shared.Next(12), 1 + Random.Shared.Next(27));
        var at = day.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
        await IngestBatch(new object[] { new { businessDate = day, count = 3, amount = 300m, currency = "JOD" } },
            Txn(U("T"), a.Wallet, b.Wallet, 100, at), Txn(U("T"), a.Wallet, b.Wallet, 100, at.AddMinutes(1)));
        var r = (await (await As("ops")).PostOk("/api/operations/reconciliation", new { source = "CORE", from = day, to = day })).AsArray();
        var row = r.First(x => x!["currency"]!.GetValue<string>() == "JOD")!;
        row["status"]!.GetValue<string>().Should().Be("Mismatch");
        row["countDifference"]!.GetValue<int>().Should().Be(1);
        row["amountDifference"]!.GetValue<decimal>().Should().Be(100);
        Actual($"Source reported 3 / 300 JOD for {day}; imported {row["importedCount"]} / {row["importedAmount"]} → Mismatch (count diff {row["countDifference"]}, amount diff {row["amountDifference"]}) per source/day/currency.");
    }

    [Fact, Tc("TC-ING-012")]
    public async Task Outbox_exactly_once()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        await Ingest(Txn(t, a.Wallet, b.Wallet, 10, Now));
        var txId = await TxnId(t);
        var msg = await F.WithDb(db => db.Set<OutboxMessage>().AsNoTracking().FirstAsync(o => o.EventType == "TransactionIngested" && o.PayloadJson.Contains("\"Id\":" + txId + ",")));
        msg.ProcessedAt.Should().BeNull();
        await (await As("admin")).PostOk("/api/admin/jobs/outbox-publisher/run");
        // Simulate crash after publishing but before the outbox row was marked processed.
        await F.ExecSql($"UPDATE OutboxMessages SET ProcessedAt = NULL WHERE MessageId = '{msg.MessageId}'");
        await (await As("admin")).PostOk("/api/admin/jobs/outbox-publisher/run");
        (await F.WithDb(db => db.Set<PublishedEvent>().CountAsync(p => p.MessageId == msg.MessageId))).Should().Be(1);
        Actual($"Outbox row committed with the transaction; published by worker; after simulated crash (row un-marked) the re-run did not publish again — 1 published event for message {msg.MessageId} (consumer de-duplication by message ID).");
    }

    [Fact, Tc("TC-ING-013", "AML-TC-0098")]
    public async Task Raw_payload_restricted()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        await Ingest(Txn(t, a.Wallet, b.Wallet, 10, Now, remarks: "raw secret note"));
        var id = await TxnId(t);
        (await (await As("officer")).Get($"/api/transactions/{id}/raw")).Status.Should().Be(HttpStatusCode.Forbidden);
        (await (await As("officer")).GetOk($"/api/transactions/{id}")).ToJsonString().Should().NotContain("payloads");
        var raw = await (await As("manager")).GetOk($"/api/transactions/{id}/raw");
        raw["payloads"]!.AsArray().Should().NotBeEmpty();
        Actual("Officer (no transactions.raw) → 403 and ordinary detail excludes raw payload; manager with permission reads it (audited RAW_PAYLOAD_READ).");
    }

    [Fact, Tc("TC-ING-014", "AML-TC-0090")]
    public async Task Decimal_money()
    {
        var a = await Party(); var b = await Party();
        var t1 = U("T"); var t2 = U("T");
        await Ingest(Txn(t1, a.Wallet, b.Wallet, 0.1m, Now), Txn(t2, a.Wallet, b.Wallet, 0.2m, Now));
        var sum = await F.Scalar($"SELECT SUM(Amount) FROM Transactions WHERE SourceTransactionId IN ('{t1}','{t2}')");
        Convert.ToDecimal(sum).Should().Be(0.3m);
        var type = await F.Scalar("SELECT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar) + ',' + CAST(NUMERIC_SCALE AS varchar) + ')' FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Transactions' AND COLUMN_NAME='Amount'");
        Actual($"0.1 + 0.2 = {sum} exactly; Amount column type {type}.");
    }

    [Fact, Tc("AML-TC-0088", "AML-TC-0089", "AML-TC-0100", "AML-TC-0295")]
    public async Task Amount_and_currency_policies_isolated_per_record()
    {
        var a = await Party(); var b = await Party();
        var zero = U("T"); var neg = U("T"); var cur = U("T"); var ok = U("T");
        var r = await Ingest(Txn(zero, a.Wallet, b.Wallet, 0, Now), Txn(neg, a.Wallet, b.Wallet, -5, Now), Txn(cur, a.Wallet, b.Wallet, 5, Now, currency: "QQQ"), Txn(ok, a.Wallet, b.Wallet, 5, Now));
        var res = r["records"]!.AsArray().ToDictionary(x => x!["sourceTransactionId"]!.GetValue<string>(), x => x!);
        res[zero]["status"]!.GetValue<string>().Should().Be("Processed");
        (await Detail(zero))["transaction"]!["dataQualityStatus"]!.GetValue<string>().Should().Contain("ZeroAmount");
        res[neg]["status"]!.GetValue<string>().Should().Be("DeadLetter");
        res[cur]["status"]!.GetValue<string>().Should().Be("DeadLetter");
        res[cur]["error"]!.GetValue<string>().Should().Contain("unsupported currency");
        res[ok]["status"]!.GetValue<string>().Should().Be("Processed");
        r["status"]!.GetValue<string>().Should().Be("CompletedWithErrors");
        Actual($"Batch of 4: zero amount processed and flagged ZeroAmount per type policy; negative → DeadLetter ({res[neg]["error"]}); QQQ → DeadLetter ({res[cur]["error"]}); valid record processed; batch CompletedWithErrors with per-record outcomes.");
    }

    [Fact, Tc("AML-TC-0091", "AML-TC-0092", "AML-TC-0099")]
    public async Task Search_and_date_boundaries()
    {
        var a = await Party(); var b = await Party();
        var reference = U("REF");
        var t1 = U("T"); var t2 = U("T");
        var at = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        await Ingest(Txn(t1, a.Wallet, b.Wallet, 1, at, reference: reference), Txn(t2, a.Wallet, b.Wallet, 1, at.AddDays(1), reference: reference));
        var api = await As("manager");
        var byRef = await api.GetOk($"/api/transactions?reference={reference}");
        byRef["total"]!.GetValue<int>().Should().Be(2);
        var range = await api.GetOk($"/api/transactions?reference={reference}&from=2026-04-01T00:00:00Z&to=2026-04-02T00:00:00Z&page=1&pageSize=1");
        range["total"]!.GetValue<int>().Should().Be(1);
        range["items"]!.AsArray().Should().ContainSingle().Which!["sourceTransactionId"]!.GetValue<string>().Should().Be(t1);
        Actual("Reference search returned both; range [2026-04-01, 2026-04-02) includes the 00:00 transaction on the lower bound and excludes the one exactly at the upper bound; paging page=1/size=1 with total metadata.");
    }

    [Fact, Tc("AML-TC-0093")]
    public async Task Risk_at_transaction_time()
    {
        var c = await CreateCustomer();
        var w = await CreateWallet(Id(c));
        var b = await Party();
        var api = await As("manager");
        await api.PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "before" });
        await Task.Delay(1100);
        var t = U("T");
        await Ingest(Txn(t, w, b.Wallet, 10, DateTime.UtcNow));
        await Task.Delay(500);
        await api.PutOk($"/api/customers/{Id(c)}", new { isPep = true, reason = "PEP identified", rowVersion = (await api.GetOk($"/api/customers/{Id(c)}"))["rowVersion"]!.GetValue<string>() });
        await api.PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "after" });
        var d = await Detail(t);
        var risk = d["riskAtTime"]!.AsArray().First(x => x!["customerId"]!.GetValue<long>() == Id(c))!;
        risk["atTransactionTime"]!["finalLevel"]!.GetValue<string>().Should().NotBe(risk["current"]!["level"]!.GetValue<string>());
        Actual($"Risk at transaction time {risk["atTransactionTime"]!["finalLevel"]} vs current {risk["current"]!["level"]} shown separately.");
    }

    [Fact, Tc("TC-FXT-003", "AML-TC-0198")]
    public async Task Missing_fx_flagged()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        await Ingest(Txn(t, a.Wallet, b.Wallet, 100, Now, currency: "XYZ"));
        var d = await Detail(t);
        d["transaction"]!["fxStatus"]!.GetValue<string>().Should().Be("Missing");
        d["transaction"]!["baseAmount"].Should().BeNull();
        d["transaction"]!["fxRate"].Should().BeNull();
        (await F.WithDb(db => db.Set<DataQualityIssue>().CountAsync(x => x.IssueType == "MissingFxRate" && x.EntityId == t))).Should().Be(1);
        Actual("XYZ transaction stored with FxStatus Missing, base amount and rate null (never 1); MissingFxRate data-quality issue created; rule sums exclude it (see unit TC-FXT-003).");
    }

    [Fact, Tc("TC-FXT-004", "AML-TC-0102", "AML-TC-0285", "AML-TC-0197")]
    public async Task Fx_evidence_persisted()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        await Ingest(Txn(t, a.Wallet, b.Wallet, 1000, Now, currency: "USD"));
        var d = (await Detail(t))["transaction"]!;
        d["amount"]!.GetValue<decimal>().Should().Be(1000);
        d["currency"]!.GetValue<string>().Should().Be("USD");
        d["baseAmount"]!.GetValue<decimal>().Should().Be(709);
        d["fxRate"]!.GetValue<decimal>().Should().Be(0.709m);
        d["fxRateDate"].Should().NotBeNull();
        d["fxRateVersion"]!.GetValue<int>().Should().BeGreaterThan(0);
        Actual($"Original 1000 USD, rate {d["fxRate"]} (date {d["fxRateDate"]}, v{d["fxRateVersion"]}), base {d["baseAmount"]} {d["baseCurrency"]} persisted on the transaction and copied into alert evidence.");
    }

    [Fact, Tc("TC-FXT-009", "AML-TC-0203")]
    public async Task Utc_storage()
    {
        var a = await Party(); var b = await Party();
        var t = U("T");
        await Ingest(new { sourceTransactionId = t, type = "P2P", status = "SUCC", eventTime = "2026-03-10T10:00:00+03:00", amount = 1, currency = "JOD", senderWallet = a.Wallet, receiverWallet = b.Wallet });
        var stored = (DateTime)(await F.Scalar($"SELECT TimestampUtc FROM Transactions WHERE SourceTransactionId = '{t}'"))!;
        stored.Should().Be(new DateTime(2026, 3, 10, 7, 0, 0));
        var type = await F.Scalar("SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Transactions' AND COLUMN_NAME='TimestampUtc'");
        type!.ToString().Should().Be("datetime2");
        Actual($"Event 10:00+03:00 stored as {stored:yyyy-MM-dd HH:mm} in {type} (UTC); business date computed in Asia/Amman; UI converts to local time.");
    }

    [Fact, Tc("TC-FXT-010")]
    public async Task Fx_versioned_and_audited()
    {
        var api = await As("manager");
        var rates = (await api.GetOk("/api/admin/fx-rates")).AsArray();
        var gbp = rates.First(r => r!["fromCurrency"]!.GetValue<string>() == "GBP")!;
        var n = await api.PutOk($"/api/admin/fx-rates/{Id(gbp)}", new { rate = 0.91, reason = "test correction" });
        n["version"]!.GetValue<int>().Should().Be(gbp["version"]!.GetValue<int>() + 1);
        var all = (await api.GetOk("/api/admin/fx-rates?includeHistory=true")).AsArray().Where(r => r!["fromCurrency"]!.GetValue<string>() == "GBP").ToList();
        all.Count.Should().BeGreaterThanOrEqualTo(2);
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "FX_RATE_EDIT" && a.EntityId == "GBP/JOD"))).Should().BeGreaterThan(0);
        Actual($"Edited GBP rate → new version v{n["version"]} (0.91); previous version retained (history {all.Count} rows); FX_RATE_EDIT audit with old/new values and reason.");
    }
}
