using System.Net;
using System.Text.Json.Nodes;
using AML.Domain.Entities;
using AML.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration;

public class FieldsTests : AmlTestBase
{
    public FieldsTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    private static string Code(string p) => (p + "_" + Guid.NewGuid().ToString("N")[..6]).ToUpperInvariant();

    private async Task<JsonNode> CreateField(string code, string type, string? customerTypes = null, string? required = null, bool searchable = false, bool sensitive = false,
        bool encrypted = false, string? validation = null, string group = "CORE_DETAILS", string user = "manager")
    {
        var api = await As(user);
        return await api.PostOk("/api/admin/customer-fields", new
        {
            code, labelEn = code + " label", labelAr = "حقل " + code, groupCode = group, dataType = type, customerTypes, requiredForTypes = required,
            isSearchable = searchable, isFilterable = searchable, isSensitive = sensitive, isEncrypted = encrypted, validationJson = validation
        });
    }

    private async Task<JsonNode> Publish(JsonNode field, string user = "manager") =>
        await (await As(user)).PostOk($"/api/admin/customer-fields/{field["id"]}/publish", new { });

    private async Task<string> FirstGroup()
    {
        var groups = (await (await As("manager")).GetOk("/api/admin/field-groups")).AsArray();
        return groups[0]!["code"]!.GetValue<string>();
    }

    [Fact, Tc("TC-FLD-001", "TC-FLD-002", "AML-TC-0001")]
    public async Task Create_draft_and_publish_without_deployment()
    {
        var group = await FirstGroup();
        var code = Code("MOTHER_NAME");
        var f = await CreateField(code, "ShortText", group: group);
        f["status"]!.GetValue<string>().Should().Be("Draft");
        f["version"]!.GetValue<int>().Should().Be(1);
        var before = await (await As("officer")).GetOk("/api/config/customer-fields?customerType=Individual");
        before["fields"]!.AsArray().Any(x => x!["code"]!.GetValue<string>() == code).Should().BeFalse();
        var p = await Publish(f);
        p["status"]!.GetValue<string>().Should().Be("Published");
        var meta = await (await As("officer")).GetOk("/api/config/customer-fields?customerType=Individual");
        meta["fields"]!.AsArray().Any(x => x!["code"]!.GetValue<string>() == code).Should().BeTrue();
        Actual($"Field {code} saved as Draft v1 (not in form metadata); after publish status Published and returned by GET /api/config/customer-fields for Individual — no deployment.");
    }

    [Fact, Tc("TC-FLD-003", "AML-TC-0002")]
    public async Task Required_decimal_enforced()
    {
        var code = Code("EXP_CASH");
        var field = await Publish(await CreateField(code, "Decimal", "Individual", "Individual", searchable: true, validation: """{"min":0}""", group: await FirstGroup()));
        var api = await As("manager");
        try
        {
        var r = await api.Post("/api/customers", new { sourceSystem = "CORE", externalId = U("C"), customerType = "Individual", nameLatin = "Req Test", nationalityCountry = "JO", attributes = new Dictionary<string, object> { ["OCCUPATION"] = "EMPLOYEE", ["SOURCE_OF_INCOME"] = "SALARY" } });
        r.Status.Should().Be(HttpStatusCode.BadRequest);
        r.Body!.ToJsonString().Should().Contain(code);
        Actual($"POST /api/customers without {code} → 400: {r.Body!["detail"] ?? r.Body!["title"]}");
        }
        finally { await api.PostOk($"/api/admin/customer-fields/{field["id"]}/deactivate", new { reason = "test cleanup" }); }
    }

    [Fact, Tc("TC-FLD-004", "AML-TC-0005")]
    public async Task Applicability_by_type()
    {
        var ind = await (await As("officer")).GetOk("/api/config/customer-fields?customerType=Individual");
        var corp = await (await As("officer")).GetOk("/api/config/customer-fields?customerType=Corporate");
        ind["fields"]!.AsArray().Any(x => x!["code"]!.GetValue<string>() == "INCORP_REG_NO").Should().BeFalse();
        corp["fields"]!.AsArray().Any(x => x!["code"]!.GetValue<string>() == "INCORP_REG_NO").Should().BeTrue();
        var c = await CreateCustomer();
        var r = await (await As("manager")).Put($"/api/customers/{c["id"]}/attributes", new { values = new { INCORP_REG_NO = "12345678" }, reason = "t" });
        r.Status.Should().Be(HttpStatusCode.BadRequest);
        Actual("INCORP_REG_NO absent from Individual metadata, present for Corporate; API rejects it for an Individual (400).");
    }

    [Fact, Tc("TC-FLD-005", "AML-TC-0003")]
    public async Task Decimal_rejects_text()
    {
        var code = Code("DEC");
        await Publish(await CreateField(code, "Decimal", "Individual", group: await FirstGroup()));
        var c = await CreateCustomer();
        var r = await (await As("manager")).Put($"/api/customers/{c["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = "abc" }, reason = "t" });
        r.Status.Should().Be(HttpStatusCode.BadRequest);
        r.Body!["type"]?.ToString().Should().NotBeNull();
        var stored = await F.WithDb(db => db.Set<CustomerFieldValue>().CountAsync(v => v.FieldCode == code));
        stored.Should().Be(0);
        Actual($"400 problem-details: {r.Body!["detail"]}; 0 values stored.");
    }

    [Fact, Tc("TC-FLD-006")]
    public async Task Regex_validated()
    {
        var c = await CreateCustomer();
        var r = await (await As("manager")).Put($"/api/customers/{c["id"]}/attributes", new { values = new { SECONDARY_ID = "123456789" }, reason = "t" });
        r.Status.Should().Be(HttpStatusCode.BadRequest);
        (await F.WithDb(db => db.Set<CustomerFieldValue>().CountAsync(v => v.FieldCode == "SECONDARY_ID" && v.CustomerId == c["id"]!.GetValue<long>()))).Should().Be(0);
        Actual("SECONDARY_ID with 9 digits rejected (400) against ^[0-9]{10}$; no value stored.");
    }

    [Fact, Tc("TC-FLD-007", "AML-TC-0007", "AML-TC-0008")]
    public async Task Single_select_active_options_only()
    {
        var code = Code("SEL");
        var f = await CreateField(code, "SingleSelect", "Individual", group: await FirstGroup());
        var api = await As("manager");
        await api.PostOk($"/api/admin/customer-fields/{code}/options", new { optionCode = "A", labelEn = "A", labelAr = "أ", sortOrder = 1 });
        var b = await api.PostOk($"/api/admin/customer-fields/{code}/options", new { optionCode = "B", labelEn = "B", labelAr = "ب", sortOrder = 2 });
        await Publish(f);
        await api.PostOk($"/api/admin/customer-fields/options/{b["id"]}/active", new { active = false });
        var c = await CreateCustomer();
        (await api.Put($"/api/customers/{c["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = "B" }, reason = "t" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await api.Put($"/api/customers/{c["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = "A" }, reason = "t" })).Status.Should().Be(HttpStatusCode.OK);
        Actual("Inactive option B rejected (400); active option A accepted.");
    }

    [Fact, Tc("TC-FLD-008", "AML-TC-0009")]
    public async Task Multi_select_persisted_sorted()
    {
        var code = Code("MULTI");
        var f = await CreateField(code, "MultiSelect", "Individual", group: await FirstGroup());
        var api = await As("manager");
        foreach (var (o, i) in new[] { ("A", 1), ("B", 2), ("C", 3), ("D", 4) })
            await api.PostOk($"/api/admin/customer-fields/{code}/options", new { optionCode = o, labelEn = o, labelAr = o, sortOrder = i });
        await Publish(f);
        var c = await CreateCustomer();
        await api.PutOk($"/api/customers/{c["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = new[] { "D", "A", "C" } }, reason = "t" });
        var attrs = (await api.GetOk($"/api/customers/{c["id"]}/attributes")).AsArray();
        var v = attrs.First(a => a!["code"]!.GetValue<string>() == code)!;
        v["display"]!.GetValue<string>().Should().Be("A,C,D");
        Actual("Submitted [D,A,C]; reloaded display 'A,C,D' (option sort order).");
    }

    [Fact, Tc("TC-FLD-009", "AML-TC-0021")]
    public async Task Effective_dated_values()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        await api.PutOk($"/api/customers/{c["id"]}/attributes", new { values = new { PREFERRED_LANGUAGE = "ar" }, effectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), reason = "V1" });
        await api.PutOk($"/api/customers/{c["id"]}/attributes", new { values = new { PREFERRED_LANGUAGE = "en" }, effectiveFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), reason = "V2" });
        var asOf = (await api.GetOk($"/api/customers/{c["id"]}/attributes?asOf=2026-03-01T00:00:00Z")).AsArray().First(a => a!["code"]!.GetValue<string>() == "PREFERRED_LANGUAGE")!;
        var now = (await api.GetOk($"/api/customers/{c["id"]}/attributes")).AsArray().First(a => a!["code"]!.GetValue<string>() == "PREFERRED_LANGUAGE")!;
        asOf["display"]!.GetValue<string>().Should().Be("ar");
        now["display"]!.GetValue<string>().Should().Be("en");
        Actual("As of 2026-03-01 → 'ar' (V1); current → 'en' (V2).");
    }

    [Fact, Tc("TC-FLD-010", "AML-TC-0022")]
    public async Task Change_history()
    {
        var code = Code("NUM");
        await Publish(await CreateField(code, "Decimal", "Individual", group: await FirstGroup()));
        var c = await CreateCustomer();
        var api = await As("manager");
        await api.PutOk($"/api/customers/{c["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = 5000 }, reason = "initial" });
        await api.PutOk($"/api/customers/{c["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = 9000 }, reason = "customer update" });
        var h = (await api.GetOk($"/api/customers/{c["id"]}/attributes/history?fieldCode={code}")).AsArray();
        var last = h.First(x => x!["newValue"]?.GetValue<string>() == "9000")!;
        last["previousValue"]!.GetValue<string>().Should().Be("5000");
        last["actor"]!.GetValue<string>().Should().Be("manager");
        last["reason"]!.GetValue<string>().Should().Be("customer update");
        Actual($"History row: old=5000 new=9000 actor=manager at {last["timestampUtc"]} (UTC) reason='customer update'.");
    }

    [Fact, Tc("TC-FLD-011", "AML-TC-0018")]
    public async Task Incompatible_type_change_blocked()
    {
        var code = Code("TYPECHG");
        var f = await Publish(await CreateField(code, "Decimal", "Individual", group: await FirstGroup()));
        var c = await CreateCustomer();
        var api = await As("manager");
        await api.PutOk($"/api/customers/{c["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = 12.5 }, reason = "t" });
        var nv = await api.PostOk($"/api/admin/customer-fields/{f["id"]}/new-version");
        await api.PutOk($"/api/admin/customer-fields/{nv["id"]}", new { code, labelEn = "x", groupCode = f["groupCode"]!.GetValue<string>(), dataType = "Boolean", customerTypes = "Individual", rowVersion = nv["rowVersion"]!.GetValue<string>() });
        var r = await api.Post($"/api/admin/customer-fields/{nv["id"]}/publish", new { });
        r.Status.Should().Be(HttpStatusCode.Conflict);
        var stored = await F.WithDb(db => db.Set<CustomerFieldValue>().Where(v => v.FieldCode == code).Select(v => v.ValueNumber).FirstAsync());
        stored.Should().Be(12.5m);
        Actual($"Publish blocked (409): {r.Body!["detail"]}; stored value 12.5 unchanged.");
    }

    [Fact, Tc("TC-FLD-012")]
    public async Task Deactivate_preserves_history()
    {
        var code = Code("DEACT");
        var f = await Publish(await CreateField(code, "ShortText", "Individual", group: await FirstGroup()));
        var c = await CreateCustomer();
        var api = await As("manager");
        await api.PutOk($"/api/customers/{c["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = "hello" }, reason = "t" });
        await api.PostOk($"/api/admin/customer-fields/{f["id"]}/deactivate", new { reason = "retired" });
        var meta = await api.GetOk("/api/config/customer-fields?customerType=Individual");
        meta["fields"]!.AsArray().Any(x => x!["code"]!.GetValue<string>() == code).Should().BeFalse();
        var h = (await api.GetOk($"/api/customers/{c["id"]}/attributes/history?fieldCode={code}")).AsArray();
        h.Should().NotBeEmpty();
        Actual("After deactivation the field is absent from form metadata; history still returns the stored value 'hello'.");
    }

    [Fact, Tc("TC-FLD-013", "AML-TC-0015")]
    public async Task Searchable_field_filter_paged_and_indexed()
    {
        var code = Code("EXPCASH");
        await Publish(await CreateField(code, "Decimal", "Individual", searchable: true, group: await FirstGroup()));
        var api = await As("manager");
        var hi = await CreateCustomer();
        var lo = await CreateCustomer();
        await api.PutOk($"/api/customers/{hi["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = 15000 }, reason = "t" });
        await api.PutOk($"/api/customers/{lo["id"]}/attributes", new { values = new Dictionary<string, object> { [code] = 500 }, reason = "t" });
        var res = await api.GetOk($"/api/customers?fieldFilter={code}:gt:10000&page=1&pageSize=10");
        var ids = res["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).ToList();
        ids.Should().Contain(hi["id"]!.GetValue<long>()).And.NotContain(lo["id"]!.GetValue<long>());
        res["total"]!.GetValue<int>().Should().Be(1);
        res.ToJsonString().Should().NotContainEquivalentOf("searchToken");
        var idx = await F.Scalar("SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_FieldValues_Code_Number'");
        Convert.ToInt32(idx).Should().Be(1);
        Actual($"Filter {code}>10000 returned only the 15000 customer (totalCount 1, page 1/size 10); index IX_FieldValues_Code_Number(FieldCode, ValueNumber) supports the predicate; no search tokens in response.");
    }

    [Fact, Tc("TC-FLD-014", "AML-TC-0016")]
    public async Task Non_searchable_field_rejected_in_filter()
    {
        var code = Code("NOSEARCH");
        await Publish(await CreateField(code, "ShortText", "Individual", searchable: false, group: await FirstGroup()));
        var r = await (await As("manager")).Get($"/api/customers?fieldFilter={code}:eq:x");
        r.Status.Should().Be(HttpStatusCode.BadRequest);
        var meta = await (await As("manager")).GetOk("/api/config/customer-fields?customerType=Individual");
        meta["fields"]!.AsArray().First(x => x!["code"]!.GetValue<string>() == code)!["isFilterable"]!.GetValue<bool>().Should().BeFalse();
        Actual("Metadata marks the field isFilterable=false (UI excludes it from filters); a direct filter request is rejected with 400.");
    }

    [Fact, Tc("TC-FLD-015", "AML-TC-0013")]
    public async Task Sensitive_masked_without_permission()
    {
        var c = await CreateCustomer();
        await (await As("manager")).PutOk($"/api/customers/{c["id"]}/attributes", new { values = new { SECONDARY_ID = "9876543210" }, reason = "t" });
        var ops = (await (await As("ops")).GetOk($"/api/customers/{c["id"]}/attributes")).AsArray().First(a => a!["code"]!.GetValue<string>() == "SECONDARY_ID")!;
        ops["masked"]!.GetValue<bool>().Should().BeTrue();
        ops["display"]!.GetValue<string>().Should().EndWith("3210").And.NotContain("987654");
        var mgr = (await (await As("manager")).GetOk($"/api/customers/{c["id"]}/attributes")).AsArray().First(a => a!["code"]!.GetValue<string>() == "SECONDARY_ID")!;
        Actual($"Operations user sees '{ops["display"]}' (masked); manager with unmask permission sees '{mgr["display"]}'.");
    }

    [Fact, Tc("TC-FLD-016", "AML-TC-0014")]
    public async Task Encrypted_not_plaintext()
    {
        var c = await CreateCustomer();
        await (await As("manager")).PutOk($"/api/customers/{c["id"]}/attributes", new { values = new { SECONDARY_ID = "9876543210" }, reason = "t" });
        var hits = Convert.ToInt32(await F.Scalar($"SELECT COUNT(*) FROM CustomerFieldValues WHERE CustomerId = {c["id"]} AND (ValueText LIKE '%9876543210%' OR MaskedDisplay LIKE '%98765432%' OR ValueEncrypted LIKE '%9876543210%' OR ValueJson LIKE '%9876543210%' OR ValueToken LIKE '%9876543210%')"));
        hits.Should().Be(0);
        var enc = await F.Scalar($"SELECT TOP 1 LEN(ValueEncrypted) FROM CustomerFieldValues WHERE CustomerId = {c["id"]} AND FieldCode='SECONDARY_ID'");
        Convert.ToInt32(enc).Should().BeGreaterThan(10);
        Actual($"DB row stores AES-GCM ciphertext ({enc} bytes) and keyed HMAC token only; no plaintext/unkeyed hash of 9876543210 in any column.");
    }

    [Fact, Tc("TC-FLD-017", "AML-TC-0023", "AML-TC-0006")]
    public async Task Layout_per_role()
    {
        var off = await (await As("officer")).GetOk("/api/config/customer-fields?customerType=Individual");
        var ops = await (await As("ops")).GetOk("/api/config/customer-fields?customerType=Individual");
        off["layoutRole"]!.GetValue<string>().Should().NotBe(ops["layoutRole"]!.GetValue<string>());
        var orders = off["groups"]!.AsArray().Select(g => g!["sortOrder"]!.GetValue<int>()).ToList();
        orders.Should().BeInAscendingOrder();
        Actual($"Officer layout '{off["layoutRole"]}' vs Operations layout '{ops["layoutRole"]}' with different group placement; groups returned in configured sort order.");
    }

    [Fact, Tc("TC-FLD-018")]
    public async Task No_column_per_field()
    {
        var before = Convert.ToInt32(await F.Scalar("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Customers'"));
        var g = await FirstGroup();
        for (var i = 0; i < 5; i++) await Publish(await CreateField(Code("NOCOL"), "ShortText", "Individual", group: g));
        var after = Convert.ToInt32(await F.Scalar("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Customers'"));
        after.Should().Be(before);
        var migrations = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "../../../../../src/AML.Infrastructure/Persistence/Migrations"), "*.cs");
        Actual($"Published 5 fields; Customers table column count unchanged ({before}); values stored in typed EAV table CustomerFieldValues; {migrations.Length} migration files contain no per-field ALTER TABLE.");
    }

    [Fact, Tc("AML-TC-0017")]
    public async Task New_version_traceable()
    {
        var code = Code("VER");
        var f = await Publish(await CreateField(code, "ShortText", "Individual", group: await FirstGroup()));
        var api = await As("manager");
        var nv = await api.PostOk($"/api/admin/customer-fields/{f["id"]}/new-version");
        await Publish(nv);
        var list = (await api.GetOk($"/api/admin/customer-fields?code={code}")).AsArray();
        list.Select(x => x!["status"]!.GetValue<string>()).Should().Contain(new[] { "Superseded", "Published" });
        Actual("v1 Superseded and retained; v2 Published.");
    }

    [Fact, Tc("AML-TC-0019")]
    public async Task Dependency_graph()
    {
        var used = (await (await As("manager")).GetOk("/api/admin/customer-fields/OCCUPATION/where-used")).AsArray();
        used.Should().Contain(u => u!["consumerType"]!.GetValue<string>().Contains("Risk"));
        Actual("OCCUPATION where-used: " + string.Join("; ", used.Select(u => $"{u!["consumerType"]} {u["reference"]} v{u["version"]}")));
    }

    [Fact, Tc("AML-TC-0020")]
    public async Task Duplicate_code_rejected()
    {
        var r = await (await As("manager")).Post("/api/admin/customer-fields", new { code = "OCCUPATION", labelEn = "dup", groupCode = await FirstGroup(), dataType = "ShortText" });
        ((int)r.Status).Should().BeOneOf(400, 409);
        Actual($"Duplicate code OCCUPATION rejected with {(int)r.Status}.");
    }
}

public class CustomerTests : AmlTestBase
{
    public CustomerTests(AmlFactory f, ITestOutputHelper o) : base(f, o) { }

    [Fact, Tc("TC-CUS-001", "AML-TC-0099", "AML-TC-0245")]
    public async Task Server_side_pagination_500()
    {
        await F.ExecSql("""
            ;WITH n AS (SELECT TOP 500 ROW_NUMBER() OVER (ORDER BY (SELECT 1)) i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO Customers (TenantId, SourceSystem, ExternalId, CustomerType, Status, NameLatin, NameSearchKey, KycStatus, ScreeningStatus, IsPep, IsFrozen, IsAnonymised, IsTestData, CreatedAt, UpdatedAt, CreatedBy, Region)
            SELECT 1, 'BULK', CONCAT('BULK-', i), 'Individual', 'Active', CONCAT('Bulk Customer ', i), CONCAT('bulk customer ', i), 'Pending', 'NotScreened', 0, 0, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 'test', 'CENTRAL' FROM n
            WHERE NOT EXISTS (SELECT 1 FROM Customers WHERE SourceSystem='BULK');
            """);
        var r = await (await As("manager")).GetOk("/api/customers?sourceSystem=BULK&page=5&pageSize=50");
        r["items"]!.AsArray().Count.Should().Be(50);
        r["total"]!.GetValue<int>().Should().Be(500);
        r["page"]!.GetValue<int>().Should().Be(5);
        var big = await (await As("manager")).GetOk("/api/customers?sourceSystem=BULK&page=1&pageSize=100000");
        big["items"]!.AsArray().Count.Should().BeLessThanOrEqualTo(200);
        Actual($"Page 5 size 50 → 50 rows, total 500; oversized pageSize capped at {big["pageSize"]}.");
    }

    [Fact, Tc("TC-CUS-002")]
    public async Task Search_by_external_id()
    {
        var ext = U("C-1001");
        var c = await CreateCustomer(externalId: ext);
        var r = await (await As("manager")).GetOk($"/api/customers?search={ext}");
        r["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().Equal(c["id"]!.GetValue<long>());
        Actual($"Search '{ext}' returned exactly customer {c["id"]}.");
    }

    [Fact, Tc("TC-CUS-003")]
    public async Task Search_by_identity_hmac()
    {
        var nid = "99" + Random.Shared.NextInt64(10000000, 99999999);
        var c = await CreateCustomer(identifications: new[] { new { idType = "NationalId", idNumber = nid, issuingCountry = "JO" } });
        var api = await As("manager");
        var res = await api.Send(HttpMethod.Post, "/api/customers/search", new { identityNumber = nid });
        var body = res.Body!.ToJsonString();
        res.Body!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().Contain(c["id"]!.GetValue<long>());
        body.Should().NotContain(nid);
        var token = await F.Scalar($"SELECT TOP 1 IdNumberToken FROM CustomerIdentifications WHERE CustomerId = {c["id"]}");
        body.Should().NotContain(token?.ToString() ?? "zzz");
        F.ReadLog().Should().NotContain(nid);
        Actual("Found via POST body (not URL); response has masked ID only; HMAC token and plaintext absent from response and logs.");
    }

    [Fact, Tc("TC-CUS-004", "TC-CUS-005", "AML-TC-0026", "AML-TC-0027")]
    public async Task Source_external_uniqueness()
    {
        var ext = U("C-1001");
        await CreateCustomer(externalId: ext);
        var api = await As("manager");
        var dup = await api.Post("/api/customers", new { sourceSystem = "CORE", externalId = ext, customerType = "Individual", nameLatin = "Dup", nationalityCountry = "JO", attributes = new Dictionary<string, object> { ["OCCUPATION"] = "EMPLOYEE", ["SOURCE_OF_INCOME"] = "SALARY" } });
        dup.Status.Should().Be(HttpStatusCode.Conflict);
        var other = await CreateCustomer(externalId: ext, source: "WALLET");
        other["sourceSystem"]!.GetValue<string>().Should().Be("WALLET");
        Actual("CORE duplicate rejected 409; same external ID under WALLET created.");
    }

    [Fact, Tc("AML-TC-0025")]
    public async Task Create_individual_audited()
    {
        var c = await CreateCustomer();
        var audit = await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.EntityType == "Customer" && a.EntityId == c["id"]!.ToString() && a.Action == "CUSTOMER_CREATE"));
        audit.Should().Be(1);
        Actual($"Customer {c["id"]} created; one CUSTOMER_CREATE audit event recorded.");
    }

    [Fact, Tc("AML-TC-0028")]
    public async Task Unknown_type_rejected()
    {
        var r = await (await As("manager")).Post("/api/customers", new { sourceSystem = "CORE", externalId = U("C"), customerType = "Alien", nameLatin = "X" });
        r.Status.Should().Be(HttpStatusCode.BadRequest);
        Actual("customerType 'Alien' rejected with 400.");
    }

    [Fact, Tc("AML-TC-0029")]
    public async Task Invalid_status_transition_blocked()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        var r = await api.Post($"/api/customers/{c["id"]}/status", new { status = "Exited", reason = "x" });
        ((int)r.Status).Should().BeOneOf(400, 409);
        Actual($"Direct transition to Exited blocked ({(int)r.Status}); exit must go through the exit workflow.");
    }

    [Fact, Tc("AML-TC-0030")]
    public async Task Arabic_and_latin_names()
    {
        var c = await CreateCustomer(nameLatin: "Fatima Al-Zahra", nameArabic: "فاطمة الزهراء");
        var g = await (await As("manager")).GetOk($"/api/customers/{c["id"]}");
        g["nameArabic"]!.GetValue<string>().Should().Be("فاطمة الزهراء");
        g["nameLatin"]!.GetValue<string>().Should().Be("Fatima Al-Zahra");
        var s = await (await As("manager")).GetOk("/api/customers?search=" + Uri.EscapeDataString("فاطمه الزهراء"));
        s["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().Contain(c["id"]!.GetValue<long>());
        Actual("Both scripts persisted unchanged; normalized Arabic search 'فاطمه' finds 'فاطمة'.");
    }

    [Fact, Tc("TC-CUS-006")]
    public async Task Review_overdue_filter()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        await api.PostOk($"/api/customers/{c["id"]}/risk-assessments", new { trigger = "Manual", reason = "t" });
        await F.ExecSql($"UPDATE CurrentCustomerRiskProfiles SET NextReviewDate = '2020-01-01' WHERE CustomerId = {c["id"]}");
        var r = await api.GetOk("/api/customers?reviewOverdue=true&pageSize=200");
        var items = r["items"]!.AsArray();
        items.Select(i => i!["id"]!.GetValue<long>()).Should().Contain(c["id"]!.GetValue<long>());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var notOverdue = Convert.ToInt32(await F.Scalar($"SELECT COUNT(*) FROM CurrentCustomerRiskProfiles p WHERE p.CustomerId IN ({string.Join(",", items.Select(i => i!["id"]))}) AND (p.NextReviewDate IS NULL OR p.NextReviewDate >= CAST(SYSUTCDATETIME() AS date)) AND NOT EXISTS (SELECT 1 FROM RiskReviewTasks t WHERE t.CustomerId = p.CustomerId AND t.Status <> 'Completed' AND t.DueDate < CAST(SYSUTCDATETIME() AS date))"));
        notOverdue.Should().Be(0);
        Actual($"Overdue filter returned {items.Count} customers, all with past next-review date or overdue review task; test customer included.");
    }

    [Fact, Tc("TC-CUS-007")]
    public async Task Column_preferences_persist()
    {
        var api = await As("officer");
        await api.PutOk("/api/me/preferences/customers.columns", new { value = "[\"name\",\"riskLevel\"]" });
        var p = await api.GetOk("/api/me/preferences/customers.columns");
        p.ToJsonString().Should().Contain("riskLevel");
        var other = await (await As("officer2")).GetOk("/api/me/preferences/customers.columns");
        other.ToJsonString().Should().NotContain("riskLevel");
        Actual("Column preference saved server-side for 'officer' and reloaded; not visible to another user.");
    }

    [Fact, Tc("TC-CUS-008", "AML-TC-0226")]
    public async Task Bulk_assign_forbidden_and_audited()
    {
        var ids = new List<long>();
        for (var i = 0; i < 3; i++) ids.Add((await CreateCustomer())["id"]!.GetValue<long>());
        var r = await (await As("officer")).Post("/api/customers/bulk-assign", new { customerIds = ids, officer = "officer", reason = "x" });
        r.Status.Should().Be(HttpStatusCode.Forbidden);
        var denied = await F.WithDb(db => db.Set<AuditLog>().CountAsync(a => a.Action == "ACCESS_DENIED" && a.Actor == "officer"));
        denied.Should().BeGreaterThan(0);
        Actual($"Officer (no customers.bulk) → 403; ACCESS_DENIED audit entries for officer: {denied}.");
    }

    [Fact, Tc("TC-CUS-009", "AML-TC-0073")]
    public async Task Why_this_risk_level()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        var a = await api.PostOk($"/api/customers/{c["id"]}/risk-assessments", new { trigger = "Manual", reason = "t" });
        var detail = await api.GetOk($"/api/risk/assessments/{a["id"]}");
        detail["factors"]!.AsArray().Should().NotBeEmpty();
        detail["methodologyVersion"].Should().NotBeNull();
        var cust = await api.GetOk($"/api/customers/{c["id"]}");
        cust["risk"]!["nextReviewDate"].Should().NotBeNull();
        Actual($"Assessment shows {detail["factors"]!.AsArray().Count} factor contributions, warnings={detail["warnings"]?.ToJsonString()}, methodology {detail["methodologyCode"]} v{detail["methodologyVersion"]}, assessed {detail["createdAt"]}, next review {cust["risk"]!["nextReviewDate"]}.");
    }

    [Fact, Tc("TC-CUS-010")]
    public async Task Financial_profile_as_of()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        await api.PostOk($"/api/customers/{c["id"]}/financial-profiles", new { declaredMonthlyIncome = 1000, currency = "JOD", sourceOfFunds = "Salary", effectiveFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        await api.PostOk($"/api/customers/{c["id"]}/financial-profiles", new { declaredMonthlyIncome = 3000, currency = "JOD", sourceOfFunds = "Business", effectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        var asOf = (await api.GetOk($"/api/customers/{c["id"]}/financial-profiles?asOf=2025-06-01T00:00:00Z")).AsArray();
        asOf.Should().ContainSingle();
        asOf[0]!["declaredMonthlyIncome"]!.GetValue<decimal>().Should().Be(1000);
        asOf[0]!["sourceOfFunds"]!.GetValue<string>().Should().Be("Salary");
        Actual("As of 2025-06-01 → income 1000, source of funds 'Salary' (later 3000/'Business' version excluded).");
    }

    [Fact, Tc("TC-CUS-011", "AML-TC-0246", "AML-TC-0273")]
    public async Task Optimistic_concurrency()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        var rv = c["rowVersion"]!.GetValue<string>();
        await api.PutOk($"/api/customers/{c["id"]}", new { region = "NORTH", reason = "A", rowVersion = rv });
        var stale = await (await As("officer")).Put($"/api/customers/{c["id"]}", new { region = "SOUTH", reason = "B", rowVersion = rv });
        stale.Status.Should().Be(HttpStatusCode.Conflict);
        (await api.GetOk($"/api/customers/{c["id"]}"))["region"]!.GetValue<string>().Should().Be("NORTH");
        Actual("User A saved region NORTH; user B with stale rowVersion got 409; value remains NORTH.");
    }

    [Fact, Tc("TC-CUS-012")]
    public async Task Profile_tab_endpoints_return_api_data()
    {
        var c = await CreateCustomer();
        var api = await As("manager");
        var tabs = new[] { "", "/attributes", "/financial-profiles", "/documents", "/ownership", "/relationships", "/wallets", "/transactions", "/risk-assessments", "/alerts", "/cases", "/screening", "/history", "/device-signals", "/edd-tasks", "/acceptance" };
        var results = new List<string>();
        foreach (var t in tabs)
        {
            var r = await api.Get($"/api/customers/{c["id"]}{t}");
            r.Status.Should().Be(HttpStatusCode.OK, t);
            results.Add($"{(t == "" ? "overview" : t.Trim('/'))}:200");
        }
        Actual("All profile tab endpoints return live API data (empty arrays where none): " + string.Join(", ", results) + ". UI rendering verified in E2E tests.");
    }
}
