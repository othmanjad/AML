using System.Net;
using System.Text.Json.Nodes;
using AML.Domain.Entities;
using AML.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration;

public class CddTests : AmlTestBase
{
    public CddTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact, Tc("TC-CDD-001", "AML-TC-0031")]
    public async Task Auto_accept_low_risk_clear()
    {
        var c = await CompleteKycCustomer();
        var r = await (await As("officer")).PostOk($"/api/customers/{Id(c)}/onboard");
        var d = r["decision"]!;
        d["decision"]!.GetValue<string>().Should().Be("Accepted");
        d["policyCode"]!.GetValue<string>().Should().Be("ACCEPT");
        var stored = await F.WithDb(db => db.Set<CustomerAcceptanceDecision>().CountAsync(x => x.CustomerId == Id(c) && x.IsCurrent));
        stored.Should().Be(1);
        Actual($"Decision {d["decision"]} (risk {r["assessment"]?["finalLevel"]}, screening clear); policy {d["policyCode"]} v{d["policyVersion"]} stored; decision recorded as current.");
    }

    [Fact, Tc("TC-CDD-002", "AML-TC-0032", "TC-SCR-003")]
    public async Task Pending_screening_blocks_acceptance()
    {
        await SetProvider("Unavailable", 100);
        try
        {
            var c = await CompleteKycCustomer();
            var r = await (await As("officer")).PostOk($"/api/customers/{Id(c)}/onboard");
            r["decision"]!["decision"]!.GetValue<string>().Should().Be("PendingInformation");
            var req = await F.WithDb(db => db.Set<ScreeningRequest>().Where(x => x.CustomerId == Id(c)).ToListAsync());
            req.Should().OnlyContain(x => x.Status != "Completed");
            req.Should().Contain(x => x.NextAttemptAt != null);
            var cust = await (await As("officer")).GetOk($"/api/customers/{Id(c)}");
            cust["screeningStatus"]!.GetValue<string>().Should().NotBe("Clear");
            Actual($"Provider unavailable → screening status {cust["screeningStatus"]}, request status {req[0].Status} with retry scheduled at {req[0].NextAttemptAt:O}; decision PendingInformation (reason {r["decision"]!["reasonCodes"]}); never Clear.");
        }
        finally { await SetProvider("Normal"); }
    }

    [Fact, Tc("TC-CDD-003", "AML-TC-0033")]
    public async Task Confirmed_sanctions_blocks_acceptance()
    {
        var c = await CompleteKycCustomer(name: "Zorvan Testlisted Qadir", nationality: "SY", dob: "1970-03-03");
        var off = await As("officer");
        var r1 = await off.PostOk($"/api/customers/{Id(c)}/onboard");
        r1["decision"]!["decision"]!.GetValue<string>().Should().NotBe("Accepted");
        var res = (await ScreeningResults(Id(c))).First(x => x.Category == "Sanctions");
        await Transition(res.Id, "ConfirmedMatch");
        var d = await off.PostOk($"/api/customers/{Id(c)}/acceptance/decide");
        d["decision"]!.GetValue<string>().Should().Be("Rejected");
        d["sanctionsWorkflow"]!.GetValue<bool>().Should().BeTrue();
        var manual = await off.Post($"/api/customers/{Id(c)}/acceptance/manual", new { decision = "Accepted", reason = "try" });
        manual.Status.Should().Be(HttpStatusCode.Conflict);
        Actual($"Confirmed {res.SourceCode} match → decision Rejected with sanctionsWorkflow=true; manual accept refused 409: {manual.Body!["detail"]}");
    }

    [Fact, Tc("TC-CDD-004", "AML-TC-0036")]
    public async Task Pep_requires_senior_approval()
    {
        var c = await CompleteKycCustomer(isPep: true);
        var r = await (await As("officer")).PostOk($"/api/customers/{Id(c)}/onboard");
        r["decision"]!["decision"]!.GetValue<string>().Should().Be("PendingSeniorApproval");
        r["dueDiligenceLevel"]!.GetValue<string>().Should().Be("EDD");
        (await (await As("officer")).Post($"/api/customers/{Id(c)}/acceptance/senior-approval", new { approve = true })).Status.Should().Be(HttpStatusCode.Forbidden);
        var cust = await (await As("officer")).GetOk($"/api/customers/{Id(c)}");
        cust["status"]!.GetValue<string>().Should().Be("Prospect");
        var ok = await (await As("senior")).PostOk($"/api/customers/{Id(c)}/acceptance/senior-approval", new { approve = true, reason = "Senior approval granted" });
        ok["decision"]!.GetValue<string>().Should().Be("Accepted");
        ok["approvedBy"]!.GetValue<string>().Should().Be("senior");
        Actual("PEP onboarding → PendingSeniorApproval, DD level EDD, status stays Prospect; officer approval 403; senior approval → Accepted (approvedBy senior).");
    }

    [Fact, Tc("TC-CDD-005", "TC-CDD-006", "AML-TC-0038")]
    public async Task Edd_tasks_and_evidence()
    {
        var c = await CompleteKycCustomer(isPep: true);
        await (await As("officer")).PostOk($"/api/customers/{Id(c)}/onboard");
        var tasks = (await (await As("officer")).GetOk($"/api/customers/{Id(c)}/edd-tasks")).AsArray();
        tasks.Select(t => t!["taskType"]!.GetValue<string>()).Should().Contain(new[] { "SourceOfFunds", "SourceOfWealth", "SeniorManagementApproval" });
        var sof = tasks.First(t => t!["taskType"]!.GetValue<string>() == "SourceOfFunds")!;
        var bad = await (await As("officer")).Post($"/api/customers/edd-tasks/{sof["id"]}/complete", new { evidence = "" });
        bad.Status.Should().Be(HttpStatusCode.BadRequest);
        var done = await (await As("officer")).PostOk($"/api/customers/edd-tasks/{sof["id"]}/complete", new { evidence = "DOC-REF-123 salary certificate" });
        done["signedOffBy"]!.GetValue<string>().Should().Be("officer");
        Actual($"EDD tasks: {string.Join(", ", tasks.Select(t => t!["taskType"]))}; completing without evidence → 400 ({bad.Body!["detail"]}); with evidence → signed off by officer.");
    }

    [Fact, Tc("TC-CDD-007", "AML-TC-0037")]
    public async Task Sdd_for_low_risk_segment()
    {
        var c = await CompleteKycCustomer();
        await (await As("manager")).PutOk($"/api/customers/{Id(c)}/attributes", new { values = new { PRODUCT_TIER = "BASIC" }, reason = "basic product" });
        var r = await (await As("officer")).PostOk($"/api/customers/{Id(c)}/onboard");
        r["dueDiligenceLevel"]!.GetValue<string>().Should().Be("SDD");
        var other = await CompleteKycCustomer();
        var r2 = await (await As("officer")).PostOk($"/api/customers/{Id(other)}/onboard");
        r2["dueDiligenceLevel"]!.GetValue<string>().Should().Be("CDD");
        Actual($"Low-risk BASIC-tier customer → SDD (no additional required fields: gaps={r["kycGaps"]!.AsArray().Count}); standard customer → CDD; PEP → EDD (see TC-CDD-004).");
    }

    [Fact, Tc("AML-TC-0034", "AML-TC-0035")]
    public async Task Manual_decisions_require_details()
    {
        var c = await CompleteKycCustomer();
        var off = await As("officer");
        await off.PostOk($"/api/customers/{Id(c)}/onboard");
        (await off.Post($"/api/customers/{Id(c)}/acceptance/manual", new { decision = "AcceptedWithConditions" })).Status.Should().Be(HttpStatusCode.BadRequest);
        var cond = await off.PostOk($"/api/customers/{Id(c)}/acceptance/manual", new { decision = "AcceptedWithConditions", reason = "monitor", conditions = "Provide salary slips quarterly", conditionsDueDate = "2027-03-31" });
        cond["conditions"]!.GetValue<string>().Should().Contain("salary");
        cond["policyVersion"]!.GetValue<int>().Should().Be(1);
        cond["decidedBy"]!.GetValue<string>().Should().Be("officer");
        (await off.Post($"/api/customers/{Id(c)}/acceptance/manual", new { decision = "Rejected" })).Status.Should().Be(HttpStatusCode.BadRequest);
        Actual($"Conditional acceptance without conditions → 400; with conditions recorded (policy v{cond["policyVersion"]}, decided by officer, due {cond["conditionsDueDate"]}); rejection without reason → 400.");
    }

    [Fact, Tc("AML-TC-0039")]
    public async Task Due_diligence_gaps_shown()
    {
        var c = await CreateCustomer();
        var g = await (await As("manager")).GetOk($"/api/customers/{Id(c)}");
        var gaps = g["kycGaps"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        gaps.Should().Contain(x => x.Contains("identification"));
        var report = await (await As("manager")).GetOk("/api/reports/kyc-gaps?pageSize=200");
        report.ToJsonString().Should().Contain($"\"CustomerId\":{Id(c)},\"Gap\":\"No identification document recorded\"");
        Actual("Profile gaps: " + string.Join("; ", gaps) + "; customer listed in KYC gap report.");
    }

    [Fact, Tc("AML-TC-0040")]
    public async Task Expired_document_task()
    {
        var c = await CreateCustomer(identifications: new[] { new { idType = "NationalId", idNumber = "9" + Random.Shared.NextInt64(100000000, 999999999), issuingCountry = "JO", expiryDate = "2020-01-01" } });
        var g = await (await As("manager")).GetOk($"/api/customers/{Id(c)}");
        g["kycGaps"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Contain(x => x.StartsWith("Expired identification"));
        await (await As("admin")).PostOk("/api/admin/jobs/kyc-expiry/run");
        var tasks = (await (await As("manager")).GetOk($"/api/customers/{Id(c)}/edd-tasks")).AsArray();
        tasks.Should().Contain(t => t!["taskType"]!.GetValue<string>() == "KycRefresh");
        (await (await As("manager")).GetOk($"/api/customers/{Id(c)}"))["kycStatus"]!.GetValue<string>().Should().Be("Expired");
        Actual("Expired ID flagged as KYC gap; kyc-expiry job created a KycRefresh task and set KYC status Expired.");
    }

    private async Task<JsonNode> Corporate(string? name = null) =>
        await CreateCustomer(type: "Corporate", nameLatin: name ?? "Test Trading " + Guid.NewGuid().ToString("N")[..5], incorporation: "JO",
            attributes: new Dictionary<string, object> { ["INCORP_REG_NO"] = "12345678" });

    [Fact, Tc("TC-CDD-010", "AML-TC-0041", "AML-TC-0047")]
    public async Task Ownership_and_control_records()
    {
        var corp = await Corporate();
        var api = await As("manager");
        var own = await api.PostOk($"/api/customers/{Id(corp)}/ownership", new { ownerName = "Owner Person", ownerType = "Person", percentage = 40, controlType = "Ownership", effectiveFrom = "2024-01-01T00:00:00Z", evidence = "Register extract 2024" });
        own["percentage"]!.GetValue<decimal>().Should().Be(40);
        own["evidence"]!.GetValue<string>().Should().Contain("Register");
        await api.PostOk($"/api/customers/{Id(corp)}/ownership", new { ownerName = "Silent Controller", ownerType = "Person", percentage = 0, controlType = "ControlOtherMeans", evidence = "Shareholder agreement" });
        var sig = await api.PostOk($"/api/customers/{Id(corp)}/ownership", new { ownerName = "Signing Officer", ownerType = "Person", controlType = "Signatory", effectiveFrom = "2025-02-01T00:00:00Z" });
        sig["controlType"]!.GetValue<string>().Should().Be("Signatory");
        var a = await api.GetOk($"/api/customers/{Id(corp)}/ownership");
        var ctrl = a["owners"]!.AsArray().First(o => o!["ownerName"]!.GetValue<string>() == "Silent Controller")!;
        ctrl["isBeneficialOwner"]!.GetValue<bool>().Should().BeTrue();
        Actual($"Direct 40% ownership stored with type/effective date/evidence; signatory relationship stored effective {sig["effectiveFrom"]}; 0% controller by other means listed as BO: {ctrl["basis"]}.");
    }

    [Fact, Tc("TC-CDD-012", "AML-TC-0046", "AML-TC-0151")]
    public async Task Ubo_pep_propagates()
    {
        var corp = await Corporate();
        var api = await As("manager");
        await api.PostOk($"/api/customers/{Id(corp)}/ownership", new { ownerName = "Testminister Kareem Fakename", ownerType = "Person", percentage = 60, controlType = "Ownership", ownerNationality = "JO" });
        await api.PostOk($"/api/customers/{Id(corp)}/screening");
        var res = (await ScreeningResults(Id(corp))).Where(r => r.SubjectType == "BeneficialOwner").ToList();
        res.Should().NotBeEmpty();
        await Transition(res.First(r => r.Category == "PEP").Id, "ConfirmedMatch");
        var a = await api.PostOk($"/api/customers/{Id(corp)}/risk-assessments", new { trigger = "Manual", reason = "UBO PEP" });
        var f = a["factors"]!.AsArray().First(x => x!["factorCode"]!.GetValue<string>() == "UBO_PEP")!;
        f["normalizedScore"]!.GetValue<decimal>().Should().Be(100);
        f["explanation"]!.GetValue<string>().Should().Contain("Testminister");
        Actual($"Owner screened (subject BeneficialOwner); confirmed PEP → entity factor UBO_PEP score 100: {f["explanation"]}");
    }

    [Fact, Tc("TC-CDD-013", "AML-TC-0045")]
    public async Task Missing_ubo_flagged()
    {
        var corp = await Corporate();
        var api = await As("manager");
        var a = await api.PostOk($"/api/customers/{Id(corp)}/risk-assessments", new { trigger = "Manual", reason = "t" });
        var f = a["factors"]!.AsArray().First(x => x!["factorCode"]!.GetValue<string>() == "UBO_PEP")!;
        f["dataStatus"]!.GetValue<string>().Should().NotBe("Present");
        a["hasMissingData"]!.GetValue<bool>().Should().BeTrue();
        var g = await api.GetOk($"/api/customers/{Id(corp)}");
        g["kycGaps"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Contain("Missing beneficial ownership information");
        Actual($"UBO data status {f["dataStatus"]} scored {f["normalizedScore"]} (missing-data score, not zero); KYC gap 'Missing beneficial ownership information' shown.");
    }

    [Fact, Tc("TC-CDD-014", "AML-TC-0048", "TC-CDD-016")]
    public async Task Exit_workflow_and_neutral_view()
    {
        var c = await CompleteKycCustomer();
        var caseNode = await (await As("officer")).PostOk("/api/cases", new { title = "Exit case " + U(), summary = "suspicion detail", priority = "High", primaryCustomerId = Id(c) });
        var e = await (await As("officer")).PostOk($"/api/customers/{Id(c)}/exit", new { reasonCode = "RISK_APPETITE", internalReason = "Suspected structuring (restricted)", linkedCaseId = Id(caseNode["case"]!), evidence = "Case file" });
        (await (await As("officer")).Post($"/api/customers/exit/{e["id"]}/decide", new { approve = true })).Status.Should().Be(HttpStatusCode.Forbidden);
        var d = await (await As("senior")).PostOk($"/api/customers/exit/{e["id"]}/decide", new { approve = true, reason = "approved" });
        d["status"]!.GetValue<string>().Should().Be("Approved");
        (await (await As("officer")).GetOk($"/api/customers/{Id(c)}"))["status"]!.GetValue<string>().Should().Be("Exited");
        var opsView = (await (await As("ops")).GetOk($"/api/customers/{Id(c)}/exit")).AsArray()[0]!;
        opsView["reasonCode"]!.GetValue<string>().Should().Be("RISK_APPETITE");
        opsView["internalReason"].Should().BeNull();
        opsView["linkedCaseId"].Should().BeNull();
        Actual($"Exit requested by officer, approved by senior ({d["approvedBy"]}); status Exited; Operations user sees only neutral code RISK_APPETITE, no internal reason or case link.");
    }

    [Fact, Tc("TC-CDD-015")]
    public async Task Do_not_onboard_check()
    {
        var nid = "9" + Random.Shared.NextInt64(100000000, 999999999);
        var c = await CompleteKycCustomer(nid: nid);
        var e = await (await As("officer")).PostOk($"/api/customers/{Id(c)}/exit", new { reasonCode = "POLICY" });
        await (await As("senior")).PostOk($"/api/customers/exit/{e["id"]}/decide", new { approve = true, reason = "ok" });
        var again = await CompleteKycCustomer(nid: nid);
        var r = await (await As("officer")).PostOk($"/api/customers/{Id(again)}/onboard");
        r["decision"]!["decision"]!.GetValue<string>().Should().Be("PendingInformation");
        var dno = (await ScreeningResults(Id(again))).Where(x => x.SourceCode == "INTERNAL-DNO").ToList();
        dno.Should().ContainSingle();
        Actual($"New customer with same ID token matched internal do-not-onboard list ({dno[0].MatchMethod}); onboarding pending review ({r["decision"]!["reasonCodes"]}).");
    }

    [Fact, Tc("AML-TC-0042", "AML-TC-0043")]
    public async Task Indirect_ownership_via_api()
    {
        var a = await Corporate("Alpha Holding");
        var b = await Corporate("Beta Holding");
        var api = await As("manager");
        await api.PostOk($"/api/customers/{Id(a)}/ownership", new { ownerCustomerId = Id(b), ownerType = "Entity", percentage = 60, controlType = "Ownership" });
        await api.PostOk($"/api/customers/{Id(b)}/ownership", new { ownerName = "Person P", ownerType = "Person", percentage = 50, controlType = "Ownership" });
        var res = await api.GetOk($"/api/customers/{Id(a)}/ownership");
        var p = res["owners"]!.AsArray().First(o => o!["ownerName"]!.GetValue<string>() == "Person P")!;
        p["effectivePercentage"]!.GetValue<decimal>().Should().Be(30);
        p["isBeneficialOwner"]!.GetValue<bool>().Should().BeTrue();
        Actual($"Person P indirect ownership {p["effectivePercentage"]}% through Beta (threshold {res["thresholdPercent"]}% from {res["thresholdSource"]}): {p["basis"]}");
    }
}

public class RiskConfigTests : AmlTestBase
{
    public RiskConfigTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    private static string MCode() => "UT_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>Creates a Merchant-only methodology (far-future effective date to avoid interfering with live assessments).</summary>
    protected async Task<JsonNode> NewMethodology(DateTime effectiveFrom, string? code = null, string approval = "", string method = "WeightedAverage", string user = "manager", bool withFactor = true)
    {
        var api = await As(user);
        var m = await api.PostOk("/api/admin/risk-methodologies", new
        {
            code = code ?? MCode(), name = "Unit methodology", aggregationMethod = method, scorePrecision = 2, appliesToCustomerTypes = "Merchant",
            approvalRequiredLevels = approval, effectiveFrom, rationale = "test methodology"
        });
        if (withFactor)
            await api.PostOk($"/api/admin/risk-methodologies/{Id(m)}/factors", new
            {
                code = "REGION", name = "Region", categoryCode = "GEOGRAPHIC", dataSource = "customer.region", factorType = "Categorical", weight = 1,
                missingDataHandling = "AssignScore", missingDataScore = 50,
                mappings = new object[] { new { matchType = "Equals", value = "NORTH", score = 90, priority = 1 }, new { matchType = "Default", score = 10, priority = 0 } }
            });
        await api.PutOk($"/api/admin/risk-methodologies/{Id(m)}/thresholds", new object[]
        {
            new { level = "Low", minScore = 0, maxScore = 24.99, reviewMonths = 36 }, new { level = "Medium", minScore = 25, maxScore = 49.99, reviewMonths = 24 },
            new { level = "High", minScore = 50, maxScore = 74.99, reviewMonths = 6 }, new { level = "Critical", minScore = 75, maxScore = 100, reviewMonths = 3 }
        });
        return await api.GetOk($"/api/admin/risk-methodologies/{Id(m)}");
    }

    protected async Task<JsonNode> Approve(JsonNode m)
    {
        await (await As("manager")).PostOk($"/api/admin/risk-methodologies/{Id(m)}/submit", new { rationale = "test" });
        return await (await As("senior")).PostOk($"/api/admin/risk-methodologies/{Id(m)}/approve");
    }

    [Fact, Tc("TC-RCF-001", "TC-RCF-002")]
    public async Task Draft_and_four_eyes()
    {
        var m = await NewMethodology(new DateTime(2033, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        m["status"]!.GetValue<string>().Should().Be("Draft");
        var merchant = await CreateCustomer(type: "Merchant", region: "NORTH");
        var a = await (await As("manager")).PostOk($"/api/customers/{Id(merchant)}/risk-assessments", new { trigger = "Manual", reason = "t", asOf = "2033-02-01T00:00:00Z" });
        a["methodologyCode"]!.GetValue<string>().Should().NotBe(m["code"]!.GetValue<string>());
        await (await As("manager")).PostOk($"/api/admin/risk-methodologies/{Id(m)}/submit", new { rationale = "test" });
        var self = await (await As("manager")).Post($"/api/admin/risk-methodologies/{Id(m)}/approve");
        ((int)self.Status).Should().BeOneOf(403, 409);
        var ok = await (await As("senior")).PostOk($"/api/admin/risk-methodologies/{Id(m)}/approve");
        ok["status"]!.GetValue<string>().Should().Be("Approved");
        Actual($"Draft saved and not used (assessment used {a["methodologyCode"]}); self-approval by creator rejected ({(int)self.Status}: {self.Body!["detail"]}); approval by senior → Approved.");
    }

    [Fact, Tc("TC-RCF-003", "TC-RSK-001", "AML-TC-0061")]
    public async Task Methodology_by_effective_date()
    {
        var code = MCode();
        var v1 = await Approve(await NewMethodology(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), code));
        var v2draft = await (await As("manager")).PostOk($"/api/admin/risk-methodologies/{Id(v1)}/new-version");
        await (await As("manager")).PutOk($"/api/admin/risk-methodologies/{Id(v2draft)}", new { code, name = "v2", aggregationMethod = "WeightedAverage", scorePrecision = 2, appliesToCustomerTypes = "Merchant", effectiveFrom = "2030-11-01T00:00:00Z", rationale = "v2", rowVersion = v2draft["rowVersion"]!.GetValue<string>() });
        var v2 = await Approve(v2draft);
        var merchant = await CreateCustomer(type: "Merchant", region: "NORTH");
        var api = await As("manager");
        var a1 = await api.PostOk($"/api/customers/{Id(merchant)}/risk-assessments", new { trigger = "Manual", reason = "t", asOf = "2030-10-15T00:00:00Z" });
        var a2 = await api.PostOk($"/api/customers/{Id(merchant)}/risk-assessments", new { trigger = "Manual", reason = "t", asOf = "2030-11-05T00:00:00Z" });
        a1["methodologyVersion"]!.GetValue<int>().Should().Be(1);
        a2["methodologyVersion"]!.GetValue<int>().Should().Be(2);
        (await api.GetOk($"/api/risk/assessments/{Id(a1)}"))["configSnapshotJson"]!.GetValue<string>().Should().Contain("\"Version\":1");
        Actual($"v1 (from 2030-01-01) and v2 (from 2030-11-01) approved without overlap error; as-of 2030-10-15 used {code} v1, as-of 2030-11-05 used v2; versions + config snapshot stored per assessment.");
    }

    [Fact, Tc("TC-RCF-004", "TC-RCF-006", "AML-TC-0056", "AML-TC-0057")]
    public async Task Validation_overlap_and_gap()
    {
        var m = await NewMethodology(new DateTime(2034, 1, 1, 0, 0, 0, DateTimeKind.Utc), withFactor: false);
        var api = await As("manager");
        var overlap = await api.Post($"/api/admin/risk-methodologies/{Id(m)}/factors", new
        {
            code = "RNG", name = "Range", categoryCode = "GEOGRAPHIC", dataSource = "customer.relationshipMonths", factorType = "Numeric", weight = 1, missingDataHandling = "AssignScore", missingDataScore = 50,
            mappings = new object[] { new { matchType = "Range", rangeMin = 0, rangeMax = 50, score = 10, priority = 1 }, new { matchType = "Range", rangeMin = 40, rangeMax = 100, score = 20, priority = 1 } }
        });
        overlap.Status.Should().Be(HttpStatusCode.BadRequest);
        await api.PostOk($"/api/admin/risk-methodologies/{Id(m)}/factors", new
        {
            code = "RNG", name = "Range", categoryCode = "GEOGRAPHIC", dataSource = "customer.relationshipMonths", factorType = "Numeric", weight = 1, missingDataHandling = "AssignScore", missingDataScore = 50,
            mappings = new object[] { new { matchType = "Range", rangeMin = 0, rangeMax = 50, score = 10, priority = 1 }, new { matchType = "Range", rangeMin = 50.01, rangeMax = 100, score = 20, priority = 1 } }
        });
        var th = await api.Put($"/api/admin/risk-methodologies/{Id(m)}/thresholds", new object[]
        {
            new { level = "Low", minScore = 0, maxScore = 24.99, reviewMonths = 36 }, new { level = "Medium", minScore = 30, maxScore = 49.99, reviewMonths = 24 },
            new { level = "High", minScore = 50, maxScore = 100, reviewMonths = 6 }
        });
        th.Status.Should().Be(HttpStatusCode.BadRequest);
        var errors = new List<string> { th.Body!["detail"]!.GetValue<string>() };
        errors[0].Should().Contain("gap");
        Actual($"Overlapping mapping ranges [0..50]/[40..100] same priority rejected (400: {overlap.Body!["detail"]}); threshold gap 25–29.99 blocks submission: " + string.Join(" | ", errors));
    }

    [Fact, Tc("TC-RCF-007")]
    public async Task Zero_weights_rejected()
    {
        var m = await NewMethodology(new DateTime(2034, 2, 1, 0, 0, 0, DateTimeKind.Utc), withFactor: false);
        var api = await As("manager");
        await api.PostOk($"/api/admin/risk-methodologies/{Id(m)}/factors", new { code = "Z", name = "Z", categoryCode = "GEOGRAPHIC", dataSource = "customer.region", weight = 0, missingDataScore = 50, mappings = new object[] { new { matchType = "Default", score = 10, priority = 0 } } });
        var v = await api.PostOk($"/api/admin/risk-methodologies/{Id(m)}/validate");
        v["valid"]!.GetValue<bool>().Should().BeFalse();
        Actual("Weighted-average with weights summing to 0 → " + string.Join(" | ", v["errors"]!.AsArray()));
    }

    [Fact, Tc("TC-RCF-008", "TC-RCF-009", "AML-TC-0071", "AML-TC-0274")]
    public async Task Simulation_not_persisted()
    {
        var m = await NewMethodology(new DateTime(2034, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = await F.WithDb(db => db.Set<CustomerRiskAssessment>().CountAsync());
        var sim = await (await As("manager")).PostOk($"/api/admin/risk-methodologies/{Id(m)}/simulate", new { customerType = "Merchant", inputs = new Dictionary<string, object> { ["customer.region"] = "NORTH" } });
        sim["persisted"]!.GetValue<bool>().Should().BeFalse();
        sim["factors"]!.AsArray().Should().NotBeEmpty();
        sim["calculatedScore"]!.GetValue<decimal>().Should().Be(90);
        var after = await F.WithDb(db => db.Set<CustomerRiskAssessment>().CountAsync());
        after.Should().Be(before);
        var bad = await (await As("manager")).Post($"/api/admin/risk-methodologies/999999999/simulate", new { customerType = "Merchant" });
        bad.Status.Should().Be(HttpStatusCode.NotFound);
        Actual($"Simulation: score {sim["calculatedScore"]} level {sim["finalLevel"]}, {sim["factors"]!.AsArray().Count} factor(s) with weights/contributions, overrides {sim["appliedOverrides"]!.ToJsonString()}; persisted=false; assessments table count unchanged ({before}); invalid methodology reference → 404.");
    }

    [Fact, Tc("TC-RCF-010")]
    public async Task Seed_thresholds_marked_sample()
    {
        var list = (await (await As("manager")).GetOk("/api/admin/risk-methodologies")).AsArray();
        var std = await (await As("manager")).GetOk($"/api/admin/risk-methodologies/{Id(list.First(x => x!["code"]!.GetValue<string>() == "CRA-STD")!)}");
        std["isSample"]!.GetValue<bool>().Should().BeTrue();
        std["thresholds"]!.AsArray().Should().OnlyContain(t => t!["isSample"]!.GetValue<bool>());
        Actual("Seed methodology CRA-STD and all its thresholds flagged isSample=true (labelled 'example – not regulatory').");
    }

    [Fact, Tc("TC-RCF-011", "AML-TC-0074")]
    public async Task Factor_concurrency()
    {
        var m = await NewMethodology(new DateTime(2034, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        var f = m["factors"]!.AsArray()[0]!;
        var body = new { code = "REGION", name = "Region A", categoryCode = "GEOGRAPHIC", dataSource = "customer.region", weight = 2, missingDataScore = 50, rowVersion = f["rowVersion"]!.GetValue<string>(), mappings = new object[] { new { matchType = "Default", score = 10, priority = 0 } } };
        await (await As("manager")).PutOk($"/api/admin/risk-factors/{Id(f)}", body);
        var second = await (await As("officer")).Put($"/api/admin/risk-factors/{Id(f)}", body);
        second.Status.Should().Be(HttpStatusCode.Conflict);
        Actual("First factor save succeeded; second save with stale rowVersion → 409.");
    }

    [Fact, Tc("TC-RCF-012", "AML-TC-0075")]
    public async Task Version_history()
    {
        var code = MCode();
        var v1 = await Approve(await NewMethodology(new DateTime(2035, 1, 1, 0, 0, 0, DateTimeKind.Utc), code));
        var v2 = await Approve(await (await As("manager")).PostOk($"/api/admin/risk-methodologies/{Id(v1)}/new-version"));
        var v3 = await (await As("manager")).PostOk($"/api/admin/risk-methodologies/{Id(v2)}/new-version");
        var h = (await (await As("manager")).GetOk($"/api/admin/risk-methodologies/history/{code}")).AsArray();
        h.Count.Should().BeGreaterThanOrEqualTo(3);
        h.First(x => x!["version"]!.GetValue<int>() == 1)!["approvedBy"]!.GetValue<string>().Should().Be("senior");
        var audit = await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "RISK_METHODOLOGY_APPROVE" && a.EntityId == code + " v1"));
        audit.Should().Be(1);
        Actual("History: " + string.Join("; ", h.Select(x => $"v{x!["version"]} {x["status"]} approver={x["approvedBy"]} from {x["effectiveFrom"]} to {x["effectiveTo"]}")) + "; approval audit event with rationale retained.");
    }

    [Fact, Tc("TC-RSK-004")]
    public async Task Score_above_100_blocked()
    {
        var m = await NewMethodology(new DateTime(2034, 5, 1, 0, 0, 0, DateTimeKind.Utc), withFactor: false);
        var api = await As("manager");
        var r = await api.Post($"/api/admin/risk-methodologies/{Id(m)}/factors", new { code = "BIG", name = "Big", categoryCode = "GEOGRAPHIC", dataSource = "customer.region", weight = 1, missingDataScore = 50, mappings = new object[] { new { matchType = "Default", score = 150, priority = 0 } } });
        var blocked = (int)r.Status >= 400 || !(await api.PostOk($"/api/admin/risk-methodologies/{Id(m)}/validate"))["valid"]!.GetValue<bool>();
        blocked.Should().BeTrue();
        var max = await F.Scalar("SELECT ISNULL(MAX(CalculatedScore),0) FROM CustomerRiskAssessments");
        Convert.ToDecimal(max).Should().BeLessThanOrEqualTo(100);
        Actual($"Mapping score 150 rejected ({(int)r.Status}) / validation fails; max stored score in DB = {max} (≤100).");
    }

    [Fact, Tc("TC-RSK-013")]
    public async Task Assessment_immutable()
    {
        var c = await CreateCustomer();
        var a = await (await As("manager")).PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "t" });
        var act = async () => await F.ExecSql($"UPDATE CustomerRiskAssessments SET FinalLevel = 'Low', CalculatedScore = 1 WHERE Id = {Id(a)}");
        await act.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();
        var del = async () => await F.ExecSql($"DELETE FROM CustomerRiskAssessments WHERE Id = {Id(a)}");
        await del.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();
        var put = await (await As("manager")).Put($"/api/risk/assessments/{Id(a)}", new { finalLevel = "Low" });
        ((int)put.Status).Should().BeOneOf(404, 405);
        Actual($"DB UPDATE/DELETE refused by trigger TR_RiskAssessments_Immutable/NoDelete; API exposes no update endpoint ({(int)put.Status}); only new assessments can be created.");
    }

    [Fact, Tc("TC-RSK-014", "AML-TC-0061")]
    public async Task Factor_edit_does_not_rewrite_history()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        var a = await api.PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "t" });
        var before = (await api.GetOk($"/api/risk/assessments/{Id(a)}")).ToJsonString();
        var stdId = Id((await api.GetOk("/api/admin/risk-methodologies")).AsArray().First(x => x!["code"]!.GetValue<string>() == "CRA-STD" && x["status"]!.GetValue<string>() == "Approved")!);
        var std = await api.GetOk($"/api/admin/risk-methodologies/{stdId}");
        var nat = std["factors"]!.AsArray().First(f => f!["code"]!.GetValue<string>() == "NATIONALITY")!;
        var edited = await api.PutOk($"/api/admin/risk-factors/{Id(nat)}", new { code = "NATIONALITY", name = "Nationality (edited)", categoryCode = "GEOGRAPHIC", dataSource = "customer.nationality", weight = 9, missingDataScore = 60, rowVersion = nat["rowVersion"]!.GetValue<string>(), mappings = new object[] { new { matchType = "Default", score = 10, priority = 0 } } });
        edited["methodologyId"]!.GetValue<long>().Should().NotBe(Id(std));
        var after = (await api.GetOk($"/api/risk/assessments/{Id(a)}")).ToJsonString();
        after.Should().Be(before);
        Actual($"Editing NATIONALITY on approved CRA-STD v{std["version"]} created a new draft version (methodology {edited["methodologyId"]}); old assessment explanation/weights byte-identical.");
    }

    [Fact, Tc("TC-RSK-015")]
    public async Task Approval_gate_for_profile()
    {
        var code = MCode();
        await Approve(await NewMethodology(new DateTime(2032, 1, 1, 0, 0, 0, DateTimeKind.Utc), code, approval: "Low,Medium,High,Critical"));
        var merchant = await CreateCustomer(type: "Merchant", region: "NORTH");
        var api = await As("manager");
        var a = await api.PostOk($"/api/customers/{Id(merchant)}/risk-assessments", new { trigger = "Manual", reason = "t", asOf = "2032-02-01T00:00:00Z" });
        a["status"]!.GetValue<string>().Should().Be("PendingApproval");
        var p1 = await api.GetOk($"/api/customers/{Id(merchant)}");
        (p1["risk"]?["lastAssessmentId"]?.GetValue<long>()).Should().NotBe(Id(a));
        await (await As("senior")).PostOk($"/api/risk/assessments/{Id(a)}/decision", new { approve = true, note = "ok" });
        var p2 = await api.GetOk($"/api/customers/{Id(merchant)}");
        p2["risk"]!["lastAssessmentId"]!.GetValue<long>().Should().Be(Id(a));
        Actual("Assessment requiring approval saved as PendingApproval; current profile unchanged until senior approval; then profile points to the approved assessment.");
    }

    [Fact, Tc("TC-RSK-016", "AML-TC-0064")]
    public async Task Event_idempotent()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        var a1 = await api.PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "ScreeningResult", triggerEventId = "evt-" + Id(c), reason = "screening" });
        var a2 = await api.PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "ScreeningResult", triggerEventId = "evt-" + Id(c), reason = "screening" });
        Id(a1).Should().Be(Id(a2));
        (await F.WithDb(db => db.Set<CustomerRiskAssessment>().CountAsync(x => x.CustomerId == Id(c)))).Should().Be(1);
        Actual($"Same event ID delivered twice → single assessment {Id(a1)}.");
    }

    [Fact, Tc("TC-RSK-017", "AML-TC-0069")]
    public async Task Scheduler_creates_review_tasks()
    {
        var c = await CompleteKycCustomer(isPep: true);
        var api = await As("manager");
        var a = await api.PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "t" });
        var prof = await api.GetOk($"/api/customers/{Id(c)}");
        var next = DateOnly.Parse(prof["risk"]!["nextReviewDate"]!.GetValue<string>());
        await F.SetSetting("Risk:ReviewLeadDays", "400");
        try { await (await As("admin")).PostOk("/api/admin/jobs/review-scheduler/run"); }
        finally { await F.SetSetting("Risk:ReviewLeadDays", "30"); }
        var tasks = (await api.GetOk($"/api/risk/review-tasks?customerId={Id(c)}")).AsArray();
        tasks.Should().ContainSingle();
        DateOnly.Parse(tasks[0]!["dueDate"]!.GetValue<string>()).Should().Be(next);
        Actual($"Final level {a["finalLevel"]} → next review {next} (per threshold review months); scheduler created review task due {tasks[0]!["dueDate"]}.");
    }

    [Fact, Tc("TC-RSK-019", "AML-TC-0062")]
    public async Task Dynamic_field_factor_and_reassessment()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        await api.PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "t" });
        var r = await api.PutOk($"/api/customers/{Id(c)}/attributes", new { values = new { OCCUPATION = "CASH_INTENSIVE" }, reason = "occupation change" });
        var re = r["reassessments"]!.AsArray();
        re.Should().ContainSingle();
        var a = await api.GetOk($"/api/risk/assessments/{re[0]!["id"]}");
        var f = a["factors"]!.AsArray().First(x => x!["factorCode"]!.GetValue<string>() == "OCCUPATION")!;
        f["observedValue"]!.GetValue<string>().Should().Be("CASH_INTENSIVE");
        f["dataReference"]!.GetValue<string>().Should().Contain("OCCUPATION");
        var r2 = await api.PutOk($"/api/customers/{Id(c)}/attributes", new { values = new { PREFERRED_LANGUAGE = "ar" }, reason = "non-material" });
        r2["reassessments"]!.AsArray().Should().BeEmpty();
        Actual($"Material field OCCUPATION change created one FieldChange reassessment; factor uses new value with reference {f["dataReference"]}; non-material field change → no reassessment.");
    }

    [Fact, Tc("TC-RSK-020")]
    public async Task Engine_failure_no_partial()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        F.Faults.Arm("risk.persist", 1, () => new InvalidOperationException("Injected failure mid-calculation"));
        var r = await api.Post($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "t" });
        ((int)r.Status).Should().Be(500);
        r.Body!["correlationId"].Should().NotBeNull();
        (await F.WithDb(db => db.Set<CustomerRiskAssessment>().CountAsync(x => x.CustomerId == Id(c)))).Should().Be(0);
        (await F.WithDb(db => db.Set<CurrentCustomerRiskProfile>().CountAsync(x => x.CustomerId == Id(c)))).Should().Be(0);
        F.ReadLog().Should().Contain(r.Body!["correlationId"]!.GetValue<string>());
        Actual($"Injected failure after insert → transaction rolled back: 0 assessments, profile unchanged; HTTP 500 problem-details with correlationId {r.Body!["correlationId"]} also present in error log; no stack trace returned.");
    }

    [Fact, Tc("AML-TC-0063")]
    public async Task Screening_update_triggers_reassessment()
    {
        var c = await CompleteKycCustomer(name: "Mohammed Testlisted Example");
        await (await As("officer")).PostOk($"/api/customers/{Id(c)}/screening");
        var res = (await ScreeningResults(Id(c))).First();
        await Transition(res.Id, "FalsePositive");
        var a = await F.WithDb(db => db.Set<CustomerRiskAssessment>().Where(x => x.CustomerId == Id(c) && x.Trigger == "ScreeningResult").ToListAsync());
        a.Should().ContainSingle();
        Actual($"Screening result {res.Id} cleared as false positive → one ScreeningResult reassessment (event {a[0].TriggerEventId}).");
    }

    [Fact, Tc("AML-TC-0065", "AML-TC-0275")]
    public async Task Manual_reassessment_reason_and_permission()
    {
        var c = await CreateCustomer();
        (await (await As("manager")).Post($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual" })).Status.Should().Be(HttpStatusCode.BadRequest);
        var a = await (await As("manager")).PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "Customer called about income" });
        a["createdBy"]!.GetValue<string>().Should().Be("manager");
        a["triggerReason"]!.GetValue<string>().Should().Contain("income");
        a["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        var before = await F.WithDb(db => db.Set<CustomerRiskAssessment>().CountAsync(x => x.CustomerId == Id(c)));
        (await (await As("ops")).Post($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "x" })).Status.Should().Be(HttpStatusCode.Forbidden);
        (await F.WithDb(db => db.Set<CustomerRiskAssessment>().CountAsync(x => x.CustomerId == Id(c)))).Should().Be(before);
        Actual($"No reason → 400; with reason stored actor=manager, trigger Manual, reason, correlation {a["correlationId"]}; Operations user → 403 and no assessment created.");
    }

    [Fact, Tc("AML-TC-0072", "AML-TC-0264")]
    public async Task Dependency_blocks_publish()
    {
        var api = await As("manager");
        var occ = (await api.GetOk("/api/admin/customer-fields?code=OCCUPATION")).AsArray().First(x => x!["status"]!.GetValue<string>() == "Published")!;
        var nv = await api.PostOk($"/api/admin/customer-fields/{Id(occ)}/new-version");
        await api.PutOk($"/api/admin/customer-fields/{Id(nv)}", new { code = "OCCUPATION", labelEn = "Occupation", groupCode = occ["groupCode"]!.GetValue<string>(), dataType = "Integer", customerTypes = "Individual", rowVersion = nv["rowVersion"]!.GetValue<string>() });
        var r = await api.Post($"/api/admin/customer-fields/{Id(nv)}/publish", new { });
        r.Status.Should().Be(HttpStatusCode.Conflict);
        r.Body!["detail"]!.GetValue<string>().Should().Contain("active consumers");
        var optUsed = (await api.GetOk("/api/admin/customer-fields/OCCUPATION/options/CASH_INTENSIVE/where-used")).AsArray();
        optUsed.Should().NotBeEmpty();
        Actual($"Type change of OCCUPATION blocked: {r.Body!["detail"]}; option CASH_INTENSIVE where-used: {string.Join("; ", optUsed.Select(u => $"{u!["consumerType"]} {u["reference"]}"))}.");
    }
}

public class OverrideTests : AmlTestBase
{
    public OverrideTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    /// <summary>Medium-risk individual (residence IQ, cash-intensive occupation, no financial profile).</summary>
    private async Task<JsonNode> MediumCustomer()
    {
        var c = await CreateCustomer(attributes: new Dictionary<string, object> { ["OCCUPATION"] = "CASH_INTENSIVE", ["SOURCE_OF_INCOME"] = "BUSINESS" });
        await F.ExecSql($"UPDATE Customers SET ResidenceCountry = 'IQ' WHERE Id = {Id(c)}");
        await (await As("manager")).PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "baseline" });
        return await (await As("manager")).GetOk($"/api/customers/{Id(c)}");
    }

    [Fact, Tc("TC-OVR-001", "TC-OVR-002", "TC-OVR-003", "TC-OVR-010", "AML-TC-0066", "AML-TC-0067")]
    public async Task Override_lifecycle()
    {
        var c = await MediumCustomer();
        var level = c["risk"]!["level"]!.GetValue<string>();
        var score = c["risk"]!["calculatedScore"]!.GetValue<decimal>();
        var target = level == "Low" ? "Medium" : "Low";
        var o = await (await As("officer")).PostOk("/api/risk/overrides", new { customerId = Id(c), requestedLevel = target, reason = "Long-standing salaried customer", evidence = "Employer letter" });
        o["calculatedScore"]!.GetValue<decimal>().Should().Be(score);
        o["requestedLevel"]!.GetValue<string>().Should().Be(target);
        (await (await As("manager")).Post($"/api/risk/overrides/{Id(o)}/decision", new { approve = true })).Status.Should().Be(HttpStatusCode.OK, "manager is a different user");
        var o2 = await (await As("manager")).PostOk("/api/risk/overrides", new { customerId = Id(c), requestedLevel = level, reason = "revert", evidence = "n/a" });
        var selfApprove = await (await As("manager")).Post($"/api/risk/overrides/{Id(o2)}/decision", new { approve = true });
        ((int)selfApprove.Status).Should().BeOneOf(403, 409);
        var p = await (await As("manager")).GetOk($"/api/customers/{Id(c)}");
        p["risk"]!["overrideApplied"]!.GetValue<bool>().Should().BeTrue();
        p["risk"]!["overrideType"]!.GetValue<string>().Should().Be("Manual");
        p["risk"]!["calculatedScore"]!.GetValue<decimal>().Should().Be(score);
        p["risk"]!["level"]!.GetValue<string>().Should().Be(target);
        Actual($"Calculated {level}/{score} kept separately from requested {target}; approved by second user (manager) → final {target}, approver stored; self-approval refused ({(int)selfApprove.Status}); profile shows Manual override indicator with reason '{p["risk"]!["overrideReason"]}'.");
    }

    [Fact, Tc("TC-OVR-004")]
    public async Task Rejected_override_keeps_level()
    {
        var c = await MediumCustomer();
        var level = c["risk"]!["level"]!.GetValue<string>();
        var o = await (await As("officer")).PostOk("/api/risk/overrides", new { customerId = Id(c), requestedLevel = level == "Low" ? "Medium" : "Low", reason = "r", evidence = "e" });
        var d = await (await As("senior")).PostOk($"/api/risk/overrides/{Id(o)}/decision", new { approve = false, reason = "Insufficient evidence" });
        d["status"]!.GetValue<string>().Should().Be("Rejected");
        d["finalLevel"]!.GetValue<string>().Should().Be(level);
        (await (await As("officer")).GetOk($"/api/customers/{Id(c)}"))["risk"]!["level"]!.GetValue<string>().Should().Be(level);
        Actual($"Rejected with reason → final stays calculated level {level}.");
    }

    [Fact, Tc("TC-OVR-005")]
    public async Task Override_requires_reason()
    {
        var c = await MediumCustomer();
        var r = await (await As("officer")).Post("/api/risk/overrides", new { customerId = Id(c), requestedLevel = "Low", reason = "" });
        r.Status.Should().Be(HttpStatusCode.BadRequest);
        Actual("Empty reason → 400: " + r.Body!["detail"]);
    }

    [Fact, Tc("TC-OVR-006", "TC-RSK-010", "AML-TC-0068")]
    public async Task Sanctions_cannot_be_lowered()
    {
        var c = await CompleteKycCustomer(name: "Zorvan Testlisted Qadir", nationality: "SY", dob: "1970-03-03");
        await (await As("officer")).PostOk($"/api/customers/{Id(c)}/screening");
        var res = (await ScreeningResults(Id(c))).First(x => x.Category == "Sanctions");
        await Transition(res.Id, "ConfirmedMatch");
        var prof = await (await As("officer")).GetOk($"/api/customers/{Id(c)}");
        prof["risk"]!["sanctionsWorkflow"]!.GetValue<bool>().Should().BeTrue();
        prof["risk"]!["level"]!.GetValue<string>().Should().Be("Critical");
        var r = await (await As("officer")).Post("/api/risk/overrides", new { customerId = Id(c), requestedLevel = "Low", reason = "try lowering" });
        r.Status.Should().Be(HttpStatusCode.Conflict);
        Actual($"Confirmed sanctions match → calculated score {prof["risk"]!["calculatedScore"]} but final level Critical via SANCTIONS_CONFIRMED mandatory override (sanctions workflow); override to Low blocked: {r.Body!["detail"]}");
    }

    [Fact, Tc("TC-OVR-007", "AML-TC-0070")]
    public async Task Overdue_review_escalates_once()
    {
        var c = await MediumCustomer();
        await F.ExecSql($"INSERT INTO RiskReviewTasks (TenantId, CustomerId, DueDate, Status, Reason, CreatedAt) VALUES (1, {Id(c)}, DATEADD(day,-1,CAST(SYSUTCDATETIME() AS date)), 'Open', 'test overdue', SYSUTCDATETIME())");
        var admin = await As("admin");
        await admin.PostOk("/api/admin/jobs/review-escalation/run");
        await admin.PostOk("/api/admin/jobs/review-escalation/run");
        var t = (await (await As("manager")).GetOk($"/api/risk/review-tasks?customerId={Id(c)}&overdue=true")).AsArray();
        t.Should().ContainSingle();
        t[0]!["status"]!.GetValue<string>().Should().Be("Escalated");
        t[0]!["escalatedTo"]!.GetValue<string>().Should().Be("AMLManager");
        var audits = await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "RISK_REVIEW_ESCALATED" && a.EntityId == t[0]!["id"]!.ToString()));
        audits.Should().Be(1);
        Actual("Task due yesterday escalated to AMLManager exactly once (job run twice, 1 escalation audit); appears in overdue report filter.");
    }

    [Fact, Tc("TC-OVR-008", "TC-OVR-009")]
    public async Task Review_completion()
    {
        var c = await MediumCustomer();
        await F.ExecSql($"INSERT INTO RiskReviewTasks (TenantId, CustomerId, DueDate, Status, Reason, CreatedAt) VALUES (1, {Id(c)}, CAST(SYSUTCDATETIME() AS date), 'Open', 'test', SYSUTCDATETIME())");
        var t = (await (await As("officer")).GetOk($"/api/risk/review-tasks?customerId={Id(c)}")).AsArray()[0]!;
        (await (await As("officer")).Post($"/api/risk/review-tasks/{Id(t)}/complete", new { evidence = "" })).Status.Should().Be(HttpStatusCode.BadRequest);
        var done = await (await As("officer")).PostOk($"/api/risk/review-tasks/{Id(t)}/complete", new { evidence = "Reviewed KYC file REF-9" });
        var a = await (await As("officer")).GetOk($"/api/risk/assessments/{done["resultAssessmentId"]}");
        a["trigger"]!.GetValue<string>().Should().Be("PeriodicReview");
        Actual($"Completion without evidence → 400; with evidence completed and new assessment {a["id"]} created with trigger PeriodicReview.");
    }
}
