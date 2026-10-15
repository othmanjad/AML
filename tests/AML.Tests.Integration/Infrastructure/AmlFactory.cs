using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AML.Application.Risk;
using AML.Application.Transactions;
using AML.Infrastructure;
using AML.Infrastructure.Jobs;
using AML.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace AML.Tests.Integration.Infrastructure;

/// <summary>Links a test to test-case IDs from the AML test-case workbooks.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class TcAttribute : Attribute
{
    public TcAttribute(params string[] ids) => Ids = ids;
    public string[] Ids { get; }
}

/// <summary>Injectable faults for resilience tests.</summary>
public sealed class TestFaultInjector : IFaultInjector
{
    private readonly ConcurrentDictionary<string, (int Remaining, Func<Exception> Factory)> _faults = new();
    public void Arm(string point, int times, Func<Exception> factory) => _faults[point] = (times, factory);
    public void Clear() => _faults.Clear();
    public void Check(string point)
    {
        if (!_faults.TryGetValue(point, out var f) || f.Remaining <= 0) return;
        _faults[point] = (f.Remaining - 1, f.Factory);
        throw f.Factory();
    }
}

public sealed class AmlFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test#Passw0rd!2026";
    public const string ClientSecret = "test-client-secret-xyz";
    public string DbName { get; } = "AML_Test_" + Guid.NewGuid().ToString("N")[..10];
    public string LogFile { get; } = Path.Combine(Path.GetTempPath(), $"aml-test-{Guid.NewGuid():N}.log");
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), $"aml-files-{Guid.NewGuid():N}");
    public TestFaultInjector Faults { get; } = new();
    public string EnvironmentName { get; set; } = "Testing";
    public static string ServerConnection => Environment.GetEnvironmentVariable("AML_TEST_SQL") ?? "Server=localhost,1433;User Id=sa;Password=Dev_Passw0rd!;TrustServerCertificate=true";
    public string ConnectionString => ServerConnection + $";Database={DbName}";
    public bool DemoData { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        foreach (var (k, v) in Settings()) builder.UseSetting(k, v);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IFaultInjector>();
            s.AddSingleton<IFaultInjector>(Faults);
        });
    }

    public Dictionary<string, string?> Settings() => new()
    {
        ["ConnectionStrings:Aml"] = ConnectionString,
        ["Security:EncryptionKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-encryption-key-0123456789ABCDEF")),
        ["Security:SearchTokenKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-search-token-key-0123456789AB")),
        ["Security:IntegrityKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-integrity-key-0123456789ABCDE")),
        ["Auth:SigningKey"] = "test-signing-key-0123456789-abcdefghijklmnop",
        ["DevAuth:Enabled"] = "true",
        ["DevAuth:SeedPassword"] = Password,
        ["Integrations:SeedClientSecret"] = ClientSecret,
        ["Database:MigrateOnStartup"] = "true",
        ["Seed:DemoData"] = DemoData ? "true" : "false",
        ["Jobs:RunInApi"] = "false",
        ["Storage:Root"] = StorageRoot,
        ["Logging:FilePath"] = LogFile,
        ["Https:Port"] = "443",
    };

    public async Task<Api> LoginAsync(string user)
    {
        var c = HttpsClient();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = Password });
        r.EnsureSuccessStatusCode();
        var token = (await r.Content.ReadFromJsonAsync<JsonObject>())!["accessToken"]!.GetValue<string>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new Api(c, user, token);
    }

    public async Task<Api> ClientAsync(string clientId = "jo-demo-core")
    {
        var c = HttpsClient();
        var r = await c.PostAsJsonAsync("/api/auth/token", new { clientId, clientSecret = ClientSecret });
        r.EnsureSuccessStatusCode();
        var token = (await r.Content.ReadFromJsonAsync<JsonObject>())!["accessToken"]!.GetValue<string>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new Api(c, clientId, token);
    }

    public Api Anonymous() => new(HttpsClient(), "anonymous", "");

    public HttpClient HttpsClient() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    /// <summary>Runs code in a system-scoped service scope (direct DB/service access for assertions).</summary>
    public async Task<T> WithScope<T>(Func<IServiceProvider, Task<T>> f, long tenantId = 1, string? asUser = null)
    {
        using var scope = Services.CreateScope();
        JobExecutor.SetSystem(scope.ServiceProvider, tenantId);
        if (asUser is not null)
        {
            var u = scope.ServiceProvider.GetRequiredService<CurrentUser>();
            u.Username = asUser;
            u.IsSystem = false;
            u.PermissionSet = AML.Application.Common.Perm.All().ToHashSet();
        }
        return await f(scope.ServiceProvider);
    }

    public Task WithDb(Func<AmlDbContext, Task> f) => WithScope<object?>(async sp => { await f(sp.GetRequiredService<AmlDbContext>()); return null; });
    public Task<T> WithDb<T>(Func<AmlDbContext, Task<T>> f) => WithScope(sp => f(sp.GetRequiredService<AmlDbContext>()));

    public async Task<int> ExecSql(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> Scalar(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    public async Task SetSetting(string key, string value)
    {
        var n = await ExecSql($"UPDATE SystemSettings SET Value = N'{value.Replace("'", "''")}' WHERE [Key] = N'{key}' AND TenantId = 1");
        if (n == 0) await ExecSql($"INSERT INTO SystemSettings (TenantId, [Key], Value, UpdatedAt) VALUES (1, N'{key}', N'{value.Replace("'", "''")}', SYSUTCDATETIME())");
    }

    public string ReadLog()
    {
        foreach (var p in Serilog.Log.Logger is null ? Array.Empty<string>() : Array.Empty<string>()) _ = p;
        using var fs = new FileStream(LogFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Environment.GetEnvironmentVariable("AML_TEST_KEEP_DB") == "1") return;
        try
        {
            SqlConnection.ClearAllPools();
            await using var conn = new SqlConnection(ServerConnection);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($"ALTER DATABASE [{DbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DbName}];", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best effort */ }
    }
}

/// <summary>Thin JSON API client used by integration tests.</summary>
public sealed class Api
{
    public HttpClient Http { get; }
    public string User { get; }
    public string Token { get; }

    public Api(HttpClient http, string user, string token)
    {
        Http = http; User = user; Token = token;
    }

    public async Task<(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Response)> Send(HttpMethod m, string url, object? body = null, IDictionary<string, string>? headers = null)
    {
        using var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = body is HttpContent hc ? hc : new StringContent(body is string s ? s : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) req.Headers.TryAddWithoutValidation(k, v);
        var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        JsonNode? node = null;
        if (!string.IsNullOrWhiteSpace(text) && (res.Content.Headers.ContentType?.MediaType?.Contains("json") ?? false))
            try { node = JsonNode.Parse(text); } catch (JsonException) { }
        return (res.StatusCode, node, res);
    }

    public Task<(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Response)> Get(string url) => Send(HttpMethod.Get, url);
    public Task<(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Response)> Post(string url, object? body = null) => Send(HttpMethod.Post, url, body ?? new { });
    public Task<(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Response)> Put(string url, object? body = null) => Send(HttpMethod.Put, url, body ?? new { });

    public async Task<JsonNode> GetOk(string url) => Ok(await Get(url), "GET " + url);
    public async Task<JsonNode> PostOk(string url, object? body = null) => Ok(await Post(url, body), "POST " + url);
    public async Task<JsonNode> PutOk(string url, object? body = null) => Ok(await Put(url, body), "PUT " + url);

    private static JsonNode Ok((HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Response) r, string what)
    {
        if ((int)r.Status >= 300) throw new Xunit.Sdk.XunitException($"{what} failed with {(int)r.Status}: {r.Body?.ToJsonString()}");
        return r.Body ?? JsonValue.Create(true)!;
    }
}

[CollectionDefinition("aml")]
public sealed class AmlCollection : ICollectionFixture<AmlFactory> { }

/// <summary>Base class with shared helpers and per-test output.</summary>
[Collection("aml")]
public abstract class AmlTestBase
{
    protected readonly AmlFactory F;
    protected readonly ITestOutputHelper Out;
    private static readonly ConcurrentDictionary<string, Api> Sessions = new();

    protected AmlTestBase(AmlFactory f, ITestOutputHelper output)
    {
        F = f;
        Out = output;
        F.Faults.Clear();
    }

    protected async Task<Api> As(string user)
    {
        var key = F.DbName + ":" + user;
        if (Sessions.TryGetValue(key, out var a)) return a;
        a = await F.LoginAsync(user);
        Sessions[key] = a;
        return a;
    }

    /// <summary>Drops a cached session (e.g. after its tokens were revoked).</summary>
    protected void Forget(string user) => Sessions.TryRemove(F.DbName + ":" + user, out _);

    /// <summary>Records the observed outcome (copied into the test-execution workbook as "Actual Result").</summary>
    protected void Actual(string text) => Out.WriteLine("ACTUAL: " + text);

    protected static string U(string prefix = "T") => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, prefix.Length + 33)];

    protected async Task<JsonNode> CreateCustomer(string type = "Individual", object? attributes = null, string? nameLatin = null, string? nameArabic = null, string? nationality = "JO",
        string? region = "CENTRAL", object? identifications = null, object? financialProfile = null, string source = "CORE", string? externalId = null, string user = "manager",
        bool isPep = false, string? dob = null, long? onboardingAgentId = null, string? incorporation = null)
    {
        var api = await As(user);
        attributes ??= type == "Individual" ? new Dictionary<string, object> { ["OCCUPATION"] = "EMPLOYEE", ["SOURCE_OF_INCOME"] = "SALARY" } : new Dictionary<string, object>();
        return await api.PostOk("/api/customers", new
        {
            sourceSystem = source, externalId = externalId ?? U("C"), customerType = type, nameLatin = nameLatin ?? "Test Person " + Guid.NewGuid().ToString("N")[..6], nameArabic,
            nationalityCountry = type == "Individual" ? nationality : null, residenceCountry = type == "Individual" ? nationality : null, incorporationCountry = incorporation, region, attributes, identifications,
            financialProfile, isPep, dateOfBirth = dob, onboardingAgentId
        });
    }

    protected async Task<string> CreateWallet(long? customerId, string? ext = null, string source = "CORE")
    {
        var api = await As("manager");
        ext ??= U("W");
        await api.PostOk("/api/wallets", new { sourceSystem = source, externalWalletId = ext, customerId, walletType = "Personal" });
        return ext;
    }

    protected async Task<JsonNode> Ingest(params object[] txns) => await IngestBatch(null, txns);

    protected async Task<JsonNode> IngestBatch(object[]? controlTotals, params object[] txns)
    {
        var client = await F.ClientAsync();
        return await client.PostOk("/api/integrations/CORE/transactions", new { batchId = U("B"), transactions = txns, controlTotals });
    }

    protected static object Txn(string id, string sender, string receiver, decimal amount, DateTime at, string type = "P2P", string status = "SUCC", string currency = "JOD",
        string? channel = "Mobile", string? reference = null, string? original = null, bool reversal = false, bool refund = false, string? src = "JO", string? dst = "JO",
        object? originator = null, object? beneficiary = null, string? remarks = null, string? device = null, string? agent = null, string? cop = null) => new
    {
        sourceTransactionId = id, originalReference = reference, type, status, eventTime = at, channel, amount, currency, senderWallet = sender, receiverWallet = receiver,
        originalSourceTransactionId = original, isReversal = reversal, isRefund = refund, sourceCountry = src, destinationCountry = dst, originator, beneficiary, remarks,
        deviceId = device, agentCustomerExternalId = agent, copResult = cop
    };

    protected async Task<long> TxnId(string sourceTxnId) =>
        await F.WithDb(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
            System.Linq.Queryable.Select(System.Linq.Queryable.Where(db.Set<AML.Domain.Entities.Transaction>(), t => t.SourceTransactionId == sourceTxnId), t => t.Id)));

    /// <summary>Creates, validates, simulates, submits, approves and publishes a rule; returns rule id.</summary>
    protected async Task<long> PublishRule(object definition, string? code = null, string severity = "Medium", DateTime? simFrom = null)
    {
        var officer = await As("officer");
        var senior = await As("senior");
        var rule = await officer.PostOk("/api/admin/aml-rules", new { code = code ?? U("R").ToUpperInvariant().Replace("-", "_"), name = "Test rule", category = "Test", severity, definition });
        var id = rule["id"]!.GetValue<long>();
        await officer.PostOk($"/api/admin/aml-rules/{id}/simulate", new { from = simFrom ?? DateTime.UtcNow.AddDays(-2), to = DateTime.UtcNow.AddMinutes(1) });
        await officer.PostOk($"/api/admin/aml-rules/{id}/submit", new { rationale = "test", testEvidence = "simulation", expectedImpact = "low" });
        await senior.PostOk($"/api/admin/aml-rules/{id}/approve", new { comment = "ok" });
        await officer.PostOk($"/api/admin/aml-rules/{id}/publish");
        return id;
    }

    protected async Task<JsonNode> RunRule(long ruleId, DateTime from, DateTime to) =>
        await (await As("officer")).PostOk($"/api/admin/aml-rules/{ruleId}/run", new { from, to });


    protected static readonly object FullKycAttributes = new Dictionary<string, object> { ["OCCUPATION"] = "EMPLOYEE", ["SOURCE_OF_INCOME"] = "SALARY" };

    /// <summary>Individual with complete KYC (id, occupation, income, financial profile) likely to score Low.</summary>
    protected async Task<JsonNode> CompleteKycCustomer(string? name = null, string nationality = "JO", bool isPep = false, string? nid = null, string? dob = "1990-05-05", string user = "manager")
    {
        nid ??= "9" + Random.Shared.NextInt64(100000000, 999999999);
        return await CreateCustomer(nameLatin: name, nationality: nationality, isPep: isPep, dob: dob, user: user,
            identifications: new[] { new { idType = "NationalId", idNumber = nid, issuingCountry = "JO", expiryDate = "2035-01-01" } },
            financialProfile: new { declaredMonthlyIncome = 900, currency = "JOD", sourceOfFunds = "Salary", sourceOfWealth = "Savings", occupation = "EMPLOYEE", expectedMonthlyVolume = 1000, expectedMonthlyCount = 10 });
    }

    protected async Task SetProvider(string mode, int failTimes = 0) =>
        await (await As("admin")).PostOk("/api/screening/provider-simulation", new { mode, failTimes });

    protected Task<List<AML.Domain.Entities.ScreeningResult>> ScreeningResults(long customerId) =>
        F.WithDb(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            System.Linq.Queryable.Where(db.Set<AML.Domain.Entities.ScreeningResult>(), r => r.CustomerId == customerId)));

    protected async Task<JsonNode> Transition(long resultId, string to, string user = "senior", string? notes = "test rationale") =>
        await (await As(user)).PostOk($"/api/screening/results/{resultId}/transition", new { toStatus = to, notes });

    protected static long Id(JsonNode n) => n["id"]!.GetValue<long>();

    protected static Dictionary<string, object> Checklist() => new() { ["reviewedTransactions"] = true, ["reviewedCustomerProfile"] = true };
}
