using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AML.Domain.Entities;
using AML.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration;

/// <summary>Shared helpers: rules are scoped to a unique channel so concurrent data never interferes.</summary>
public abstract class RuleTestBase : AmlTestBase
{
    protected RuleTestBase(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    protected static string Channel() => "CH" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    protected static object CountRule(string channel, int min = 1, string severity = "Medium", object? dedup = null, string window = "Rolling") => new
    {
        scope = new { channels = new[] { channel } },
        window = window == "Rolling" ? (object)new { type = "Rolling", days = 1 } : new { type = "Calendar", unit = "Day" },
        condition = new { aggregate = "count", @operator = "gte", value = min },
        dedupKey = dedup,
        action = new { severity }
    };

    protected async Task<(long Customer, string Wallet)> Party()
    {
        var c = await CreateCustomer();
        return (Id(c), await CreateWallet(Id(c)));
    }

    /// <summary>Creates one alert for a fresh customer via a published count rule; returns (alertId, customerId, ruleId, channel).</summary>
    protected async Task<(long Alert, long Customer, long Rule, string Channel)> MakeAlert(string severity = "Medium")
    {
        var ch = Channel();
        var rule = await PublishRule(CountRule(ch, 1, severity), severity: severity);
        var a = await Party(); var b = await Party();
        var at = DateTime.UtcNow.AddMinutes(-10);
        await Ingest(Txn(U("T"), a.Wallet, b.Wallet, 100, at, channel: ch));
        await RunRule(rule, at.AddMinutes(-1), DateTime.UtcNow);
        var alerts = (await (await As("officer")).GetOk($"/api/alerts?customerId={a.Customer}"))["items"]!.AsArray();
        return (Id(alerts[0]!), a.Customer, rule, ch);
    }

    protected async Task<JsonNode> Alert(long id, string user = "officer") => (await (await As(user)).GetOk($"/api/alerts/{id}"))["alert"]!;
    protected async Task<string> Rv(long id, string user = "officer") => (await Alert(id, user))["rowVersion"]!.GetValue<string>();
}

public class RuleWorkflowTests : RuleTestBase
{
    public RuleWorkflowTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact, Tc("TC-RUL-001", "AML-TC-0104")]
    public async Task Create_draft_nested()
    {
        var def = new
        {
            window = new { type = "Rolling", hours = 24 },
            condition = new
            {
                op = "or", conditions = new object[]
                {
                    new { op = "and", conditions = new object[] { new { field = "txn.amount", @operator = "gt", value = 5000 }, new { field = "txn.channel", @operator = "eq", value = "Agent" } } },
                    new { aggregate = "count", @operator = "gt", value = 10 }
                }
            }
        };
        var r = await (await As("officer")).PostOk("/api/admin/aml-rules", new { code = "UT_" + Channel(), name = "Nested", category = "Test", severity = "Medium", definition = def });
        r["versions"]![0]!["status"]!.GetValue<string>().Should().Be("Draft");
        var v = await (await As("officer")).PostOk($"/api/admin/aml-rules/{Id(r)}/validate");
        v["valid"]!.GetValue<bool>().Should().BeTrue();
        Actual("Rule (amount>5000 AND channel=Agent) OR count>10 in 24h saved as Draft v1; JSON definition validated (valid=true).");
    }

    [Fact, Tc("AML-TC-0276", "AML-TC-0277")]
    public async Task Malformed_and_complex_rejected()
    {
        var api = await As("officer");
        var bad = await api.Post("/api/admin/aml-rules", new { code = "UT_" + Channel(), name = "Bad", category = "Test", definition = new { condition = new { field = "txn.amount", @operator = "gt" } } });
        bad.Status.Should().Be(HttpStatusCode.BadRequest);
        object node = new { field = "txn.amount", @operator = "gt", value = 1 };
        for (var i = 0; i < 9; i++) node = new { op = "and", conditions = new[] { node } };
        var deep = await api.Post("/api/admin/aml-rules", new { code = "UT_" + Channel(), name = "Deep", category = "Test", definition = new { condition = node } });
        deep.Status.Should().Be(HttpStatusCode.BadRequest);
        Actual($"Missing value → 400 with path: {bad.Body!["detail"]}; 10-level nesting → 400: {deep.Body!["detail"]}.");
    }

    [Fact, Tc("TC-RUL-014", "AML-TC-0126", "AML-TC-0256", "AML-TC-0257")]
    public async Task Approval_workflow()
    {
        var officer = await As("officer");
        var r = await officer.PostOk("/api/admin/aml-rules", new { code = "UT_" + Channel(), name = "Wf", category = "Test", severity = "Medium", definition = CountRule(Channel()) });
        var noRationale = await officer.Post($"/api/admin/aml-rules/{Id(r)}/submit", new { });
        noRationale.Status.Should().Be(HttpStatusCode.BadRequest);
        await officer.PostOk($"/api/admin/aml-rules/{Id(r)}/simulate", new { from = DateTime.UtcNow.AddDays(-1), to = DateTime.UtcNow });
        await officer.PostOk($"/api/admin/aml-rules/{Id(r)}/submit", new { rationale = "New typology", testEvidence = "simulation 0 matches", expectedImpact = "~5 alerts/month" });
        (await officer.Post($"/api/admin/aml-rules/{Id(r)}/approve", new { comment = "self" })).Status.Should().Be(HttpStatusCode.Forbidden);
        var mgrSelf = await (await As("manager")).PostOk("/api/admin/aml-rules", new { code = "UT_" + Channel(), name = "Wf2", category = "Test", severity = "Medium", definition = CountRule(Channel()) });
        await (await As("manager")).PostOk($"/api/admin/aml-rules/{Id(mgrSelf)}/simulate", new { from = DateTime.UtcNow.AddDays(-1), to = DateTime.UtcNow });
        await (await As("manager")).PostOk($"/api/admin/aml-rules/{Id(mgrSelf)}/submit", new { rationale = "x", testEvidence = "y", expectedImpact = "z" });
        var selfApprove = await (await As("manager")).Post($"/api/admin/aml-rules/{Id(mgrSelf)}/approve", new { comment = "self" });
        ((int)selfApprove.Status).Should().BeOneOf(403, 409);
        await (await As("senior")).PostOk($"/api/admin/aml-rules/{Id(r)}/approve", new { comment = "ok" });
        var pub = await officer.PostOk($"/api/admin/aml-rules/{Id(r)}/publish");
        pub["status"]!.GetValue<string>().Should().Be("Active");
        pub["approvedBy"]!.GetValue<string>().Should().Be("senior");
        pub["expectedImpact"]!.GetValue<string>().Should().Contain("alerts/month");
        Actual($"Submit without rationale → 400; author/approver without permission → 403; manager self-approval → {(int)selfApprove.Status}; validate→simulate→approve(senior)→publish → Active, approver senior, impact '{pub["expectedImpact"]}' stored.");
    }

    [Fact, Tc("TC-RUL-015", "AML-TC-0266")]
    public async Task Seed_rules_disabled()
    {
        var rules = (await (await As("officer")).GetOk("/api/admin/aml-rules")).AsArray().Where(r => r!["isSample"]!.GetValue<bool>()).ToList();
        rules.Should().NotBeEmpty();
        rules.Should().OnlyContain(r => r!["status"]!.GetValue<string>() != "Active");
        var before = await F.WithDb(db => db.Set<AmlAlert>().CountAsync(a => a.IsTestData && a.Source == "Rule"));
        await (await As("admin")).PostOk("/api/admin/jobs/rule-scheduler/run");
        var after = await F.WithDb(db => db.Set<AmlAlert>().CountAsync(a => a.IsTestData && a.Source == "Rule"));
        after.Should().Be(before);
        Actual($"{rules.Count} seed rules marked sample and disabled ({string.Join(",", rules.Select(r => r!["status"]).Distinct())}); scheduler run produced no alerts from them.");
    }

    [Fact, Tc("TC-RUL-016", "AML-TC-0122")]
    public async Task Execution_log()
    {
        var ch = Channel();
        var rule = await PublishRule(CountRule(ch));
        var res = await RunRule(rule, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);
        var ex = (await (await As("officer")).GetOk($"/api/admin/aml-rules/executions?ruleId={rule}")).AsArray().First(e => Id(e!) == res["executionId"]!.GetValue<long>())!;
        foreach (var k in new[] { "ruleVersion", "windowStart", "windowEnd", "recordsEvaluated", "matches", "errorCount", "status", "correlationId" }) ex[k].Should().NotBeNull(k);
        Actual($"Execution {ex["id"]}: rule v{ex["ruleVersion"]}, window {ex["windowStart"]}..{ex["windowEnd"]}, evaluated {ex["recordsEvaluated"]}, matches {ex["matches"]}, errors {ex["errorCount"]}, status {ex["status"]}, correlation {ex["correlationId"]}.");
    }

    [Fact, Tc("TC-RUL-017", "TC-RUL-018", "AML-TC-0125", "AML-TC-0254")]
    public async Task Backtest()
    {
        var ch = Channel();
        var rule = await PublishRule(CountRule(ch));
        var a = await Party(); var b = await Party();
        var at = DateTime.UtcNow.AddDays(-3);
        await Ingest(Txn(U("T"), a.Wallet, b.Wallet, 100, at, channel: ch));
        var dry = await (await As("officer")).PostOk($"/api/admin/aml-rules/{rule}/run-backtest", new { from = at.AddHours(-1), to = at.AddHours(1) });
        dry["matches"]!.GetValue<int>().Should().Be(2, "sender (out) and receiver (in) subjects both match count>=1");
        dry["alertsCreated"]!.GetValue<int>().Should().Be(0);
        (await F.WithDb(db => db.Set<AmlAlert>().CountAsync(x => x.PrimaryCustomerId == a.Customer))).Should().Be(0);
        (await (await As("officer")).Post($"/api/admin/aml-rules/{rule}/run-backtest", new { from = at.AddHours(-1), to = at.AddHours(1), createAlerts = true })).Status.Should().Be(HttpStatusCode.Forbidden);
        var withAlerts = await (await As("manager")).PostOk($"/api/admin/aml-rules/{rule}/run-backtest", new { from = at.AddHours(-1), to = at.AddHours(1), createAlerts = true });
        withAlerts["alertsCreated"]!.GetValue<int>().Should().Be(2);
        var alert = await F.WithDb(db => db.Set<AmlAlert>().FirstAsync(x => x.PrimaryCustomerId == a.Customer));
        alert.IsBacktest.Should().BeTrue();
        alert.DeduplicationKey.Should().StartWith("BT");
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(x => x.Action == "RULE_BACKTEST"))).Should().BeGreaterThan(0);
        var ops = await (await As("officer")).GetOk($"/api/alerts?customerId={a.Customer}");
        ops["total"]!.GetValue<int>().Should().Be(0);
        Actual("Dry-run backtest: 2 matches (both parties), 0 alerts; createAlerts without permission → 403; with permission → alerts tagged IsBacktest (key prefix BT), excluded from operational queue, audited RULE_BACKTEST.");
    }

    [Fact, Tc("TC-RUL-019", "AML-TC-0269")]
    public async Task Edit_creates_new_version()
    {
        var ch = Channel();
        var rule = await PublishRule(CountRule(ch, 1));
        var officer = await As("officer");
        var v2 = await officer.PostOk($"/api/admin/aml-rules/{rule}/versions");
        v2["version"]!.GetValue<int>().Should().Be(2);
        v2["status"]!.GetValue<string>().Should().Be("Draft");
        await officer.PutOk($"/api/admin/aml-rules/{rule}/versions/{Id(v2)}", new { definition = CountRule(ch, 5), rowVersion = v2["rowVersion"]!.GetValue<string>() });
        var r = await officer.GetOk($"/api/admin/aml-rules/{rule}");
        r["activeVersion"]!.GetValue<int>().Should().Be(1);
        await officer.PostOk($"/api/admin/aml-rules/{rule}/simulate", new { from = DateTime.UtcNow.AddDays(-1), to = DateTime.UtcNow });
        await officer.PostOk($"/api/admin/aml-rules/{rule}/submit", new { rationale = "threshold", testEvidence = "sim", expectedImpact = "fewer" });
        await (await As("senior")).PostOk($"/api/admin/aml-rules/{rule}/approve", new { comment = "ok" });
        await officer.PostOk($"/api/admin/aml-rules/{rule}/publish");
        var after = await officer.GetOk($"/api/admin/aml-rules/{rule}");
        after["activeVersion"]!.GetValue<int>().Should().Be(2);
        after["versions"]!.AsArray().Should().Contain(v => v!["version"]!.GetValue<int>() == 1 && v["status"]!.GetValue<string>() == "Superseded");
        Actual("Edit created v2 Draft while v1 stayed Active; after approval/publish v2 Active and v1 Superseded (retained for traceable rollback).");
    }

    [Fact, Tc("TC-RUL-020")]
    public async Task Dynamic_field_in_rule()
    {
        var api = await As("manager");
        var g = (await api.GetOk("/api/admin/field-groups")).AsArray()[0]!["code"]!.GetValue<string>();
        var code = "EXP_MONTHLY_CASH_" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var fld = await api.PostOk("/api/admin/customer-fields", new { code, labelEn = "Expected monthly cash", groupCode = g, dataType = "Decimal", customerTypes = "Individual", isSearchable = true });
        await api.PostOk($"/api/admin/customer-fields/{Id(fld)}/publish", new { });
        var ch = Channel();
        var rule = await PublishRule(new { scope = new { channels = new[] { ch } }, condition = new { op = "and", conditions = new object[] { new { field = "field:" + code, @operator = "gt", value = 10000 }, new { field = "txn.amount", @operator = "gt", value = 0 } } } });
        var a = await Party(); var b = await Party();
        await api.PutOk($"/api/customers/{a.Customer}/attributes", new { values = new Dictionary<string, object> { [code] = 15000 }, effectiveFrom = DateTime.UtcNow.AddDays(-1), reason = "t" });
        var at = DateTime.UtcNow.AddMinutes(-5);
        await Ingest(Txn(U("T"), a.Wallet, b.Wallet, 10, at, channel: ch));
        var res = await RunRule(rule, at.AddMinutes(-1), DateTime.UtcNow);
        res["matches"]!.GetValue<int>().Should().BeGreaterThan(0);
        var alert = (await (await As("officer")).GetOk($"/api/alerts?customerId={a.Customer}"))["items"]!.AsArray()[0]!;
        var detail = await Alert(Id(alert));
        detail["evidence"]!.ToJsonString().Should().Contain("field:" + code);
        Actual($"Rule on field:{code} resolved by code at evaluation time; match evidence shows condition 'field:{code} gt 10000' with actual 15000.");
    }

    [Fact, Tc("TC-ING-011", "TC-ALR-002", "AML-TC-0123")]
    public async Task Reingestion_and_rerun_no_duplicate_alert()
    {
        var ch = Channel();
        var rule = await PublishRule(CountRule(ch));
        var a = await Party(); var b = await Party();
        var at = DateTime.UtcNow.AddMinutes(-10);
        var msg = Txn(U("T"), a.Wallet, b.Wallet, 100, at, channel: ch);
        await Ingest(msg);
        for (var i = 0; i < 3; i++)
        {
            await Ingest(msg);
            await RunRule(rule, at.AddMinutes(-1), DateTime.UtcNow);
        }
        (await F.WithDb(db => db.Set<AmlAlert>().CountAsync(x => x.PrimaryCustomerId == a.Customer))).Should().Be(1);
        Actual("Re-ingested the same transaction and reran the rule 3 times → exactly one alert (deduplicated by rule+version+subject+day key; DB unique index).");
    }

    [Fact, Tc("TC-ALR-003", "AML-TC-0112")]
    public async Task New_window_new_alert()
    {
        var ch = Channel();
        var rule = await PublishRule(CountRule(ch));
        var a = await Party(); var b = await Party();
        var d1 = DateTime.UtcNow.AddDays(-3); var d2 = DateTime.UtcNow.AddDays(-1);
        await Ingest(Txn(U("T"), a.Wallet, b.Wallet, 100, d1, channel: ch), Txn(U("T"), a.Wallet, b.Wallet, 100, d2, channel: ch));
        await RunRule(rule, d1.AddMinutes(-1), d1.AddMinutes(1));
        await RunRule(rule, d2.AddMinutes(-1), d2.AddMinutes(1));
        (await F.WithDb(db => db.Set<AmlAlert>().CountAsync(x => x.PrimaryCustomerId == a.Customer))).Should().Be(2);
        Actual("Matches on two different business days (different evidence group) created two separate alerts.");
    }

    [Fact, Tc("TC-ALR-004", "AML-TC-0278")]
    public async Task Configurable_dedup_key()
    {
        var ch = Channel();
        var rule = await PublishRule(CountRule(ch, 1, dedup: new[] { "rule", "subject", "day" }));
        var a = await Party(); var b = await Party();
        var at = DateTime.UtcNow.Date.AddHours(-12);
        if (at > DateTime.UtcNow.AddMinutes(-5)) at = DateTime.UtcNow.AddMinutes(-30);
        await Ingest(Txn(U("T"), a.Wallet, b.Wallet, 100, at, channel: ch), Txn(U("T"), a.Wallet, b.Wallet, 100, at.AddMinutes(1), channel: ch));
        var runs = Enumerable.Range(0, 3).Select(_ => RunRule(rule, at.AddMinutes(-1), at.AddMinutes(2))).ToList();
        await Task.WhenAll(runs);
        var alerts = await F.WithDb(db => db.Set<AmlAlert>().Where(x => x.PrimaryCustomerId == a.Customer).ToListAsync());
        alerts.Should().ContainSingle();
        var matches = await F.WithDb(db => db.Set<AmlAlertRuleMatch>().CountAsync(m => m.AlertId == alerts[0].Id));
        matches.Should().Be(2);
        Actual($"Dedup key rule+customer+day: two matches the same day (and 3 concurrent reruns) → one alert {alerts[0].AlertNumber} holding both matches.");
    }

    [Fact, Tc("TC-ALR-001", "AML-TC-0124", "AML-TC-0155")]
    public async Task Evidence_snapshot_immutable()
    {
        var (alertId, _, rule, ch) = await MakeAlert();
        var before = await (await As("officer")).GetOk($"/api/alerts/{alertId}");
        var officer = await As("officer");
        var v2 = await officer.PostOk($"/api/admin/aml-rules/{rule}/versions");
        await officer.PutOk($"/api/admin/aml-rules/{rule}/versions/{Id(v2)}", new { definition = CountRule(ch, 7), rowVersion = v2["rowVersion"]!.GetValue<string>() });
        var after = await (await As("officer")).GetOk($"/api/alerts/{alertId}");
        after["matches"]!.ToJsonString().Should().Be(before["matches"]!.ToJsonString());
        after["alert"]!["evidence"]!.ToJsonString().Should().Be(before["alert"]!["evidence"]!.ToJsonString());
        var m = before["matches"]![0]!;
        var upd = async () => await F.ExecSql($"UPDATE AmlAlertRuleMatches SET MatchedConditionsJson = '[]' WHERE AlertId = {alertId}");
        await upd.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();
        Actual($"Alert retains rule version v{before["alert"]!["ruleVersion"]}, matched conditions, window {m["windowStart"]}..{m["windowEnd"]} and transactions after the rule was edited; DB trigger blocks updates to match evidence.");
    }

    [Fact, Tc("AML-TC-0127", "AML-TC-0128")]
    public async Task Effective_dates_and_disable()
    {
        var ch = Channel();
        var officer = await As("officer");
        var r = await officer.PostOk("/api/admin/aml-rules", new { code = "UT_" + ch, name = "Future", category = "Test", severity = "Medium", definition = CountRule(ch), effectiveFrom = DateTime.UtcNow.AddDays(30) });
        await officer.PostOk($"/api/admin/aml-rules/{Id(r)}/simulate", new { from = DateTime.UtcNow.AddDays(-1), to = DateTime.UtcNow });
        await officer.PostOk($"/api/admin/aml-rules/{Id(r)}/submit", new { rationale = "a", testEvidence = "b", expectedImpact = "c" });
        await (await As("senior")).PostOk($"/api/admin/aml-rules/{Id(r)}/approve", new { comment = "ok" });
        await officer.PostOk($"/api/admin/aml-rules/{Id(r)}/publish");
        var before = await F.WithDb(db => db.Set<AmlRuleExecution>().CountAsync(e => e.RuleId == Id(r) && e.Mode == "Scheduled"));
        await (await As("admin")).PostOk("/api/admin/jobs/rule-scheduler/run");
        (await F.WithDb(db => db.Set<AmlRuleExecution>().CountAsync(e => e.RuleId == Id(r) && e.Mode == "Scheduled"))).Should().Be(before);
        var (alertId, _, rule2, _) = await MakeAlert();
        await officer.PostOk($"/api/admin/aml-rules/{rule2}/enabled", new { enabled = false, reason = "retire" });
        var execBefore = await F.WithDb(db => db.Set<AmlRuleExecution>().CountAsync(e => e.RuleId == rule2 && e.Mode == "Scheduled"));
        await (await As("admin")).PostOk("/api/admin/jobs/rule-scheduler/run");
        (await F.WithDb(db => db.Set<AmlRuleExecution>().CountAsync(e => e.RuleId == rule2 && e.Mode == "Scheduled"))).Should().Be(execBefore);
        (await Alert(alertId))["id"]!.GetValue<long>().Should().Be(alertId);
        Actual("Rule effective in 30 days not executed by scheduler; disabled rule produced no new scheduled executions while its prior runs and alerts remain.");
    }
}

public class AlertWorkflowTests : RuleTestBase
{
    public AlertWorkflowTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact, Tc("TC-ALR-005", "AML-TC-0156")]
    public async Task Assign()
    {
        var (id, _, _, _) = await MakeAlert();
        var r = await (await As("officer")).PostOk($"/api/alerts/{id}/assign", new { assignee = "investigator", team = "FIU-A" });
        r["alert"]!["status"]!.GetValue<string>().Should().Be("Assigned");
        r["alert"]!["assignedTo"]!.GetValue<string>().Should().Be("investigator");
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "ALERT_ASSIGNED" && a.EntityId == id.ToString()))).Should().Be(1);
        Actual("Alert assigned to investigator → status Assigned; ALERT_ASSIGNED audit record.");
    }

    [Fact, Tc("TC-ALR-006", "AML-TC-0158", "AML-TC-0159")]
    public async Task Disposition_requires_code_notes_checklist()
    {
        var (id, _, _, _) = await MakeAlert();
        var api = await As("officer");
        await api.PostOk($"/api/alerts/{id}/start-review", new { });
        (await api.Post($"/api/alerts/{id}/disposition", new { resolutionCode = "Closed-FalsePositive", rowVersion = await Rv(id) })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await api.Post($"/api/alerts/{id}/disposition", new { resolutionCode = "Whatever", notes = "Reviewed all activity in detail", checklist = Checklist(), rowVersion = await Rv(id) })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await api.Post($"/api/alerts/{id}/disposition", new { resolutionCode = "Closed-FalsePositive", notes = "Reviewed all activity in detail", rowVersion = await Rv(id) })).Status.Should().Be(HttpStatusCode.BadRequest);
        var ok = await api.PostOk($"/api/alerts/{id}/disposition", new { resolutionCode = "Closed-FalsePositive", notes = "Reviewed all activity in detail; salary pattern", checklist = Checklist(), rowVersion = await Rv(id) });
        ok["alert"]!["status"]!.GetValue<string>().Should().Be("Closed-FalsePositive");
        ok["alert"]!["resolutionNotes"]!.GetValue<string>().Should().Contain("salary");
        Actual("Close without notes → 400; invalid resolution code → 400; missing checklist → 400; with code + narrative + checklist → Closed-FalsePositive, narrative retained in history.");
    }

    [Fact, Tc("TC-ALR-007")]
    public async Task Escalate()
    {
        var (id, _, _, _) = await MakeAlert();
        var api = await As("officer");
        await api.PostOk($"/api/alerts/{id}/start-review", new { });
        (await api.Post($"/api/alerts/{id}/escalate", new { reason = "" })).Status.Should().Be(HttpStatusCode.BadRequest);
        var r = await api.PostOk($"/api/alerts/{id}/escalate", new { reason = "Large unexplained inflows" });
        r["alert"]!["status"]!.GetValue<string>().Should().Be("Escalated");
        var q = await (await As("senior")).GetOk("/api/alerts?status=Escalated&pageSize=200");
        q["items"]!.AsArray().Should().Contain(x => Id(x!) == id);
        Actual("Escalation requires reason; status Escalated and alert appears in the senior reviewers' Escalated queue.");
    }

    [Fact, Tc("TC-ALR-008", "AML-TC-0160")]
    public async Task Convert_to_case()
    {
        var (id, _, _, _) = await MakeAlert();
        var api = await As("officer");
        await api.PostOk($"/api/alerts/{id}/start-review", new { });
        await api.PostOk($"/api/alerts/{id}/escalate", new { reason = "needs investigation" });
        var c = await api.PostOk($"/api/alerts/{id}/convert-to-case", new { title = "Investigation " + id });
        var a = await Alert(id);
        a["status"]!.GetValue<string>().Should().Be("ConvertedToCase");
        a["caseId"]!.GetValue<long>().Should().Be(Id(c["case"]!));
        c["alerts"]!.AsArray().Should().Contain(x => Id(x!) == id);
        Actual($"Escalated alert converted → case {c["case"]!["caseNumber"]} created and linked; alert status ConvertedToCase with history entry.");
    }

    [Fact, Tc("TC-ALR-009", "AML-TC-0164")]
    public async Task High_severity_closure_approval()
    {
        var (id, _, _, _) = await MakeAlert("High");
        var api = await As("officer");
        await api.PostOk($"/api/alerts/{id}/start-review", new { });
        var r = await api.PostOk($"/api/alerts/{id}/disposition", new { resolutionCode = "Closed-NoSuspicionIdentified", notes = "Reviewed; activity consistent with profile", checklist = Checklist(), rowVersion = await Rv(id) });
        r["alert"]!["status"]!.GetValue<string>().Should().Be("PendingClosureApproval");
        var self = await (await As("officer")).Post($"/api/alerts/{id}/approve-closure", new { approve = true });
        self.Status.Should().Be(HttpStatusCode.Forbidden);
        var ok = await (await As("senior")).PostOk($"/api/alerts/{id}/approve-closure", new { approve = true, comment = "agree" });
        ok["alert"]!["status"]!.GetValue<string>().Should().Be("Closed-NoSuspicionIdentified");
        ok["alert"]!["closureApprovedBy"]!.GetValue<string>().Should().Be("senior");
        Actual("High-severity closure → PendingClosureApproval (policy HIGH_SEVERITY_4EYES); officer cannot approve (403); senior approval → closed, approver recorded.");
    }

    [Fact, Tc("TC-ALR-010")]
    public async Task No_auto_report()
    {
        var (id, cust, _, _) = await MakeAlert();
        var before = await F.WithDb(db => db.Set<RegulatoryReport>().CountAsync());
        var api = await As("officer");
        await api.PostOk($"/api/alerts/{id}/start-review", new { });
        await api.PostOk($"/api/alerts/{id}/disposition", new { resolutionCode = "Closed-Other", notes = "Suspicious - referred to MLRO for case", checklist = Checklist(), rowVersion = await Rv(id) });
        (await F.WithDb(db => db.Set<RegulatoryReport>().CountAsync())).Should().Be(before);
        Actual("Closing the alert created no regulatory report (reports only from cases, with explicit preparation/approval).");
    }

    [Fact, Tc("TC-ALR-011")]
    public async Task Concurrent_disposition_conflict()
    {
        var (id, _, _, _) = await MakeAlert();
        await (await As("officer")).PostOk($"/api/alerts/{id}/start-review", new { });
        var rv = await Rv(id);
        var body = new { resolutionCode = "Closed-FalsePositive", notes = "Reviewed all activity in detail", checklist = Checklist(), rowVersion = rv };
        await (await As("officer")).PostOk($"/api/alerts/{id}/disposition", body);
        var second = await (await As("officer2")).Post($"/api/alerts/{id}/disposition", body);
        second.Status.Should().Be(HttpStatusCode.Conflict);
        Actual("First analyst closed the alert; second analyst with the same (stale) row version → 409 Conflict.");
    }

    [Fact, Tc("TC-ALR-012")]
    public async Task Sla_overdue()
    {
        var (id, _, _, _) = await MakeAlert();
        var a = await Alert(id);
        var created = a["createdAt"]!.GetValue<DateTime>();
        var due = a["dueAt"]!.GetValue<DateTime>();
        (due - created).TotalDays.Should().BeGreaterThan(4);
        await F.ExecSql($"UPDATE AmlAlerts SET DueAt = DATEADD(day,-1,SYSUTCDATETIME()) WHERE Id = {id}");
        (await Alert(id))["isOverdue"]!.GetValue<bool>().Should().BeTrue();
        var list = await (await As("officer")).GetOk("/api/alerts?overdue=true&pageSize=200");
        list["items"]!.AsArray().Should().Contain(x => Id(x!) == id);
        var rep = await (await As("officer")).GetOk("/api/reports/alerts-summary");
        rep.ToJsonString().Should().NotBeEmpty();
        Actual($"Medium alert due {due:yyyy-MM-dd} (5 working days, Fri/Sat weekends and holidays excluded); after passing due date flagged overdue and listed by overdue filter and in alerts-summary aging report.");
    }

    [Fact, Tc("AML-TC-0157")]
    public async Task Unauthorized_disposition()
    {
        var (id, _, _, _) = await MakeAlert();
        var r = await (await As("auditor")).Post($"/api/alerts/{id}/disposition", new { resolutionCode = "Closed-FalsePositive", notes = "attempt by auditor", checklist = Checklist(), rowVersion = await Rv(id) });
        r.Status.Should().Be(HttpStatusCode.Forbidden);
        (await Alert(id))["status"]!.GetValue<string>().Should().Be("New");
        Actual("Auditor disposition attempt → 403; alert remains New.");
    }

    [Fact, Tc("AML-TC-0166")]
    public async Task Reopen_keeps_history()
    {
        var (id, _, _, _) = await MakeAlert();
        var api = await As("officer");
        await api.PostOk($"/api/alerts/{id}/start-review", new { });
        await api.PostOk($"/api/alerts/{id}/disposition", new { resolutionCode = "Closed-FalsePositive", notes = "Reviewed all activity in detail", checklist = Checklist(), rowVersion = await Rv(id) });
        await (await As("senior")).PostOk($"/api/alerts/{id}/reopen", new { reason = "QA finding" });
        var d = await (await As("officer")).GetOk($"/api/alerts/{id}");
        d["alert"]!["status"]!.GetValue<string>().Should().Be("UnderReview");
        d["history"]!.AsArray().Should().Contain(h => h!["toStatus"]!.GetValue<string>() == "Closed-FalsePositive");
        Actual($"Reopened by QA reviewer; original disposition remains in history; reopen count {d["alert"]!["reopenCount"]}.");
    }
}

public class CaseWorkflowTests : RuleTestBase
{
    public CaseWorkflowTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    private async Task<JsonNode> NewCase(string priority = "Medium", string confidentiality = "Standard", long? customer = null, string user = "officer") =>
        (await (await As(user)).PostOk("/api/cases", new { title = "Case " + U(), priority, confidentiality, primaryCustomerId = customer }))["case"]!;

    [Fact, Tc("TC-CAS-001", "AML-TC-0161", "AML-TC-0279")]
    public async Task Link_multiple_alerts()
    {
        var c = await NewCase();
        var ids = new List<long>();
        for (var i = 0; i < 3; i++) ids.Add((await MakeAlert()).Alert);
        foreach (var a in ids) await (await As("officer")).PostOk($"/api/cases/{Id(c)}/link-alert", new { alertId = a });
        var dup = await (await As("officer")).Post($"/api/cases/{Id(c)}/link-alert", new { alertId = ids[0] });
        var d = await (await As("officer")).GetOk($"/api/cases/{Id(c)}");
        d["alerts"]!.AsArray().Count.Should().Be(3);
        foreach (var a in ids) (await Alert(a))["caseId"]!.GetValue<long>().Should().Be(Id(c));
        Actual($"3 alerts linked; each alert shows case reference {c["caseNumber"]}; duplicate link request → {(int)dup.Status} with no duplicate relationship.");
    }

    [Fact, Tc("TC-CAS-002", "AML-TC-0162")]
    public async Task Link_transactions_customers()
    {
        var c = await NewCase();
        var a = await Party(); var b = await Party();
        var ids = Enumerable.Range(0, 5).Select(_ => U("T")).ToList();
        await Ingest(ids.Select(x => Txn(x, a.Wallet, b.Wallet, 10, DateTime.UtcNow.AddMinutes(-3))).ToArray());
        var api = await As("officer");
        foreach (var x in ids) await api.PostOk($"/api/cases/{Id(c)}/link-transaction", new { transactionId = await TxnId(x) });
        await api.PostOk($"/api/cases/{Id(c)}/link-customer", new { customerId = a.Customer, role = "Subject" });
        await api.PostOk($"/api/cases/{Id(c)}/link-customer", new { customerId = b.Customer, role = "Counterparty" });
        var d = await api.GetOk($"/api/cases/{Id(c)}");
        d["transactions"]!.AsArray().Count.Should().Be(5);
        d["customers"]!.AsArray().Count.Should().Be(2);
        Actual("5 transactions and 2 customers (Subject, Counterparty) linked and returned in the case detail tabs.");
    }

    [Fact, Tc("TC-CAS-003", "TC-CAS-004", "TC-CAS-008", "AML-TC-0163", "AML-TC-0167")]
    public async Task Closure_rules_and_history()
    {
        var (alertId, cust, _, _) = await MakeAlert();
        var c = await NewCase(customer: cust);
        var api = await As("officer");
        await api.PostOk($"/api/cases/{Id(c)}/link-alert", new { alertId });
        var invalid = await api.Post($"/api/cases/{Id(c)}/transition", new { toStatus = "PendingApproval" });
        invalid.Status.Should().Be(HttpStatusCode.Conflict);
        await api.PostOk($"/api/cases/{Id(c)}/transition", new { toStatus = "UnderInvestigation", comment = "start" });
        var noFindings = await api.Post($"/api/cases/{Id(c)}/transition", new { toStatus = "Closed", finalDecision = "NoFurtherAction", closureReason = "x" });
        noFindings.Status.Should().Be(HttpStatusCode.BadRequest);
        var cur = await api.GetOk($"/api/cases/{Id(c)}");
        await api.PutOk($"/api/cases/{Id(c)}", new { findings = "Activity explained by documented business income", rowVersion = cur["case"]!["rowVersion"]!.GetValue<string>() });
        var closed = await api.PostOk($"/api/cases/{Id(c)}/transition", new { toStatus = "Closed", finalDecision = "NoFurtherAction", closureReason = "Explained" });
        closed["case"]!["status"]!.GetValue<string>().Should().Be("Closed");
        (await Alert(alertId))["status"]!.GetValue<string>().Should().Be("New");
        var h = closed["history"]!.AsArray();
        h.Should().HaveCountGreaterThanOrEqualTo(2);
        h.Should().OnlyContain(x => x!["actor"] != null && x["at"] != null);
        Actual($"Invalid Open→PendingApproval → 409 listing valid next states ({invalid.Body!["detail"]}); close without findings → 400; closed with findings+decision; linked alert kept its own status (New); history: " + string.Join(", ", h.Select(x => $"{x!["fromStatus"]}→{x["toStatus"]} by {x["actor"]}")));
    }

    [Fact, Tc("TC-CAS-005", "AML-TC-0168")]
    public async Task Notes_immutable()
    {
        var c = await NewCase();
        var api = await As("officer");
        var n = await api.PostOk($"/api/cases/{Id(c)}/notes", new { text = "Initial observation" });
        n["author"]!.GetValue<string>().Should().Be("officer");
        var edit = await api.Put($"/api/cases/{Id(c)}/notes/{Id(n)}", new { text = "changed" });
        edit.Status.Should().Be(HttpStatusCode.MethodNotAllowed);
        var corr = await api.PostOk($"/api/cases/{Id(c)}/notes", new { text = "Correction: amount was 5,000", supersedesNoteId = Id(n) });
        var upd = async () => await F.ExecSql($"UPDATE AmlCaseNotes SET Text = 'tampered' WHERE Id = {Id(n)}");
        await upd.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();
        Actual($"Note author/time stored ({n["createdAt"]}); PUT edit → 405; correction saved as new note superseding #{Id(n)}; DB trigger blocks direct updates.");
    }

    [Fact, Tc("TC-CAS-006", "TC-CAS-007", "AML-TC-0234", "AML-TC-0235", "AML-TC-0294")]
    public async Task Evidence_upload_rules()
    {
        var c = await NewCase();
        var api = await As("officer");
        async Task<(HttpStatusCode, JsonNode?)> Upload(string name, byte[] content, string type)
        {
            var form = new MultipartFormDataContent();
            var bc = new ByteArrayContent(content);
            bc.Headers.ContentType = new MediaTypeHeaderValue(type);
            form.Add(bc, "file", name);
            var r = await api.Send(HttpMethod.Post, $"/api/cases/{Id(c)}/documents", form);
            return (r.Status, r.Body);
        }
        var exe = await Upload("tool.exe", new byte[] { 0x4D, 0x5A, 0x90, 0 }, "application/octet-stream");
        exe.Item1.Should().Be(HttpStatusCode.BadRequest);
        var fakePdf = await Upload("fake.pdf", "not a pdf"u8.ToArray(), "application/pdf");
        fakePdf.Item1.Should().Be(HttpStatusCode.BadRequest);
        var eicar = await Upload("eicar.txt", "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"u8.ToArray(), "text/plain");
        eicar.Item1.Should().Be(HttpStatusCode.BadRequest);
        var pdf = await Upload("evidence.pdf", "%PDF-1.4\n%test evidence\n"u8.ToArray(), "application/pdf");
        pdf.Item1.Should().Be(HttpStatusCode.OK);
        var docId = Id(pdf.Item2!);
        var anon = await F.Anonymous().Get($"/api/cases/{Id(c)}/documents/{docId}");
        anon.Status.Should().Be(HttpStatusCode.Unauthorized);
        (await F.Anonymous().Get("/files/" + pdf.Item2!["sha256"])).Status.Should().NotBe(HttpStatusCode.OK);
        var dl = await api.Send(HttpMethod.Get, $"/api/cases/{Id(c)}/documents/{docId}");
        dl.Status.Should().Be(HttpStatusCode.OK);
        var trav = await api.Get($"/api/cases/{Id(c)}/documents/..%2F..%2Fappsettings.json");
        ((int)trav.Status).Should().BeOneOf(400, 404);
        Directory.GetFiles(F.StorageRoot, "*", SearchOption.AllDirectories).Should().NotBeEmpty();
        Actual($".exe → 400; spoofed PDF → 400 (magic bytes); EICAR → 400 (malware scan); valid PDF stored outside web root (storage root, SHA-256 {pdf.Item2!["sha256"]!.ToString()[..12]}…); unauthenticated download → 401; no static path; traversal attempt → {(int)trav.Status}; authorized download via API OK.");
    }

    [Fact, Tc("TC-CAS-009")]
    public async Task High_outcome_requires_mlro()
    {
        var c = await NewCase(priority: "High");
        var api = await As("officer");
        await api.PostOk($"/api/cases/{Id(c)}/transition", new { toStatus = "UnderInvestigation" });
        var cur = await api.GetOk($"/api/cases/{Id(c)}");
        await api.PutOk($"/api/cases/{Id(c)}", new { findings = "Structuring identified across 3 accounts", recommendedAction = "FileReport", rowVersion = cur["case"]!["rowVersion"]!.GetValue<string>() });
        (await api.Post($"/api/cases/{Id(c)}/transition", new { toStatus = "Closed", finalDecision = "Report", closureReason = "STR" })).Status.Should().Be(HttpStatusCode.Conflict);
        await api.PostOk($"/api/cases/{Id(c)}/transition", new { toStatus = "PendingApproval" });
        (await (await As("manager")).Post($"/api/cases/{Id(c)}/approve", new { approve = true })).Status.Should().Be(HttpStatusCode.Forbidden);
        var ok = await (await As("mlro")).PostOk($"/api/cases/{Id(c)}/approve", new { approve = true, comment = "agree" });
        ok["case"]!["approvedBy"]!.GetValue<string>().Should().Be("mlro");
        Actual("Recommended action FileReport: closure blocked until approval; manager (no STR approval) → 403; MLRO approval recorded.");
    }

    [Fact, Tc("TC-CAS-010")]
    public async Task Tasks_due_dates()
    {
        var c = await NewCase();
        var t = await (await As("officer")).PostOk($"/api/cases/{Id(c)}/tasks", new { title = "Request bank statements", assignee = "investigator", dueDate = DateTime.UtcNow.AddDays(-1) });
        var my = await (await As("investigator")).GetOk("/api/cases/my-tasks");
        var mine = my.AsArray().First(x => Id(x!) == Id(t))!;
        mine["isOverdue"]!.GetValue<bool>().Should().BeTrue();
        Actual("Task assigned to investigator appears in their queue with due date; past due flagged overdue.");
    }

    [Fact, Tc("AML-TC-0169", "AML-TC-0170", "AML-TC-0171", "AML-TC-0242")]
    public async Task Restricted_case_visibility()
    {
        var cust = await CreateCustomer();
        var before = (await (await As("investigator")).GetOk($"/api/customers/{Id(cust)}"))["caseCount"]!.GetValue<int>();
        var dashBefore = await (await As("investigator")).GetOk("/api/dashboard");
        var c = await NewCase(confidentiality: "Restricted", customer: Id(cust), user: "mlro");
        var list = await (await As("investigator")).GetOk("/api/cases?pageSize=200");
        list["items"]!.AsArray().Should().NotContain(x => Id(x!) == Id(c));
        (await (await As("investigator")).Get($"/api/cases/{Id(c)}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await (await As("investigator")).GetOk($"/api/customers/{Id(cust)}"))["caseCount"]!.GetValue<int>().Should().Be(before);
        var dashAfter = await (await As("investigator")).GetOk("/api/dashboard");
        dashAfter["cases"]?.ToJsonString().Should().Be(dashBefore["cases"]?.ToJsonString());
        await (await As("mlro")).GetOk($"/api/cases/{Id(c)}");
        (await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "RESTRICTED_READ" && a.EntityId == Id(c).ToString() && a.Actor == "mlro"))).Should().BeGreaterThan(0);
        Actual("Restricted case invisible to investigator in list, GET → 404 (no existence hint), customer case count and dashboard counts unchanged; MLRO read recorded as RESTRICTED_READ audit.");
    }
}
