using System.Text.Json;
using AML.Application.Cases;
using AML.Application.Customers;
using AML.Application.Reporting;
using AML.Application.Risk;
using AML.Application.Rules;
using AML.Application.Transactions;
using AML.Domain.Entities;
using AML.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AML.Infrastructure.Persistence;

/// <summary>Fictitious demonstration scenario (all records flagged as test data). Never real personal or financial data.</summary>
public static class DemoDataSeeder
{
    public static async Task SeedAsync(IServiceProvider root, long tenantId, CancellationToken ct = default)
    {
        using var scope = root.CreateScope();
        var sp = scope.ServiceProvider;
        JobExecutor.SetSystem(sp, tenantId);
        var user = sp.GetRequiredService<CurrentUser>();
        user.Username = "seed";
        user.IsSystem = false; // run as a scoped tenant user with full permissions so tenant filters apply
        user.PermissionSet = Application.Common.Perm.All().ToHashSet();
        user.RoleSet = new HashSet<string> { Application.Common.RoleCodes.AmlManager };
        var db = sp.GetRequiredService<AmlDbContext>();
        if (await db.Set<Customer>().AnyAsync(c => c.IsTestData && c.ExternalId == "DEMO-0001", ct)) return;

        var customers = sp.GetRequiredService<CustomerService>();
        JsonElement J(object v) => JsonSerializer.SerializeToElement(v);
        var demo = await customers.CreateAsync(new CustomerCreateRequest
        {
            SourceSystem = "CORE", ExternalId = "DEMO-0001", CustomerType = "Individual", NameLatin = "Omar Testcustomer Demo", NameArabic = "عمر تيست ديمو",
            DateOfBirth = new DateOnly(1988, 4, 12), NationalityCountry = "JO", ResidenceCountry = "JO", Region = "CENTRAL", AssignedOfficer = "officer",
            Attributes = new() { ["OCCUPATION"] = J("EMPLOYEE"), ["SOURCE_OF_INCOME"] = J("SALARY"), ["PRODUCT_TIER"] = J("STANDARD"), ["PREFERRED_LANGUAGE"] = J("ar") },
            Identifications = new() { new IdentificationInput { IdType = "NationalId", IdNumber = "9990000001", IssuingCountry = "JO", ExpiryDate = new DateOnly(2030, 1, 1) } },
            Contacts = new() { new ContactInput { ContactType = "Phone", Value = "+962790000001" }, new ContactInput { ContactType = "Email", Value = "omar.demo@example.test" } },
            FinancialProfile = new FinancialProfileInput { DeclaredMonthlyIncome = 1200, Currency = "JOD", SourceOfFunds = "Salary", Occupation = "Employee", ExpectedMonthlyVolume = 1500, ExpectedMonthlyCount = 20 }
        }, ct);
        var counterparty = await customers.CreateAsync(new CustomerCreateRequest
        {
            SourceSystem = "CORE", ExternalId = "DEMO-0002", CustomerType = "Individual", NameLatin = "Mohammed Abdullah", NameArabic = "محمد عبدالله", DateOfBirth = new DateOnly(1990, 1, 1),
            NationalityCountry = "JO", ResidenceCountry = "JO", Region = "NORTH", Attributes = new() { ["OCCUPATION"] = J("SELF_EMPLOYED"), ["SOURCE_OF_INCOME"] = J("BUSINESS") }
        }, ct);
        await customers.CreateAsync(new CustomerCreateRequest { SourceSystem = "CORE", ExternalId = "DEMO-0003", CustomerType = "Individual", NameLatin = "Fatima Alzahraa Example", NameArabic = "فاطمة الزهراء", NationalityCountry = "JO", ResidenceCountry = "JO", Region = "CENTRAL", Attributes = new() { ["OCCUPATION"] = J("STUDENT"), ["SOURCE_OF_INCOME"] = J("REMITTANCES") } }, ct);
        await customers.CreateAsync(new CustomerCreateRequest { SourceSystem = "CORE", ExternalId = "DEMO-0004", CustomerType = "Individual", NameLatin = "Ahmad Mustafa Sample", NameArabic = "أحمد مصطفى", NationalityCountry = "JO", ResidenceCountry = "JO", Region = "SOUTH", Attributes = new() { ["OCCUPATION"] = J("EMPLOYEE"), ["SOURCE_OF_INCOME"] = J("SALARY") } }, ct);

        var corp = await customers.CreateAsync(new CustomerCreateRequest { SourceSystem = "CORE", ExternalId = "DEMO-CORP-01", CustomerType = "Corporate", NameLatin = "Demo Trading LLC (fictitious)", NameArabic = "شركة ديمو للتجارة", IncorporationCountry = "JO", Region = "CENTRAL", Attributes = new() { ["INCORP_REG_NO"] = J("123456789"), ["BUSINESS_ACTIVITY"] = J("General trading") } }, ct);
        var holding = await customers.CreateAsync(new CustomerCreateRequest { SourceSystem = "CORE", ExternalId = "DEMO-CORP-02", CustomerType = "Corporate", NameLatin = "Demo Holding Co (fictitious)", IncorporationCountry = "AE", Region = "CENTRAL", Attributes = new() { ["INCORP_REG_NO"] = J("987654321") } }, ct);
        await customers.AddOwnershipAsync(corp.Id, new OwnershipInput { OwnerCustomerId = holding.Id, OwnerType = "Entity", Percentage = 60, ControlType = "Ownership", Evidence = "Demo register extract (fictitious)" }, ct);
        await customers.AddOwnershipAsync(holding.Id, new OwnershipInput { OwnerName = "Laila Testowner", OwnerType = "Person", Percentage = 50, ControlType = "Ownership", OwnerNationality = "JO", OwnerDateOfBirth = new DateOnly(1975, 6, 1) }, ct);
        await customers.AddOwnershipAsync(corp.Id, new OwnershipInput { OwnerName = "Sami Testpartner", OwnerType = "Person", Percentage = 15, ControlType = "Ownership", OwnerNationality = "JO" }, ct);
        await customers.AddOwnershipAsync(corp.Id, new OwnershipInput { OwnerName = "Rana Testsignatory", OwnerType = "Person", ControlType = "Signatory" }, ct);

        var wallets = sp.GetRequiredService<WalletService>();
        await wallets.CreateAsync(new WalletInput { SourceSystem = "CORE", ExternalWalletId = "W-DEMO-1", CustomerId = demo.Id, WalletType = "Personal" }, ct);
        await wallets.CreateAsync(new WalletInput { SourceSystem = "CORE", ExternalWalletId = "W-DEMO-CLR", CustomerId = demo.Id, WalletType = "Internal" }, ct);
        await wallets.CreateAsync(new WalletInput { SourceSystem = "CORE", ExternalWalletId = "W-DEMO-2", CustomerId = counterparty.Id, WalletType = "Personal" }, ct);
        await wallets.CreateMappingAsync(new WalletMappingInput { SourceSystem = "CORE", ExternalWalletId = "W-DEMO-CLR", MappingCategory = "Clearing", Notes = "Demo internal clearing wallet - never an economic party", EffectiveFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, ct);

        var now = DateTime.UtcNow;
        var start = now.Date.AddDays(-2).AddHours(8);
        var msgs = new List<JsonElement>
        {
            J(new { sourceTransactionId = "DEMO-T-001", type = "P2P", status = "SUCC", eventTime = start, channel = "Mobile", amount = 950m, currency = "JOD", senderWallet = "W-DEMO-1", receiverWallet = "W-DEMO-2", sourceCountry = "JO", destinationCountry = "JO", isTestData = true }),
            J(new { sourceTransactionId = "DEMO-T-002", type = "P2P", status = "SUCC", eventTime = start.AddHours(2), channel = "Mobile", amount = 960m, currency = "JOD", senderWallet = "W-DEMO-1", receiverWallet = "W-DEMO-2", sourceCountry = "JO", destinationCountry = "JO", isTestData = true }),
            J(new { sourceTransactionId = "DEMO-T-003", type = "P2P", status = "SUCC", eventTime = start.AddHours(4), channel = "Mobile", amount = 940m, currency = "JOD", senderWallet = "W-DEMO-1", receiverWallet = "W-DEMO-2", sourceCountry = "JO", destinationCountry = "JO", isTestData = true }),
            J(new { sourceTransactionId = "DEMO-T-004", type = "P2P", status = "PEND", eventTime = start.AddHours(6), channel = "Mobile", amount = 120m, currency = "JOD", senderWallet = "W-DEMO-1", receiverWallet = "W-DEMO-2", isTestData = true }),
            J(new { sourceTransactionId = "DEMO-T-005", type = "MerchantPayment", status = "SUCC", eventTime = start.AddHours(7), channel = "Mobile", amount = 200m, currency = "USD", senderWallet = "W-DEMO-1", receiverWallet = "W-DEMO-2", isTestData = true }),
            J(new { sourceTransactionId = "DEMO-T-005-R", type = "Reversal", status = "SUCC", eventTime = start.AddHours(9), channel = "Mobile", amount = 200m, currency = "USD", senderWallet = "W-DEMO-2", receiverWallet = "W-DEMO-1", isReversal = true, originalSourceTransactionId = "DEMO-T-005", isTestData = true }),
            J(new { sourceTransactionId = "DEMO-T-006", originalReference = "DEMO-REF-CLR", type = "P2P", status = "SUCC", eventTime = start.AddHours(10), channel = "Mobile", amount = 75m, currency = "JOD", senderWallet = "W-DEMO-1", receiverWallet = "W-DEMO-CLR", isTestData = true }),
            J(new { sourceTransactionId = "DEMO-T-007", originalReference = "DEMO-REF-CLR", type = "P2P", status = "SUCC", eventTime = start.AddHours(10).AddMinutes(1), channel = "Mobile", amount = 75m, currency = "JOD", senderWallet = "W-DEMO-CLR", receiverWallet = "W-DEMO-2", isTestData = true }),
        };
        var ingestion = sp.GetRequiredService<IngestionService>();
        await ingestion.IngestAsync("CORE", new IngestionBatchRequest { BatchId = "DEMO-BATCH-1", Transactions = msgs }, ct);

        var risk = sp.GetRequiredService<RiskAssessmentService>();
        await risk.AssessAsync(demo.Id, "Onboarding", "demo:onboarding", "Demo onboarding assessment", null, ct);
        await risk.AssessAsync(corp.Id, "Onboarding", "demo:onboarding", "Demo onboarding assessment", null, ct);

        var exec = sp.GetRequiredService<RuleExecutionService>();
        var structuring = await db.Set<AmlRuleVersion>().Join(db.Set<AmlRule>().Where(r => r.Code == "STRUCTURING"), v => v.RuleId, r => r.Id, (v, r) => v).FirstAsync(ct);
        await exec.RunAsync(structuring.Id, start.AddHours(-1), now, new RuleRunOptions("Manual", true, "seed"), ct);
        var alert = await db.Set<AmlAlert>().FirstAsync(a => a.PrimaryCustomerId == demo.Id, ct);

        var cases = sp.GetRequiredService<CaseService>();
        await cases.CreateAsync(new CaseCreateRequest { Title = "Demo investigation - possible structuring (fictitious)", PrimaryCustomerId = demo.Id, AlertIds = new() { alert.Id }, AssignedInvestigator = "officer", Summary = "Demo case created by seed data." }, ct);
        var theCase = await db.Set<AmlCase>().OrderByDescending(c => c.Id).FirstAsync(ct);
        theCase.IsTestData = true;
        await db.SaveChangesAsync(ct);
        var reports = sp.GetRequiredService<RegulatoryReportService>();
        await reports.CreateFromCaseAsync(theCase.Id, "STR", null, ct);
        var str = await db.Set<RegulatoryReport>().OrderByDescending(r => r.Id).FirstAsync(ct);
        str.IsTestData = true;
        str.Narrative = "DEMO ONLY - fictitious narrative. Never submitted. Three transfers just below 1,000 JOD within six hours to the same counterparty.";
        str.IndicatorCodes = "STRUCTURING";
        foreach (var c in await db.Set<Customer>().Where(c => c.ExternalId.StartsWith("DEMO")).ToListAsync(ct)) c.IsTestData = true;
        foreach (var w in await db.Set<Wallet>().Where(w => w.ExternalWalletId.StartsWith("W-DEMO")).ToListAsync(ct)) w.IsTestData = true;
        await db.SaveChangesAsync(ct);
    }
}
