using AML.Infrastructure;
using AML.Infrastructure.Jobs;
using AML.Infrastructure.Services;
using Serilog;
using Serilog.Formatting.Compact;

// Background worker host: runs all scheduled jobs (rule scheduler, screening/action retries, SLA escalations,
// outbox publication, exports, reconciliation, entity resolution) in a system security context.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((sp, cfg) => cfg.ReadFrom.Configuration(builder.Configuration).Enrich.FromLogContext()
    .WriteTo.Console(new MaskedFormatter()));
builder.Services.AddAmlInfrastructure(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddHostedService<JobRunnerHostedService>();
await builder.Build().RunAsync();

sealed class MaskedFormatter : Serilog.Formatting.ITextFormatter
{
    private readonly CompactJsonFormatter _inner = new();
    public void Format(Serilog.Events.LogEvent e, TextWriter output)
    {
        using var sw = new StringWriter();
        _inner.Format(e, sw);
        output.Write(SensitiveDataMasker.Mask(sw.ToString()));
    }
}
