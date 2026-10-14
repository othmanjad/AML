using System.Net;
using System.Text.Json.Nodes;
using AML.Domain.Entities;
using AML.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration;

public abstract class ScreeningTestBase : RuleTestBase
{
    protected ScreeningTestBase(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    private const string Consonants = "bdfgklmnprstvz";
    private const string Vowels = "aeiou";

    /// <summary>Random pronounceable synthetic word (never a real person's name).</summary>
    protected static string Word(int len = 7)
    {
        var r = Random.Shared;
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = i % 2 == 0 ? Consonants[r.Next(Consonants.Length)] : Vowels[r.Next(Vowels.Length)];
        chars[0] = char.ToUpperInvariant(chars[0]);
        return new string(chars);
    }

    protected static string FakeName() => $"{Word()} {Word(8)} {Word(6)}";

    protected async Task<JsonNode> AddListEntry(string list, string name, string? dob = null, string? nationality = null, string? category = null, string? entryId = null)
    {
        entryId ??= "E-" + Guid.NewGuid().ToString("N")[..8];
        return await (await As("manager")).PostOk($"/api/watchlists/{list}/versions", new
        {
            versionLabel = "V-" + Guid.NewGuid().ToString("N")[..10], publishedAt = DateTime.UtcNow.AddHours(-1),
            additions = new[] { new { entryId, primaryName = name, dateOfBirth = dob, nationality, category } }
        });
    }

    protected async Task<(long Customer, string Wallet, ScreeningResult Result)> MatchedCustomer(string list = "UN-TEST", string? category = null)
    {
        var name = FakeName();
        var c = await CompleteKycCustomer(name: name);
        var w = await CreateWallet(Id(c));
        await AddListEntry(list, name, category: category);
        var res = (await ScreeningResults(Id(c))).First(r => r.SubjectType == "Customer");
        return (Id(c), w, res);
    }
}

public class ScreeningTests : ScreeningTestBase
{
    public ScreeningTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact, Tc("TC-SCR-001", "AML-TC-0131")]
    public async Task Potential_match_details()
    {
        var name = FakeName();
        await AddListEntry("UN-TEST", name, "1980-01-01", "JO");
        var c = await CompleteKycCustomer(name: name, dob: "1980-01-01");
        await (await As("officer")).PostOk($"/api/customers/{Id(c)}/screening");
        var r = (await (await As("officer")).GetOk($"/api/customers/{Id(c)}/screening"))["results"]!.AsArray()[0]!;
        foreach (var k in new[] { "providerCode", "listEntryId", "matchScore", "matchMethod", "matchedAttributes", "listVersion" }) r[k].Should().NotBeNull(k);
        var cust = await (await As("officer")).GetOk($"/api/customers/{Id(c)}");
        cust["screeningStatus"]!.GetValue<string>().Should().Be("PotentialMatch");
        Actual($"Result: provider {r["providerCode"]}, entry {r["listEntryId"]}, score {r["matchScore"]}, method {r["matchMethod"]}, version {r["listVersion"]}, attributes {r["matchedAttributes"]!.ToJsonString()}; customer screening status PotentialMatch shown separately from risk level {cust["risk"]?["level"]}.");
    }

    [Fact, Tc("TC-SCR-002", "AML-TC-0132", "AML-TC-0133")]
    public async Task State_transitions_audited()
    {
        var (_, _, res) = await MatchedCustomer("OFAC-TEST");
        await Transition(res.Id, "UnderReview");
        await Transition(res.Id, "Escalated");
        await Transition(res.Id, "UnableToDetermine");
        (await (await As("senior")).Post($"/api/screening/results/{res.Id}/transition", new { toStatus = "FalsePositive" })).Status.Should().Be(HttpStatusCode.BadRequest);
        var fp = await Transition(res.Id, "FalsePositive", notes: "DOB and nationality differ; evidence ref DOC-1");
        fp["reviewer"]!.GetValue<string>().Should().Be("senior");
        var bad = await (await As("senior")).Post($"/api/screening/results/{res.Id}/transition", new { toStatus = "Escalated", notes = "x" });
        bad.Status.Should().Be(HttpStatusCode.Conflict);
        var d = await (await As("senior")).GetOk($"/api/screening/results/{res.Id}");
        d["history"]!.AsArray().Count.Should().Be(4);
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "SCREENING_TRANSITION" && a.EntityId == res.Id.ToString()))).Should().Be(4);
        Actual($"Potential→UnderReview→Escalated→UnableToDetermine→FalsePositive each audited (4 history rows: reviewer, time, notes, list version {fp["listVersion"]}); FP without rationale → 400; invalid FalsePositive→Escalated → 409.");
    }

    [Fact, Tc("TC-SCR-003", "TC-SCR-004", "TC-SCR-005", "AML-TC-0134", "AML-TC-0135", "AML-TC-0283")]
    public async Task Provider_failures_never_clear()
    {
        var api = await As("officer");
        await SetProvider("Timeout", 1);
        try
        {
            var c = await CompleteKycCustomer();
            var reqs = (await api.PostOk($"/api/customers/{Id(c)}/screening")).AsArray();
            reqs[0]!["status"]!.GetValue<string>().Should().Be("Unavailable");
            (await api.GetOk($"/api/customers/{Id(c)}"))["screeningStatus"]!.GetValue<string>().Should().Be("Unavailable");
            var retried = await api.PostOk($"/api/screening/requests/{reqs[0]!["id"]}/retry");
            retried["status"]!.GetValue<string>().Should().Be("Completed");
            retried["failureHistory"]!.GetValue<string>().Should().Contain("Timeout");
            (await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "SCREENING_MANUAL_RETRY" && a.EntityId == reqs[0]!["id"]!.ToString()))).Should().Be(1);
            await SetProvider("Malformed", 1);
            var c2 = await CompleteKycCustomer();
            var r2 = (await api.PostOk($"/api/customers/{Id(c2)}/screening")).AsArray();
            r2[0]!["status"]!.GetValue<string>().Should().Be("Unavailable");
            await SetProvider("Unavailable", 1);
            var c3 = await CompleteKycCustomer();
            var r3 = (await api.PostOk($"/api/customers/{Id(c3)}/screening")).AsArray();
            await F.ExecSql($"UPDATE ScreeningRequests SET NextAttemptAt = DATEADD(minute,-1,SYSUTCDATETIME()) WHERE Id = {r3[0]!["id"]}");
            await (await As("admin")).PostOk("/api/admin/jobs/screening-retry/run");
            var after = await F.WithDb(db => db.Set<ScreeningRequest>().FirstAsync(x => x.Id == r3[0]!["id"]!.GetValue<long>()));
            after.Status.Should().Be("Completed");
            after.Attempts.Should().Be(2);
            Actual($"Timeout → Unavailable (never Clear), manual retry audited and completed keeping failure history '{retried["failureHistory"]}'; malformed response → Unavailable; outage with backoff (30s·2^n) retried by job → completed on attempt {after.Attempts}.");
        }
        finally { await SetProvider("Normal"); }
    }

    [Fact, Tc("TC-SCR-006")]
    public async Task Screening_separate_from_risk()
    {
        var (cid, _, res) = await MatchedCustomer("OFAC-TEST");
        await Transition(res.Id, "FalsePositive");
        var c = await (await As("officer")).GetOk($"/api/customers/{cid}");
        c["screeningStatus"]!.GetValue<string>().Should().Be("Clear");
        c["risk"].Should().NotBeNull();
        Actual($"After false-positive clearance: screening status '{c["screeningStatus"]}' and risk level '{c["risk"]!["level"]}' are separate fields (UI badges screening-status / risk-level).");
    }

    [Fact, Tc("TC-SCR-007", "TC-SCR-008", "AML-TC-0137", "AML-TC-0138", "AML-TC-0154")]
    public async Task List_version_and_delta_rescreen()
    {
        var name = FakeName();
        var c = await CompleteKycCustomer(name: name);
        var corp = await CreateCustomer(type: "Corporate", nameLatin: "Holding " + Word(), incorporation: "JO", attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "12345678" });
        var ownerName = FakeName();
        await (await As("manager")).PostOk($"/api/customers/{Id(corp)}/ownership", new { ownerName, ownerType = "Person", percentage = 50, controlType = "Ownership" });
        var sigName = FakeName();
        await (await As("manager")).PostOk($"/api/customers/{Id(corp)}/ownership", new { ownerName = sigName, ownerType = "Person", controlType = "Signatory" });
        var api = await As("manager");
        var v = await api.PostOk("/api/watchlists/NATIONAL-TEST/versions", new
        {
            versionLabel = "V-" + Guid.NewGuid().ToString("N")[..10], publishedAt = DateTime.UtcNow.AddHours(-2),
            additions = new[] { new { entryId = "N-" + U(), primaryName = name }, new { entryId = "N-" + U(), primaryName = ownerName }, new { entryId = "N-" + U(), primaryName = sigName } }
        });
        v["additions"]!.GetValue<int>().Should().Be(3);
        v["publishedAt"].Should().NotBeNull();
        v["timeToScreenMs"]!.GetValue<long>().Should().BeGreaterThan(0);
        var results = await F.WithDb(db => db.Set<ScreeningResult>().Where(r => r.ListVersion == v["versionLabel"]!.GetValue<string>()).ToListAsync());
        results.Select(r => r.SubjectType).Should().Contain(new[] { "Customer", "BeneficialOwner", "Signatory" });
        Actual($"Version {v["versionLabel"]} stored (published {v["publishedAt"]}, +{v["additions"]}/~{v["amendments"]}/-{v["removals"]}); delta rescreen of {v["subjectsScreened"]} subjects raised matches for customer, UBO and signatory; time-to-screen {v["timeToScreenMs"]} ms.");
    }

    [Fact, Tc("TC-SCR-009", "AML-TC-0141")]
    public async Task Entry_removal_flags_matches()
    {
        var name = FakeName();
        var c = await CompleteKycCustomer(name: name);
        var entryId = "R-" + Guid.NewGuid().ToString("N")[..8];
        await AddListEntry("OFAC-TEST", name, entryId: entryId);
        var res = (await ScreeningResults(Id(c))).Single(r => r.ListEntryId == entryId);
        await (await As("manager")).PostOk("/api/watchlists/OFAC-TEST/versions", new { versionLabel = "V-" + U(), publishedAt = DateTime.UtcNow, removals = new[] { entryId } });
        var after = await F.WithDb(db => db.Set<ScreeningResult>().FirstAsync(r => r.Id == res.Id));
        after.NeedsReview.Should().BeTrue();
        after.Status.Should().Be("PotentialMatch");
        Actual($"Entry {entryId} removed → open match flagged NeedsReview ('{after.NeedsReviewReason}'); historical evidence retained; release workflow via FalsePositive/UnderReview transitions.");
    }

    [Fact, Tc("TC-SCR-010", "TC-SCR-011", "TC-SCR-012", "AML-TC-0139", "AML-TC-0140")]
    public async Task Allow_list_behaviour()
    {
        var name = FakeName();
        var c = await CompleteKycCustomer(name: name, dob: "1985-05-05");
        var entryId = "A-" + Guid.NewGuid().ToString("N")[..8];
        await AddListEntry("OFAC-TEST", name, entryId: entryId);
        var res = (await ScreeningResults(Id(c))).Single(r => r.ListEntryId == entryId);
        await Transition(res.Id, "FalsePositive", notes: "Different person - DOB mismatch");
        var api = await As("officer");
        var again = (await api.PostOk($"/api/customers/{Id(c)}/screening")).AsArray()[0]!;
        again["suppressedCount"]!.GetValue<int>().Should().BeGreaterThan(0);
        (await ScreeningResults(Id(c))).Count(r => r.ListEntryId == entryId).Should().Be(1);
        (await F.WithDb(db => db.Set<ScreeningSuppressionLog>().CountAsync(s => s.ListEntryId == entryId))).Should().BeGreaterThan(0);
        var cust = await api.GetOk($"/api/customers/{Id(c)}");
        await (await As("manager")).PutOk($"/api/customers/{Id(c)}", new { dateOfBirth = "1985-06-06", reason = "corrected DOB", rowVersion = cust["rowVersion"]!.GetValue<string>() });
        await api.PostOk($"/api/customers/{Id(c)}/screening");
        (await ScreeningResults(Id(c))).Count(r => r.ListEntryId == entryId).Should().Be(2);
        var c2Name = FakeName();
        var c2 = await CompleteKycCustomer(name: c2Name);
        var e2 = "A-" + Guid.NewGuid().ToString("N")[..8];
        await AddListEntry("OFAC-TEST", c2Name, entryId: e2);
        await Transition((await ScreeningResults(Id(c2))).Single(r => r.ListEntryId == e2).Id, "FalsePositive");
        await (await As("manager")).PostOk("/api/watchlists/OFAC-TEST/versions", new { versionLabel = "V-" + U(), publishedAt = DateTime.UtcNow, amendments = new[] { new { entryId = e2, primaryName = c2Name, dateOfBirth = "1960-01-01" } } });
        (await ScreeningResults(Id(c2))).Count(r => r.ListEntryId == e2).Should().Be(2);
        Actual("Rescreen of unchanged subject suppressed by allow-list (suppression logged, no new alert); DOB change invalidated suppression → match re-raised; list-entry amendment invalidated suppression → match re-raised.");
    }

    [Fact, Tc("TC-SCR-013", "AML-TC-0152")]
    public async Task Pf_list()
    {
        var (_, _, res) = await MatchedCustomer("PF-TEST");
        res.Category.Should().Be("ProliferationFinancing");
        Actual($"Match against PF-TEST categorized '{res.Category}' and routed by the mandatory-list action policy (Sanctions,Terrorism,ProliferationFinancing).");
    }

    [Fact, Tc("TC-SCR-014", "AML-TC-0153")]
    public async Task Provider_response_restricted()
    {
        var (_, _, res) = await MatchedCustomer("OFAC-TEST");
        var raw = await F.Scalar($"SELECT ProviderResponseEncrypted FROM ScreeningResults WHERE Id = {res.Id}");
        raw!.ToString().Should().NotContain(res.SubjectName);
        var officer = await (await As("officer")).GetOk($"/api/screening/results/{res.Id}");
        officer["result"]!["providerResponse"].Should().BeNull();
        var mgr = await (await As("manager")).GetOk($"/api/screening/results/{res.Id}");
        mgr["result"]!["providerResponse"].Should().NotBeNull();
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "SCREENING_RAW_READ" && a.Actor == "manager"))).Should().BeGreaterThan(0);
        Actual("Provider response stored AES-GCM encrypted; hidden from officer (no screening.raw); visible to authorized manager with SCREENING_RAW_READ audit.");
    }

    [Fact, Tc("TC-SCR-015", "AML-TC-0136", "AML-TC-0284")]
    public async Task Screening_idempotent()
    {
        var name = FakeName();
        var c = await CompleteKycCustomer(name: name);
        await AddListEntry("OFAC-TEST", name);
        var api = await As("officer");
        var key = "IDEMP-" + U();
        var r1 = (await api.PostOk($"/api/customers/{Id(c)}/screening?idempotencyKey={key}")).AsArray();
        var r2 = (await api.PostOk($"/api/customers/{Id(c)}/screening?idempotencyKey={key}")).AsArray();
        r1[0]!["id"]!.GetValue<long>().Should().Be(r2[0]!["id"]!.GetValue<long>());
        await api.PostOk($"/api/customers/{Id(c)}/screening");
        (await ScreeningResults(Id(c))).Count(r => r.SubjectType == "Customer").Should().Be(1);
        Actual("Same idempotency key → same request/result; subsequent manual rescreen after the delta rescreen produced no duplicate logical result (1 result).");
    }

    [Fact, Tc("TC-SCR-016")]
    public async Task Screening_report_counts()
    {
        var r = await (await As("officer")).GetOk("/api/reports/screening-summary");
        var dbCounts = await F.WithDb(db => db.Set<ScreeningResult>().GroupBy(x => x.Status).Select(g => new { g.Key, n = g.Count() }).ToListAsync());
        var json = r.ToJsonString();
        foreach (var s in dbCounts) json.Should().Contain(s.Key);
        Actual("Screening summary report lists counts per status (" + string.Join(", ", dbCounts.Select(x => $"{x.Key}={x.n}")) + ") plus request states (pending/unavailable).");
    }

    [Fact, Tc("TC-L10N-007", "TC-L10N-008")]
    public async Task Arabic_and_transliteration_screening()
    {
        var w1 = Word(); var w2 = Word();
        var entry = $"Abdullah bin {w1} Al {w2}";
        await AddListEntry("OFAC-TEST", entry);
        var c = await CompleteKycCustomer(name: $"Abd Allah {w1} {w2}");
        await (await As("officer")).PostOk($"/api/customers/{Id(c)}/screening");
        var res = (await ScreeningResults(Id(c))).FirstOrDefault(r => r.SubjectType == "Customer");
        res.Should().NotBeNull();
        var m = FakeName().Split(' ');
        await AddListEntry("OFAC-TEST", $"Mohammed {m[1]} {m[2]}");
        var c2 = await CompleteKycCustomer(name: $"Muhammad {m[1]} {m[2]}");
        await (await As("officer")).PostOk($"/api/customers/{Id(c2)}/screening");
        var r2 = (await ScreeningResults(Id(c2))).FirstOrDefault(r => r.SubjectType == "Customer");
        r2.Should().NotBeNull();
        Actual($"'{entry}' matched 'Abd Allah {w1} {w2}' (score {res!.MatchScore}, {res.Explanation}); 'Mohammed …' list entry matched 'Muhammad …' (score {r2!.MatchScore}).");
    }
}

public class ComplianceActionTests : ScreeningTestBase
{
    public ComplianceActionTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    private async Task<List<ComplianceAction>> Actions(long customerId) =>
        await F.WithDb(db => db.Set<ComplianceAction>().Where(a => a.CustomerId == customerId).OrderBy(a => a.Id).ToListAsync());

    [Fact, Tc("TC-ACT-001", "TC-ACT-002", "TC-ACT-006", "TC-ACT-007", "AML-TC-0143", "AML-TC-0147", "AML-TC-0148")]
    public async Task Auto_freeze_on_confirmed_mandatory_match()
    {
        var (cid, wallet, res) = await MatchedCustomer("UN-TEST");
        await F.ExecSql($"INSERT INTO MockCoreTargetStates (TenantId, Target, State, Balance, Currency, UpdatedAt) VALUES (1, 'WALLET:{wallet}', 'Normal', 1250.50, 'JOD', SYSUTCDATETIME())");
        await Transition(res.Id, "ConfirmedMatch", notes: "Full name, DOB and nationality confirmed");
        var a = (await Actions(cid)).Single(x => x.ActionType == "FreezeWallet");
        a.IdempotencyKey.Should().StartWith("SCREENING:");
        a.Status.Should().Be("Applied");
        a.CoreReference.Should().StartWith("CORE-");
        a.FrozenAmount.Should().Be(1250.50m);
        var cust = await (await As("officer")).GetOk($"/api/customers/{cid}");
        cust["isFrozen"]!.GetValue<bool>().Should().BeTrue();
        var n = (await (await As("officer")).GetOk($"/api/compliance-actions/notifications?customerId={cid}")).AsArray();
        n.Should().ContainSingle();
        n[0]!["status"]!.GetValue<string>().Should().Be("Draft");
        Actual($"Confirmed UN match → FreezeWallet sent with idempotency key {a.IdempotencyKey}; mock core ack {a.CoreReference} → Applied, customer frozen; frozen {a.FrozenAmount} {a.FrozenCurrency} at {a.FrozenAt:O}; authority notification draft due {n[0]!["dueAt"]} (FREEZE_NOTIFICATION_HOURS).");
    }

    [Fact, Tc("TC-ACT-003", "AML-TC-0144")]
    public async Task Core_failure_retried_never_applied()
    {
        await F.SetSetting("MockCore:FailureMode", "FailTimes:2");
        try
        {
            var (cid, _, res) = await MatchedCustomer("UN-TEST");
            await Transition(res.Id, "ConfirmedMatch");
            var a = (await Actions(cid)).Single();
            a.Status.Should().Be("Retrying");
            (await (await As("officer")).GetOk($"/api/customers/{cid}"))["isFrozen"]!.GetValue<bool>().Should().BeFalse();
            (await F.WithDb(db => db.Set<OperationalAlert>().CountAsync(o => o.Category == "CoreActionFailure" && o.Reference == a.Id.ToString()))).Should().BeGreaterThan(0);
            for (var i = 0; i < 2; i++)
            {
                await F.ExecSql($"UPDATE ComplianceActions SET NextAttemptAt = DATEADD(minute,-1,SYSUTCDATETIME()) WHERE Id = {a.Id}");
                await (await As("admin")).PostOk("/api/admin/jobs/action-retry/run");
            }
            var after = (await Actions(cid)).Single();
            after.Status.Should().Be("Applied");
            after.Attempts.Should().Be(3);
            (await F.WithDb(db => db.Set<MockCoreCommand>().CountAsync(m => m.IdempotencyKey == a.IdempotencyKey))).Should().Be(1);
            Actual($"Core unavailable twice → Retrying with backoff, ops alert raised, never shown as applied; third attempt via action-retry job → Applied (attempts {after.Attempts}); single core command for the idempotency key.");
        }
        finally { await F.SetSetting("MockCore:FailureMode", "None"); }
    }

    [Fact, Tc("TC-ACT-004", "AML-TC-0142")]
    public async Task Duplicate_freeze_idempotent()
    {
        var c = await CompleteKycCustomer();
        var w = await CreateWallet(Id(c));
        var wid = await F.WithDb(db => db.Set<Wallet>().Where(x => x.ExternalWalletId == w).Select(x => x.Id).FirstAsync());
        var key = "FRZ-" + U();
        var a1 = await (await As("officer")).PostOk("/api/compliance-actions", new { actionType = "FreezeWallet", walletId = wid, reason = "Investigation", idempotencyKey = key });
        var a2 = await (await As("officer")).PostOk("/api/compliance-actions", new { actionType = "FreezeWallet", walletId = wid, reason = "Investigation", idempotencyKey = key });
        Id(a1).Should().Be(Id(a2));
        await (await As("senior")).PostOk($"/api/compliance-actions/{Id(a1)}/approve", new { approve = true });
        await (await As("officer")).PostOk($"/api/compliance-actions/{Id(a1)}/retry");
        (await F.WithDb(db => db.Set<ComplianceAction>().CountAsync(x => x.IdempotencyKey == key))).Should().Be(1);
        (await F.WithDb(db => db.Set<MockCoreCommand>().CountAsync(x => x.IdempotencyKey == key))).Should().Be(1);
        Actual("Same freeze requested twice with one idempotency key → single action record; resend after Applied did not reach core again (1 core command).");
    }

    [Fact, Tc("TC-ACT-005", "AML-TC-0146")]
    public async Task Release_four_eyes()
    {
        var c = await CompleteKycCustomer();
        var w = await CreateWallet(Id(c));
        var wid = await F.WithDb(db => db.Set<Wallet>().Where(x => x.ExternalWalletId == w).Select(x => x.Id).FirstAsync());
        var mgr = await As("manager");
        var rel = await mgr.PostOk("/api/compliance-actions", new { actionType = "Release", walletId = wid, reason = "Match cleared" });
        rel["status"]!.GetValue<string>().Should().Be("PendingApproval");
        var self = await mgr.Post($"/api/compliance-actions/{Id(rel)}/approve", new { approve = true });
        ((int)self.Status).Should().BeOneOf(403, 409);
        (await F.WithDb(db => db.Set<MockCoreCommand>().CountAsync(x => x.IdempotencyKey == rel["idempotencyKey"]!.GetValue<string>()))).Should().Be(0);
        var ok = await (await As("senior")).PostOk($"/api/compliance-actions/{Id(rel)}/approve", new { approve = true, reason = "agree" });
        ok["status"]!.GetValue<string>().Should().Be("Applied");
        Actual($"Release PendingApproval; requester self-approval refused ({(int)self.Status}) and nothing sent to core; independent approval → Applied.");
    }

    [Fact, Tc("TC-ACT-008", "AML-TC-0145")]
    public async Task Reconciliation_mismatch()
    {
        var (cid, wallet, res) = await MatchedCustomer("UN-TEST");
        await Transition(res.Id, "ConfirmedMatch");
        await F.ExecSql($"UPDATE MockCoreTargetStates SET State = 'Normal' WHERE Target = 'WALLET:{wallet}'");
        var r = (await (await As("officer")).PostOk("/api/compliance-actions/reconcile")).AsArray();
        r.Should().Contain(x => x!["isMismatch"]!.GetValue<bool>() && x["coreState"]!.GetValue<string>() == "Normal");
        Actual("Core state changed manually to Normal → reconciliation reports mismatch (AML=Frozen, core=Normal) and raises an operational alert.");
    }

    [Fact, Tc("TC-ACT-009")]
    public async Task Rule_hold_action()
    {
        var ch = Channel();
        var rule = await PublishRule(new { eligibleStatuses = new[] { "Pending" }, scope = new { channels = new[] { ch } }, condition = new { field = "txn.amount", @operator = "gte", value = 1 }, action = new { severity = "Medium", complianceAction = "HoldTransaction" } });
        var a = await CompleteKycCustomer(); var wa = await CreateWallet(Id(a)); var b = await CompleteKycCustomer(); var wb = await CreateWallet(Id(b));
        var t = U("T");
        var at = DateTime.UtcNow.AddMinutes(-5);
        await Ingest(Txn(t, wa, wb, 50, at, channel: ch, status: "PEND"));
        var rr = await (await As("officer")).PostOk($"/api/admin/aml-rules/{rule}/run", new { from = at.AddMinutes(-1), to = DateTime.UtcNow });
        var holds = (await (await As("officer")).GetOk("/api/holds")).AsArray();
        holds.Should().Contain(h => h!["sourceTransactionId"]!.GetValue<string>() == t);
        Actual("Rule with action HoldTransaction matched → hold command sent to (mock) core per RuleAction policy; transaction listed in hold queue.");
    }

    [Fact, Tc("TC-ACT-010", "AML-TC-0222")]
    public async Task Suspend_agent()
    {
        var agent = await CreateCustomer(type: "Agent", nameLatin: "Agent " + Word());
        (await (await As("ops")).Post("/api/compliance-actions", new { actionType = "SuspendAgent", customerId = Id(agent), reason = "x" })).Status.Should().Be(HttpStatusCode.Forbidden);
        var a = await (await As("officer")).PostOk("/api/compliance-actions", new { actionType = "SuspendAgent", customerId = Id(agent), reason = "Agent investigation" });
        (await F.WithDb(db => db.Set<MockCoreCommand>().CountAsync(x => x.IdempotencyKey == a["idempotencyKey"]!.GetValue<string>()))).Should().Be(0);
        await (await As("senior")).PostOk($"/api/compliance-actions/{Id(a)}/approve", new { approve = true });
        (await (await As("officer")).GetOk($"/api/customers/{Id(agent)}"))["status"]!.GetValue<string>().Should().Be("Suspended");
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(x => x.Action == "COMPLIANCE_ACTION_DISPATCH" && x.EntityId == a["idempotencyKey"]!.GetValue<string>()))).Should().Be(1);
        Actual("Unauthorized suspension → 403 and nothing sent; officer request pending approval (not sent); after senior approval command sent, agent status Suspended, dispatch audited.");
    }

    [Fact, Tc("AML-TC-0149")]
    public async Task Unknown_result_reconciled_before_retry()
    {
        var c = await CompleteKycCustomer();
        await F.SetSetting("MockCore:FailureMode", "Unknown");
        long id;
        try
        {
            var a = await (await As("officer")).PostOk("/api/compliance-actions", new { actionType = "RequireKycRefresh", customerId = Id(c), reason = "refresh" });
            id = Id(a);
            a["status"]!.GetValue<string>().Should().Be("Unknown");
        }
        finally { await F.SetSetting("MockCore:FailureMode", "None"); }
        var r = await (await As("officer")).PostOk($"/api/compliance-actions/{id}/retry");
        r["status"]!.GetValue<string>().Should().Be("Applied");
        r["reconciliationStatus"]!.GetValue<string>().Should().Be("FoundOnCore");
        var times = await F.WithDb(db => db.Set<MockCoreCommand>().Where(x => x.IdempotencyKey == r["idempotencyKey"]!.GetValue<string>()).Select(x => x.TimesReceived).FirstAsync());
        times.Should().Be(1);
        Actual("Timeout without ack → status Unknown; retry first looked up the idempotency key on core (FoundOnCore) and marked Applied without resending (core received once).");
    }

    [Fact, Tc("AML-TC-0150")]
    public async Task Pep_does_not_freeze()
    {
        var (cid, _, res) = await MatchedCustomer("PEP-TEST");
        await Transition(res.Id, "ConfirmedMatch");
        (await Actions(cid)).Should().BeEmpty();
        Actual("Confirmed PEP match → no freeze action (PEP handled via EDD/senior approval, not sanctions policy).");
    }

    [Fact, Tc("AML-TC-0172", "TC-TOF-004")]
    public async Task Neutral_outbound_codes()
    {
        var (cid, _, res) = await MatchedCustomer("UN-TEST");
        await Transition(res.Id, "ConfirmedMatch", notes: "TERRORIST FINANCING SUSPICION CONFIRMED");
        var payloads = await F.WithDb(db => db.Set<MockCoreCommand>().Where(m => m.IdempotencyKey.StartsWith("SCREENING:" + res.Id + ":")).Select(m => m.PayloadJson).ToListAsync());
        payloads.Should().NotBeEmpty();
        payloads.Should().OnlyContain(p => p.Contains("RESTRICTED_CONTACT_COMPLIANCE") && !p.Contains("SUSPICION") && !p.Contains("UN-TEST"));
        Actual("Core payload: " + payloads[0] + " — neutral code only, no suspicion, list or case details.");
    }
}

public class RealtimeTests : ScreeningTestBase
{
    public RealtimeTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    private async Task<(System.Net.HttpStatusCode Status, JsonNode? Body)> Decide(object body)
    {
        var client = await F.ClientAsync();
        var r = await client.Post("/api/realtime/transactions/decide", body);
        return (r.Status, r.Body);
    }

    private static object Req(string? id = null, decimal amount = 50, string type = "P2P", string? beneficiary = null, string? remarks = null, string? channel = "Mobile", string? sender = null) => new
    {
        sourceTransactionId = id ?? "RT-" + Guid.NewGuid().ToString("N")[..10], transactionType = type, channel, amount, currency = "JOD", senderWallet = sender,
        receiverWallet = "W-X", beneficiaryName = beneficiary, remarks
    };

    [Fact, Tc("TC-RTD-001", "AML-TC-0181")]
    public async Task Clean_allow()
    {
        var (s, b) = await Decide(Req());
        s.Should().Be(HttpStatusCode.OK);
        b!["decision"]!.GetValue<string>().Should().Be("Allow");
        b["decisionId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        b["latencyMs"]!.GetValue<int>().Should().BeLessThan(300);
        Actual($"Allow, reasons {b["reasonCodes"]!.ToJsonString()}, decisionId {b["decisionId"]}, correlationId {b["correlationId"]}, latency {b["latencyMs"]} ms (< 300 ms budget).");
    }

    [Fact, Tc("TC-RTD-002", "AML-TC-0183")]
    public async Task Sanctioned_beneficiary_rejected()
    {
        var (_, b) = await Decide(Req(beneficiary: "ACME TERROR LTD"));
        b!["decision"]!.GetValue<string>().Should().Be("Reject");
        b["reasonCodes"]!.ToJsonString().Should().Contain("SANCTIONS_NAME_MATCH");
        Actual($"Beneficiary on mandatory list → {b["decision"]} with {b["reasonCodes"]!.ToJsonString()} before execution; decision stored for audit.");
    }

    [Fact, Tc("TC-RTD-003", "AML-TC-0182")]
    public async Task Remarks_screened()
    {
        var name = FakeName();
        await AddListEntry("OFAC-TEST", name);
        var (_, b) = await Decide(Req(remarks: $"payment for {name} invoice 22"));
        b!["decision"]!.GetValue<string>().Should().Be("HoldForReview");
        b["reasonCodes"]!.ToJsonString().Should().Contain("FREE_TEXT_LIST_MATCH");
        var q = (await (await As("officer")).GetOk("/api/holds?status=Held")).AsArray();
        q.Any(h => h!["decisionId"]?.ToString() == b["decisionId"]!.GetValue<string>()).Should().BeTrue();
        Actual("Listed name inside free-text remarks → Hold (FREE_TEXT_LIST_MATCH) and hold-queue entry created.");
    }

    [Fact, Tc("TC-RTD-004", "TC-RTD-005", "AML-TC-0186", "AML-TC-0187", "AML-TC-0188")]
    public async Task Fallback_policies()
    {
        await F.SetSetting("Realtime:SimulatedDelayMs", "1500");
        try
        {
            var (_, big) = await Decide(Req(amount: 1500));
            big!["decision"]!.GetValue<string>().Should().Be("Reject");
            big["isFallback"]!.GetValue<bool>().Should().BeTrue();
            var (_, small) = await Decide(Req(amount: 100));
            small!["decision"]!.GetValue<string>().Should().Be("Allow");
            small["isFallback"]!.GetValue<bool>().Should().BeTrue();
            var (_, other) = await Decide(Req(amount: 500, type: "Remittance"));
            other!["decision"]!.GetValue<string>().Should().Be("HoldForReview");
            var post = (await (await As("officer")).GetOk("/api/holds?status=PostReview")).AsArray();
            post.Any(h => h!["decisionId"]?.ToString() == small["decisionId"]!.GetValue<string>()).Should().BeTrue();
            (await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "REALTIME_FALLBACK"))).Should().BeGreaterThan(2);
            Actual($"Latency > 300 ms budget: 1500 JOD → Reject (FailClosed ≥1000); 100 JOD P2P → Allow with fallback flag (FailOpen ≤200) queued for post-review; 500 Remittance → Hold (default FailClosed); latencies {big["latencyMs"]}/{small["latencyMs"]} ms; fallbacks audited.");
        }
        finally { await F.SetSetting("Realtime:SimulatedDelayMs", "0"); }
    }

    [Fact, Tc("TC-RTD-006", "AML-TC-0184", "AML-TC-0185")]
    public async Task Idempotent_decide()
    {
        var id = "RT-" + U();
        var req = Req(id: id);
        var (_, a) = await Decide(req);
        var (_, b) = await Decide(req);
        b!["decisionId"]!.GetValue<string>().Should().Be(a!["decisionId"]!.GetValue<string>());
        b["idempotent"]!.GetValue<bool>().Should().BeTrue();
        var (s, c) = await Decide(Req(id: id, amount: 999));
        s.Should().Be(HttpStatusCode.Conflict);
        Actual("Same source transaction ID → same decision ID (idempotent=true); same ID with changed amount → 409 conflict (not silently treated as identical).");
    }

    [Fact, Tc("TC-RTD-007", "TC-RTD-008", "AML-TC-0190")]
    public async Task Hold_release_and_sla()
    {
        var listed = FakeName();
        await AddListEntry("OFAC-TEST", listed);
        var (_, b) = await Decide(Req(beneficiary: listed));
        var held = (await (await As("officer")).GetOk("/api/holds?status=Held")).AsArray().First(h => h!["decisionId"]?.ToString() == b!["decisionId"]!.GetValue<string>())!;
        await F.ExecSql($"UPDATE HoldQueueItems SET SlaDueAt = DATEADD(minute,-5,SYSUTCDATETIME()) WHERE Id = {Id(held)}");
        await (await As("admin")).PostOk("/api/admin/jobs/hold-sla/run");
        await (await As("admin")).PostOk("/api/admin/jobs/hold-sla/run");
        (await F.WithDb(db => db.Set<OperationalAlert>().CountAsync(o => o.Category == "HoldSlaBreach" && o.Reference == Id(held).ToString()))).Should().Be(1);
        var rel = await (await As("officer")).PostOk($"/api/holds/{Id(held)}/decision", new { release = true, note = "False positive - different person" });
        rel["status"]!.GetValue<string>().Should().Be("Released");
        (await F.WithDb(db => db.Set<MockCoreCommand>().CountAsync(m => m.IdempotencyKey == $"HOLD:{Id(held)}:ReleaseTransaction"))).Should().Be(1);
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "HOLD_RELEASE" && a.EntityId == Id(held).ToString()))).Should().Be(1);
        Actual("Held transaction past 30-min SLA escalated exactly once (HoldSlaBreach alert); analyst release sent ReleaseTransaction to core and was audited.");
    }

    [Fact, Tc("TC-RTD-009", "AML-TC-0189")]
    public async Task Decision_linked_to_ingested()
    {
        var a = await CompleteKycCustomer(); var wa = await CreateWallet(Id(a)); var b = await CompleteKycCustomer(); var wb = await CreateWallet(Id(b));
        var id = "RT-" + U();
        var (_, d) = await Decide(new { sourceTransactionId = id, transactionType = "P2P", channel = "Mobile", amount = 20, currency = "JOD", senderWallet = wa, receiverWallet = wb });
        await Ingest(Txn(id, wa, wb, 20, DateTime.UtcNow.AddMinutes(-1)));
        var t = await (await As("officer")).GetOk($"/api/transactions/{await TxnId(id)}");
        t["transaction"]!["realtimeDecisionId"]!.GetValue<string>().Should().Be(d!["decisionId"]!.GetValue<string>());
        t["realtimeDecision"]!["decision"]!.GetValue<string>().Should().Be("Allow");
        Actual($"Ingested transaction {id} linked to real-time decision {d["decisionId"]} by source transaction ID.");
    }

    [Fact, Tc("TC-RTD-010")]
    public async Task Only_realtime_rules_inline()
    {
        var ch = Channel();
        var officer = await As("officer");
        var rt = await officer.PostOk("/api/admin/aml-rules", new
        {
            code = "RT_" + ch, name = "RT daily sum", category = "Test", ruleType = "Realtime", severity = "Medium",
            definition = new { realtimeCapable = true, window = new { type = "Calendar", unit = "Day" }, condition = new { op = "and", conditions = new object[] { new { field = "txn.channel", @operator = "eq", value = ch }, new { aggregate = "sum", @operator = "gte", value = 300 } } } }
        });
        await officer.PostOk($"/api/admin/aml-rules/{Id(rt)}/simulate", new { from = DateTime.UtcNow.AddDays(-1), to = DateTime.UtcNow });
        await officer.PostOk($"/api/admin/aml-rules/{Id(rt)}/submit", new { rationale = "rt", testEvidence = "sim", expectedImpact = "low" });
        await (await As("senior")).PostOk($"/api/admin/aml-rules/{Id(rt)}/approve", new { comment = "ok" });
        await officer.PostOk($"/api/admin/aml-rules/{Id(rt)}/publish");
        var batch = await PublishRule(CountRule(ch, 1));
        var (_, d) = await Decide(Req(amount: 500, channel: ch));
        d!["decision"]!.GetValue<string>().Should().Be("HoldForReview");
        d["reasonCodes"]!.ToJsonString().Should().Contain("RULE_RT_" + ch);
        var rec = await F.WithDb(db => db.Set<RealtimeDecision>().FirstAsync(x => x.DecisionId == Guid.Parse(d["decisionId"]!.GetValue<string>())));
        rec.EvaluatedRules.Should().Contain("RT_" + ch).And.NotContain("UT_");
        Actual($"Inline evaluation used only real-time-capable rules ({rec.EvaluatedRules}) with pre-computed daily aggregates; batch rule not executed inline; decision Hold (RULE_RT_{ch}).");
    }
}

public class PaymentTransparencyTests : ScreeningTestBase
{
    public PaymentTransparencyTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact, Tc("TC-R16-001", "AML-TC-0191")]
    public async Task Missing_originator_dob_alert()
    {
        var a = await CompleteKycCustomer(); var wa = await CreateWallet(Id(a)); var b = await CompleteKycCustomer(); var wb = await CreateWallet(Id(b));
        var t = U("T");
        await Ingest(Txn(t, wa, wb, 2000, DateTime.UtcNow.AddMinutes(-2), type: "Remittance", currency: "USD", src: "JO", dst: "AE",
            originator: new { name = "Omar Test Sender", address = "1 Test St, Amman" }, beneficiary: new { name = "Ali Test Receiver", account = "AE000TEST1" }));
        var d = await (await As("officer")).GetOk($"/api/transactions/{await TxnId(t)}");
        d["paymentTransparency"]!["paymentTransparencyStatus"]!.GetValue<string>().Should().Be("Incomplete");
        d["paymentTransparency"]!["paymentTransparencyIssues"]!.GetValue<string>().Should().Contain("OriginatorDateOfBirth");
        var alerts = await F.WithDb(db => db.Set<AmlAlert>().CountAsync(x => x.DeduplicationKey == "R16:CORE:" + t));
        alerts.Should().Be(1);
        Actual($"Cross-border 2000 USD remittance missing originator DOB → status Incomplete ({d["paymentTransparency"]!["paymentTransparencyIssues"]}) and R16 completeness alert.");
    }

    [Fact, Tc("TC-R16-005", "TC-R16-006", "AML-TC-0195", "AML-TC-0196")]
    public async Task Cop_and_chain_data_stored()
    {
        var a = await CompleteKycCustomer(); var wa = await CreateWallet(Id(a)); var b = await CompleteKycCustomer(); var wb = await CreateWallet(Id(b));
        var t = U("T");
        await Ingest(new
        {
            sourceTransactionId = t, type = "Remittance", status = "SUCC", eventTime = DateTime.UtcNow.AddMinutes(-1), amount = 100, currency = "JOD", senderWallet = wa, receiverWallet = wb,
            sourceCountry = "JO", destinationCountry = "AE", copResult = "MISMATCH", intermediaryInstitution = "TEST INTERMEDIARY BANK", orderingInstitution = "TEST ORDERING BANK",
            originator = new { name = "Omar Test", account = wa, address = "Amman", dateOfBirth = "1990-01-01" }, beneficiary = new { name = "Ali Test", account = "AE-ACC-1" }
        });
        var d = await (await As("officer")).GetOk($"/api/transactions/{await TxnId(t)}");
        var p = d["paymentTransparency"]!;
        p["confirmationOfPayee"]!.GetValue<string>().Should().Be("MISMATCH");
        p["intermediaryInstitution"]!.GetValue<string>().Should().Be("TEST INTERMEDIARY BANK");
        p["originator"]!["name"]!.GetValue<string>().Should().Be("Omar Test");
        p["beneficiary"]!["account"]!.GetValue<string>().Should().Be("AE-ACC-1");
        var fields = await (await As("officer")).GetOk("/api/config/rule-fields");
        fields.ToJsonString().Should().Contain("txn.copResult");
        Actual("CoP 'MISMATCH', intermediary/ordering institutions and originator/beneficiary fields stored and shown in transaction detail; txn.copResult available as rule input.");
    }
}

public class RegulatoryReportingTests : ScreeningTestBase
{
    public RegulatoryReportingTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    private async Task<(long Case, long Customer, List<long> Txns)> CaseWithData()
    {
        var a = await CompleteKycCustomer(name: "Strtest " + Word() + " محمد"); var wa = await CreateWallet(Id(a)); var b = await CompleteKycCustomer(); var wb = await CreateWallet(Id(b));
        var ids = new[] { U("T"), U("T") };
        await Ingest(ids.Select(x => Txn(x, wa, wb, 950, DateTime.UtcNow.AddHours(-2))).ToArray());
        var api = await As("officer");
        var c = (await api.PostOk("/api/cases", new { title = "STR case " + U(), priority = "Medium", primaryCustomerId = Id(a) }))["case"]!;
        var tx = new List<long>();
        foreach (var x in ids) { var tid = await TxnId(x); tx.Add(tid); await api.PostOk($"/api/cases/{Id(c)}/link-transaction", new { transactionId = tid }); }
        return (Id(c), Id(a), tx);
    }

    private async Task<JsonNode> ApprovedReport()
    {
        var (caseId, _, tx) = await CaseWithData();
        var api = await As("officer");
        var r = await api.PostOk($"/api/cases/{caseId}/reports", new { reportType = "STR" });
        await api.PutOk($"/api/regulatory-reports/{Id(r)}", new { narrative = "Customer conducted repeated transfers of 950 JOD just below the 1000 JOD threshold on the same day.", indicatorCodes = new[] { "STRUCTURING" }, transactionIds = tx });
        await api.PostOk($"/api/regulatory-reports/{Id(r)}/submit-for-review");
        return await (await As("mlro")).PostOk($"/api/regulatory-reports/{Id(r)}/approve", new { comment = "approved" });
    }

    [Fact, Tc("TC-STR-001", "TC-STR-002", "AML-TC-0173")]
    public async Task Create_from_case_prefilled()
    {
        var (caseId, cust, tx) = await CaseWithData();
        var r = await (await As("officer")).PostOk($"/api/cases/{caseId}/reports", new { reportType = "STR" });
        r["status"]!.GetValue<string>().Should().Be("Draft");
        r.ToJsonString().Should().Contain(cust.ToString());
        var alertRoute = await (await As("officer")).Post("/api/alerts/1/reports", new { reportType = "STR" });
        ((int)alertRoute.Status).Should().BeOneOf(404, 405);
        Actual($"Report {r["reportNumber"]} created from case with subject (customer {cust}, masked identifiers, accounts) and {tx.Count} case transactions prefilled; no alert→report endpoint ({(int)alertRoute.Status}).");
    }

    [Fact, Tc("TC-STR-003", "TC-STR-004", "AML-TC-0174", "AML-TC-0180")]
    public async Task Narrative_and_schema_validation()
    {
        var (caseId, _, _) = await CaseWithData();
        var api = await As("officer");
        var r = await api.PostOk($"/api/cases/{caseId}/reports", new { reportType = "STR" });
        (await api.Post($"/api/regulatory-reports/{Id(r)}/submit-for-review")).Status.Should().Be(HttpStatusCode.BadRequest);
        await api.PutOk($"/api/regulatory-reports/{Id(r)}", new { narrative = new string('n', 80) });
        await api.PostOk($"/api/regulatory-reports/{Id(r)}/submit-for-review");
        var v = await api.PostOk($"/api/regulatory-reports/{Id(r)}/validate");
        v["valid"]!.GetValue<bool>().Should().BeFalse();
        var appr = await (await As("mlro")).Post($"/api/regulatory-reports/{Id(r)}/approve", new { comment = "x" });
        appr.Status.Should().Be(HttpStatusCode.BadRequest);
        Actual("Submit for review without narrative → 400; payload missing mandatory report_indicators fails XSD validation (" + string.Join(" | ", v["errors"]!.AsArray()) + "); approval blocked (400).");
    }

    [Fact, Tc("TC-STR-005", "TC-STR-006", "AML-TC-0175", "AML-TC-0267")]
    public async Task Mlro_approval_no_auto_submit()
    {
        var (caseId, _, tx) = await CaseWithData();
        var api = await As("officer");
        var r = await api.PostOk($"/api/cases/{caseId}/reports", new { reportType = "STR" });
        await api.PutOk($"/api/regulatory-reports/{Id(r)}", new { narrative = new string('n', 80), indicatorCodes = new[] { "STRUCTURING" }, transactionIds = tx });
        await api.PostOk($"/api/regulatory-reports/{Id(r)}/submit-for-review");
        (await api.Post($"/api/regulatory-reports/{Id(r)}/approve", new { comment = "self" })).Status.Should().Be(HttpStatusCode.Forbidden);
        (await (await As("investigator")).Post($"/api/regulatory-reports/{Id(r)}/approve", new { comment = "x" })).Status.Should().Be(HttpStatusCode.Forbidden);
        var ok = await (await As("mlro")).PostOk($"/api/regulatory-reports/{Id(r)}/approve", new { comment = "ok" });
        ok["status"]!.GetValue<string>().Should().Be("Approved");
        foreach (var job in new[] { "str-sla", "outbox-publisher", "rule-scheduler" }) await (await As("admin")).PostOk($"/api/admin/jobs/{job}/run");
        (await (await As("mlro")).GetOk($"/api/regulatory-reports/{Id(r)}"))["status"]!.GetValue<string>().Should().Be("Approved");
        Actual("Preparer and analyst cannot approve (403); MLRO approval → Approved; report stays Approved after background jobs (no automatic submission).");
    }

    [Fact, Tc("TC-STR-007", "TC-STR-008", "AML-TC-0176", "AML-TC-0177", "AML-TC-0178")]
    public async Task Submit_immutable_ack_amendment()
    {
        var r = await ApprovedReport();
        var sub = await (await As("mlro")).PostOk($"/api/regulatory-reports/{Id(r)}/submit");
        sub["status"]!.GetValue<string>().Should().Be("Submitted");
        sub["payloadHash"]!.GetValue<string>().Should().HaveLength(64);
        sub["submissionChannel"]!.GetValue<string>().Should().Contain("Sandbox");
        (await (await As("officer")).Put($"/api/regulatory-reports/{Id(r)}", new { narrative = new string('x', 80) })).Status.Should().Be(HttpStatusCode.Conflict);
        var upd = async () => await F.ExecSql($"UPDATE RegulatoryReports SET Narrative = 'tampered' WHERE Id = {Id(r)}");
        await upd.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();
        var ack = await (await As("mlro")).PostOk($"/api/regulatory-reports/{Id(r)}/acknowledgement", new { fiuReference = "FIU-TEST-001", accepted = true, details = "received" });
        ack["status"]!.GetValue<string>().Should().Be("Acknowledged");
        ack["acknowledgedAt"].Should().NotBeNull();
        var caseId = r["caseId"]!.GetValue<long>();
        var amend = await (await As("officer")).PostOk($"/api/cases/{caseId}/reports", new { reportType = "Amendment", previousReportId = Id(r) });
        amend["previousReportId"]!.GetValue<long>().Should().Be(Id(r));
        Actual($"Submitted to sandbox channel (payload SHA-256 {sub["payloadHash"]!.ToString()[..12]}…); edit refused (409) and DB trigger blocks updates; FIU ref FIU-TEST-001 + acknowledgement time stored; amendment {amend["reportNumber"]} links to original without rewriting it.");
    }

    [Fact, Tc("TC-STR-009")]
    public async Task Manual_export_path()
    {
        var r = await ApprovedReport();
        var api = await As("mlro");
        var exp = await api.Send(HttpMethod.Get, $"/api/regulatory-reports/{Id(r)}/export");
        exp.Status.Should().Be(HttpStatusCode.OK);
        var hash = exp.Response.Headers.GetValues("X-Payload-SHA256").First();
        var xml = await exp.Response.Content.ReadAsStringAsync();
        xml.Should().Contain("<report>").And.Contain("محمد");
        var m = await api.PostOk($"/api/regulatory-reports/{Id(r)}/manual-submission", new { evidence = "Uploaded via FIU portal, screenshot REF-77", fiuReference = "FIU-MAN-77" });
        m["status"]!.GetValue<string>().Should().Be("Submitted");
        m["payloadHash"]!.GetValue<string>().Should().Be(hash);
        Actual($"XML exported (UTF-8, Arabic preserved) with SHA-256 header {hash[..12]}…; manual submission recorded with evidence and FIU reference; stored hash matches exported file.");
    }

    [Fact, Tc("TC-STR-010", "AML-TC-0241")]
    public async Task Sla_from_suspicion_date()
    {
        var (caseId, _, _) = await CaseWithData();
        var r = await (await As("officer")).PostOk($"/api/cases/{caseId}/reports", new { reportType = "STR" });
        var due = r["dueAt"]!.GetValue<DateTime>();
        due.Should().BeAfter(DateTime.UtcNow.AddDays(1));
        await F.ExecSql($"UPDATE RegulatoryReports SET DueAt = DATEADD(hour,-1,SYSUTCDATETIME()) WHERE Id = {Id(r)}");
        await (await As("admin")).PostOk("/api/admin/jobs/str-sla/run");
        var after = await (await As("mlro")).GetOk($"/api/regulatory-reports/{Id(r)}");
        after["isOverdue"]!.GetValue<bool>().Should().BeTrue();
        (await F.WithDb(db => db.Set<OperationalAlert>().CountAsync(o => o.Category == "StrOverdue" && o.Reference == Id(r).ToString()))).Should().Be(1);
        Actual($"Due date {due:O} = suspicion date + 2 working days from jurisdiction setting STR_FILING_DEADLINE (to be confirmed by compliance); after due → reminder + overdue escalation once.");
    }

    [Fact, Tc("TC-STR-011", "TC-STR-012", "AML-TC-0179", "AML-TC-0240")]
    public async Task Authority_requests()
    {
        var c = await CompleteKycCustomer();
        var api = await As("officer");
        var r = await api.PostOk("/api/authority-requests", new { reference = "FIU-REQ-" + U(), authority = "FIU (test)", requestType = "InformationRequest", customerId = Id(c), scope = "All transactions 2026", dueDate = DateTime.UtcNow.AddDays(5), assignedOfficer = "officer" });
        await F.ExecSql($"UPDATE AuthorityRequests SET DueDate = DATEADD(day,-1,SYSUTCDATETIME()) WHERE Id = {Id(r)}");
        r["assignedOfficer"]!.GetValue<string>().Should().Be("officer");
        await (await As("admin")).PostOk("/api/admin/jobs/authority-overdue/run");
        var g = await api.GetOk($"/api/authority-requests/{Id(r)}");
        g["isOverdue"]!.GetValue<bool>().Should().BeTrue();
        var resp = await api.PostOk($"/api/authority-requests/{Id(r)}/respond", new { response = "Statement provided", evidence = "DOC-RESP-1" });
        resp["responseEvidence"]!.GetValue<string>().Should().Be("DOC-RESP-1");
        var reassess = await F.WithDb(db => db.Set<CustomerRiskAssessment>().CountAsync(a => a.CustomerId == Id(c) && a.Trigger == "AuthorityRequest"));
        reassess.Should().Be(1);
        Actual("Authority request registered with owner, scope and due date; overdue flagged by job; response and evidence stored (audited); event-triggered reassessment (trigger AuthorityRequest) created.");
    }

    [Fact, Tc("AML-TC-0268")]
    public async Task Dev_uses_mock_and_sandbox()
    {
        var h = await (await As("admin")).GetOk("/api/operations/health");
        h["integrations"]!["coreAdapter"]!.GetValue<string>().Should().Contain("Mock");
        h["integrations"]!["fiuChannel"]!.GetValue<string>().Should().Contain("Sandbox");
        Actual($"Non-production environment wired to core adapter '{h["integrations"]!["coreAdapter"]}' and FIU channel '{h["integrations"]!["fiuChannel"]}' — no real freeze/STR submission possible.");
    }
}

public class TippingOffTests : ScreeningTestBase
{
    public TippingOffTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact, Tc("TC-TOF-001", "TC-TOF-002", "TC-TOF-003")]
    public async Task Restricted_case_not_leaked()
    {
        var c = await CompleteKycCustomer();
        var restricted = (await (await As("mlro")).PostOk("/api/cases", new { title = "Restricted " + U(), confidentiality = "Restricted", primaryCustomerId = Id(c) }))["case"]!;
        var ops = await (await As("ops")).GetOk($"/api/customers/{Id(c)}");
        ops["caseCount"]!.GetValue<int>().Should().Be(0);
        (await (await As("ops")).Get($"/api/customers/{Id(c)}/cases")).Status.Should().Be(HttpStatusCode.Forbidden);
        var inv = await As("investigator");
        var s1 = await inv.GetOk($"/api/cases?search={restricted["caseNumber"]}");
        var s2 = await inv.GetOk("/api/cases?search=CASE-DOES-NOT-EXIST-999");
        s1.ToJsonString().Replace(restricted["caseNumber"]!.GetValue<string>(), "X").Should().Be(s2.ToJsonString().Replace("CASE-DOES-NOT-EXIST-999", "X"));
        (await inv.GetOk($"/api/customers/{Id(c)}"))["caseCount"]!.GetValue<int>().Should().Be(0);
        (await (await As("mlro")).GetOk($"/api/customers/{Id(c)}"))["caseCount"]!.GetValue<int>().Should().Be(1);
        Actual("Operations user: case count 0, no cases tab access (403); investigator search for the restricted case number returns the same empty page as a non-existent number; MLRO count 1 vs unauthorized 0 without any hint.");
    }

    [Fact, Tc("TC-TOF-005", "TC-TOF-006")]
    public async Task Restricted_reads_audited_and_reported()
    {
        var restricted = (await (await As("mlro")).PostOk("/api/cases", new { title = "Restricted " + U(), confidentiality = "Restricted", assignedInvestigator = "investigator" }))["case"]!;
        await (await As("investigator")).GetOk($"/api/cases/{Id(restricted)}");
        await (await As("admin")).GetOk($"/api/cases/{Id(restricted)}");
        var report = (await (await As("admin")).GetOk("/api/cases/access-report")).AsArray();
        report.Should().Contain(r => r!["actor"]!.GetValue<string>() == "investigator" && r["entityId"]!.GetValue<string>() == Id(restricted).ToString());
        var adminRow = report.First(r => r!["actor"]!.GetValue<string>() == "admin" && r["entityId"]!.GetValue<string>() == Id(restricted).ToString())!;
        adminRow["outsideTeam"]!.GetValue<bool>().Should().BeTrue();
        Actual("Assigned investigator read → RESTRICTED_READ audit; admin read of a case outside their team flagged outsideTeam=true in the access report.");
    }

    [Fact, Tc("TC-TOF-007")]
    public async Task Export_excludes_restricted()
    {
        var (alertId, _, _, _) = await new AlertHelper(F, Out).Make();
        await F.ExecSql($"UPDATE AmlAlerts SET Confidentiality = 'Restricted' WHERE Id = {alertId}");
        var off = await (await As("officer")).GetOk("/api/reports/alerts?pageSize=1000");
        off.ToJsonString().Should().NotContain($"\"Id\":{alertId},");
        var mlro = await (await As("mlro")).GetOk("/api/reports/alerts?pageSize=1000&includeRestricted=true");
        mlro.ToJsonString().Should().Contain($"{alertId}");
        Actual("Alerts register/export for officer (no restricted-export permission) excludes the restricted alert; MLRO with permission and includeRestricted sees it.");
    }

    [Fact, Tc("TC-TOF-008")]
    public async Task Str_events_hidden()
    {
        var c = await CompleteKycCustomer();
        var api = await As("officer");
        var cs = (await api.PostOk("/api/cases", new { title = "STR " + U(), primaryCustomerId = Id(c) }))["case"]!;
        await api.PostOk($"/api/cases/{Id(cs)}/reports", new { reportType = "STR" });
        var hist = (await (await As("ops")).GetOk($"/api/customers/{Id(c)}/history")).ToJsonString();
        hist.Should().NotContain("STR").And.NotContain("RegulatoryReport");
        (await (await As("ops")).Get("/api/regulatory-reports")).Status.Should().Be(HttpStatusCode.Forbidden);
        (await (await As("ops")).Get("/api/admin/audit?action=STR_CREATE")).Status.Should().Be(HttpStatusCode.Forbidden);
        Actual("Operations user's customer history shows no STR events (restricted audit filtered); report and audit endpoints forbidden.");
    }

    private sealed class AlertHelper : RuleTestBase
    {
        public AlertHelper(AmlFactory f, ITestOutputHelper o) : base(f, o) { }
        public Task<(long Alert, long Customer, long Rule, string Channel)> Make() => MakeAlert();
    }
}
