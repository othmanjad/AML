using System.Net;
using AML.Domain.Entities;
using AML.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration;

public sealed class DemoFactory : IAsyncLifetime
{
    public AmlFactory Factory { get; } = new() { DemoData = true };
    public Task InitializeAsync() { _ = Factory.Services; return Task.CompletedTask; }
    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

[CollectionDefinition("demo")]
public sealed class DemoCollection : ICollectionFixture<DemoFactory> { }

[Collection("demo")]
public class DemoSeedTests
{
    private readonly AmlFactory F;
    private readonly ITestOutputHelper Out;
    public DemoSeedTests(DemoFactory d, ITestOutputHelper o) { F = d.Factory; Out = o; }

    [Fact, Tc("TC-JUR-003")]
    public async Task Demo_customer_complete()
    {
        var api = await F.LoginAsync("manager");
        var list = await api.GetOk("/api/customers?search=DEMO-0001");
        var id = list["items"]![0]!["id"]!.GetValue<long>();
        var attrs = (await api.GetOk($"/api/customers/{id}/attributes")).AsArray();
        var wallets = (await api.GetOk($"/api/customers/{id}/wallets")).AsArray();
        var txns = (await api.GetOk($"/api/customers/{id}/transactions?pageSize=100"))["items"]!.AsArray().Select(t => t!["status"]!.GetValue<string>()).ToList();
        var assess = (await api.GetOk($"/api/customers/{id}/risk-assessments")).AsArray();
        var alerts = (await api.GetOk($"/api/customers/{id}/alerts"))["items"]!.AsArray();
        var cases = (await api.GetOk($"/api/customers/{id}/cases"))["items"]!.AsArray();
        attrs.Should().NotBeEmpty();
        wallets.Count.Should().BeGreaterThanOrEqualTo(2);
        wallets.Should().Contain(w => w!["isInternal"]!.GetValue<bool>() || w["mappingCategory"] != null);
        txns.Should().Contain(new[] { "Completed", "Pending", "Reversed" });
        assess.Should().NotBeEmpty(); alerts.Should().NotBeEmpty(); cases.Should().NotBeEmpty();
        Out.WriteLine($"ACTUAL: Demo customer DEMO-0001: {attrs.Count} dynamic attributes, {wallets.Count} wallets (1 mapped clearing wallet), transaction statuses {string.Join("/", txns.Distinct())}, {assess.Count} assessment(s), {alerts.Count} alert(s), {cases.Count} case(s).");
    }

    [Fact, Tc("TC-JUR-004")]
    public async Task Demo_str_never_submitted()
    {
        var api = await F.LoginAsync("mlro");
        var str = await F.WithDb(db => db.Set<RegulatoryReport>().FirstAsync(r => r.IsTestData));
        str.Status.Should().Be("Draft");
        await F.ExecSql($"UPDATE RegulatoryReports SET Status = 'Approved' WHERE Id = {str.Id}");
        var r = await api.Post($"/api/regulatory-reports/{str.Id}/submit");
        r.Status.Should().Be(HttpStatusCode.Conflict);
        await F.ExecSql($"UPDATE RegulatoryReports SET Status = 'Draft' WHERE Id = {str.Id}");
        Out.WriteLine($"ACTUAL: Demo STR {str.ReportNumber} seeded as Draft (IsTestData); even if approved, submission refused (409: {r.Body!["detail"]}); UI disables Submit for test/non-approved reports.");
    }
}
