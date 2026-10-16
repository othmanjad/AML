using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AML.Domain.Entities;
using AML.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration;

/// <summary>Own database: the test publishes an Individual methodology effective now, which would change other suites' assessments.</summary>
public sealed class LookupFactory : AmlFactory { }

[CollectionDefinition("lookups")]
public sealed class LookupCollection : ICollectionFixture<LookupFactory> { }

/// <summary>
/// Lookup management: the wallet sends its own codes, AML translates them (code mappings), unmapped codes wait in a queue and
/// are applied when a mapping is added; each AML code carries an approved risk score used by LookupScore factors, so the
/// customer's level (Low / Medium / High) follows the configuration dynamically.
/// </summary>
[Collection("lookups")]
public class LookupManagementTests : AmlTestBase
{
    public LookupManagementTests(LookupFactory f, ITestOutputHelper o) : base(f, o) { }

    private static string U8() => Guid.NewGuid().ToString("N")[..8];

    private async Task<JsonNode> Customer(string type, string occupation, string nationality) =>
        await (await As("manager")).PostOk("/api/customers", new
        {
            sourceSystem = "WALLET", externalId = "LK-" + U8(), customerType = type, nameLatin = "Lookup Test " + U8(), nationalityCountry = nationality,
            residenceCountry = "JO", region = "CENTRAL", attributes = new Dictionary<string, object> { ["OCCUPATION"] = occupation, ["SOURCE_OF_INCOME"] = "SALARY" }
        });

    private async Task<JsonNode> Assess(JsonNode c) =>
        await (await As("manager")).PostOk($"/api/customers/{Id(c)}/risk-assessments", new { trigger = "Manual", reason = "lookup test" });

    private async Task<string?> Level(JsonNode c) =>
        await F.WithDb(db => db.Set<CurrentCustomerRiskProfile>().Where(p => p.CustomerId == Id(c)).Select(p => p.Level).FirstOrDefaultAsync());

    private async Task Score(string category, string code, decimal score)
    {
        var s = await (await As("manager")).PostOk("/api/admin/lookup-management/scores", new { category, code, score, rationale = "test", effectiveFrom = DateTime.UtcNow.AddMinutes(-5) });
        await (await As("senior")).PostOk($"/api/admin/lookup-management/scores/{Id(s)}/approve", new { comment = "ok" });
    }

    [Fact]
    public async Task Wallet_codes_are_mapped_and_lookup_scores_drive_the_risk_level()
    {
        var admin = await As("admin");
        foreach (var (cat, ext, code) in new[] { ("CUSTOMER_TYPE", "IND", "Individual"), ("COUNTRY", "JOR", "JO"), ("COUNTRY", "SYR", "SY"), ("OCCUPATION", "OCC-017", "STUDENT"), ("OCCUPATION", "OCC-020", "CASH_INTENSIVE") })
            await admin.PostOk("/api/admin/lookup-management/mappings", new { sourceSystem = "WALLET", category = cat, externalCode = ext, code });
        var badCode = await admin.Post("/api/admin/lookup-management/mappings", new { sourceSystem = "WALLET", category = "OCCUPATION", externalCode = "OCC-X", code = "ASTRONAUT" });
        var dup = await admin.Post("/api/admin/lookup-management/mappings", new { sourceSystem = "WALLET", category = "COUNTRY", externalCode = "JOR", code = "JO" });
        ((int)badCode.Status).Should().Be(400, "the target must be an AML code of the category");
        ((int)dup.Status).Should().Be(409);

        // Methodology: occupation (weight 2) and nationality (weight 1), both scored from the central lookup scores.
        var mgr = await As("manager");
        var m = await mgr.PostOk("/api/admin/risk-methodologies", new
        {
            code = "LK_" + U8().ToUpperInvariant(), name = "Lookup-scored methodology", aggregationMethod = "WeightedAverage", scorePrecision = 2,
            appliesToCustomerTypes = "Individual", approvalRequiredLevels = "", effectiveFrom = DateTime.UtcNow.AddMinutes(-1), rationale = "lookup scoring"
        });
        await mgr.PostOk($"/api/admin/risk-methodologies/{Id(m)}/factors", new
        {
            code = "OCCUPATION", name = "Occupation", categoryCode = "CUSTOMER_PROFILE", dataSource = "field:OCCUPATION", factorType = "Categorical", weight = 2,
            missingDataHandling = "AssignScore", missingDataScore = 50, scoringMode = "LookupScore", lookupCategory = "OCCUPATION"
        });
        await mgr.PostOk($"/api/admin/risk-methodologies/{Id(m)}/factors", new
        {
            code = "NATIONALITY", name = "Nationality", categoryCode = "GEOGRAPHIC", dataSource = "customer.nationality", factorType = "Categorical", weight = 1,
            missingDataHandling = "AssignScore", missingDataScore = 50, scoringMode = "LookupScore", lookupCategory = "COUNTRY"
        });
        await mgr.PutOk($"/api/admin/risk-methodologies/{Id(m)}/thresholds", new object[]
        {
            new { level = "Low", minScore = 0, maxScore = 39.99, reviewMonths = 36 }, new { level = "Medium", minScore = 40, maxScore = 69.99, reviewMonths = 12 },
            new { level = "High", minScore = 70, maxScore = 100, reviewMonths = 6 }
        });
        await mgr.PostOk($"/api/admin/risk-methodologies/{Id(m)}/submit", new { rationale = "test" });
        await (await As("senior")).PostOk($"/api/admin/risk-methodologies/{Id(m)}/approve");

        foreach (var (cat, code, s) in new[] { ("OCCUPATION", "STUDENT", 10m), ("OCCUPATION", "CASH_INTENSIVE", 90m), ("COUNTRY", "JO", 10m), ("COUNTRY", "SY", 95m) })
            await Score(cat, code, s);
        var self = await mgr.PostOk("/api/admin/lookup-management/scores", new { category = "COUNTRY", code = "EG", score = 40, rationale = "test" });
        ((int)(await mgr.Post($"/api/admin/lookup-management/scores/{Id(self)}/approve", new { })).Status).Should().Be(403, "a score cannot be approved by its proposer");

        // Customers sent with the wallet's own codes.
        var a = await Customer("IND", "OCC-017", "JOR");
        var b = await Customer("IND", "OCC-020", "SYR");
        var c = await Customer("IND", "OCC-999", "JOR");
        var attrsA = await mgr.GetOk($"/api/customers/{Id(a)}/attributes");
        var occA = attrsA.AsArray().First(x => x!["code"]!.GetValue<string>() == "OCCUPATION")!["value"]!.GetValue<string>();
        a["customerType"]!.GetValue<string>().Should().Be("Individual");
        a["nationalityCountry"]!.GetValue<string>().Should().Be("JO");
        occA.Should().Be("STUDENT");

        await Assess(a); await Assess(b); var ac = await Assess(c);
        (await Level(a)).Should().Be("Low", "STUDENT 10 ×2 + JO 10 → 10");
        (await Level(b)).Should().Be("High", "CASH_INTENSIVE 90 ×2 + SY 95 → 91.67");
        (await Level(c)).Should().Be("Low", "unmapped occupation is missing (50) ×2 + JO 10 → 36.67");
        ac["factors"]!.AsArray().First(x => x!["factorCode"]!.GetValue<string>() == "OCCUPATION")!["dataStatus"]!.GetValue<string>().Should().Be("Missing");

        // The unmapped wallet code waits in the queue; adding a mapping applies it and re-assesses the customer.
        var unmapped = await admin.GetOk("/api/admin/lookup-management/unmapped");
        unmapped.AsArray().Should().Contain(x => x!["externalCode"]!.GetValue<string>() == "OCC-999" && x["category"]!.GetValue<string>() == "OCCUPATION");
        (await F.WithDb(db => db.Set<DataQualityIssue>().AnyAsync(d => d.IssueType == "UnmappedCode" && d.EntityId == Id(c).ToString()))).Should().BeTrue();
        var created = await admin.PostOk("/api/admin/lookup-management/mappings", new { sourceSystem = "WALLET", category = "OCCUPATION", externalCode = "OCC-999", code = "CASH_INTENSIVE" });
        created["remap"]!["valuesApplied"]!.GetValue<int>().Should().Be(1);
        created["remap"]!["customersReassessed"]!.GetValue<int>().Should().Be(1);
        (await Level(c)).Should().Be("Medium", "CASH_INTENSIVE 90 ×2 + JO 10 → 63.33 after the mapping is added");
        (await admin.GetOk("/api/admin/lookup-management/unmapped")).AsArray().Should().NotContain(x => x!["externalCode"]!.GetValue<string>() == "OCC-999");

        // Changing a code's score re-assesses the customers holding it.
        var s2 = await mgr.PostOk("/api/admin/lookup-management/scores", new { category = "OCCUPATION", code = "STUDENT", score = 80, rationale = "student mule typology", effectiveFrom = DateTime.UtcNow.AddMinutes(-1) });
        var decided = await (await As("senior")).PostOk($"/api/admin/lookup-management/scores/{Id(s2)}/approve", new { comment = "ok" });
        decided["reassessed"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(1);
        (await Level(a)).Should().Be("Medium", "STUDENT 80 ×2 + JO 10 → 56.67 after the new score");
        var history = await F.WithDb(db => db.Set<LookupRiskScore>().Where(x => x.Category == "OCCUPATION" && x.Code == "STUDENT").OrderBy(x => x.Id).Select(x => x.Status).ToListAsync());
        history.Should().Equal("Superseded", "Approved");

        // Transactions: wallet transaction-type codes are translated too.
        await admin.PostOk("/api/admin/lookup-management/mappings", new { sourceSystem = "WALLET", category = "TXN_TYPE", externalCode = "W2W", code = "P2P" });
        var client = await F.ClientAsync("jo-demo-wallet");
        var txId = "LK-T-" + U8();
        await client.PostOk("/api/integrations/WALLET/transactions", new { transactions = new[] { new { sourceTransactionId = txId, type = "W2W", status = "SUCC", eventTime = DateTime.UtcNow, amount = 10, currency = "JOD", senderWallet = "W-LK-1", receiverWallet = "W-LK-2" } } });
        (await F.WithDb(db => db.Set<Transaction>().Where(t => t.SourceTransactionId == txId).Select(t => t.TransactionType).FirstAsync())).Should().Be("P2P");

        // CSV import updates and creates mappings in bulk.
        var csv = "sourceSystem,category,externalCode,code,externalLabel\nWALLET,COUNTRY,EGY,EG,Egypt\nWALLET,COUNTRY,JOR,JO,Jordan\nWALLET,COUNTRY,XXX,ZZ,Bad\n";
        var content = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "file", "map.csv" } };
        var imp = await admin.Send(HttpMethod.Post, "/api/admin/lookup-management/mappings/import", content);
        ((int)imp.Status).Should().Be(200, imp.Body?.ToJsonString());
        imp.Body!["created"]!.GetValue<int>().Should().Be(1);
        imp.Body!["updated"]!.GetValue<int>().Should().Be(1);
        imp.Body!["rejected"]!.GetValue<int>().Should().Be(1);

        // Without mappings for a category, the platform keeps strict validation (unknown country is rejected).
        var strict = await mgr.Post("/api/customers", new { sourceSystem = "CORE", externalId = "LK-S-" + U8(), customerType = "Individual", nameLatin = "Strict", nationalityCountry = "QQ" });
        ((int)strict.Status).Should().Be(400);
    }
}
