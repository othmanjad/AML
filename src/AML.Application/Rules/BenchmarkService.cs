using System.Text.RegularExpressions;
using AML.Application.Common;
using AML.Domain.Entities;
using AML.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Rules;

public sealed class BenchmarkInput
{
    public string Metric { get; set; } = "";
    public string SegmentField { get; set; } = "";
    public string SegmentValue { get; set; } = "";
    public decimal Value { get; set; }
    public string? Source { get; set; }
    public DateTime? EffectiveFrom { get; set; }
}

/// <summary>
/// Industry / segment benchmarks used by the benchmarkDeviation rule aggregate. A new value for the same metric and segment
/// closes the previous one (effective-dated history is kept). Values must cite their source and are to be confirmed by compliance.
/// </summary>
public sealed class BenchmarkService
{
    private static readonly Regex MetricCode = new("^[A-Z0-9_]{1,50}$");
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;

    public BenchmarkService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit)
    {
        _db = db; _user = user; _clock = clock; _audit = audit;
    }

    public async Task<List<IndustryBenchmark>> ListAsync(string? metric, bool includeHistory, CancellationToken ct)
    {
        var q = _db.Set<IndustryBenchmark>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(metric)) q = q.Where(b => b.Metric == metric);
        if (!includeHistory) q = q.Where(b => b.IsActive && b.EffectiveTo == null);
        return await q.OrderBy(b => b.Metric).ThenBy(b => b.SegmentField).ThenBy(b => b.SegmentValue).ThenByDescending(b => b.EffectiveFrom).Take(1000).ToListAsync(ct);
    }

    public async Task<IndustryBenchmark> CreateAsync(BenchmarkInput i, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RulesConfig);
        var metric = i.Metric.Trim().ToUpperInvariant();
        var errors = new Dictionary<string, List<string>>();
        if (!MetricCode.IsMatch(metric)) errors["metric"] = new() { "Metric code: A-Z, 0-9 and _ (max 50)." };
        if (RuleFieldRegistry.Resolve(i.SegmentField, null) is null && !i.SegmentField.StartsWith("field:", StringComparison.Ordinal))
            errors["segmentField"] = new() { $"Unknown segment field '{i.SegmentField}' (customer.* or field:CODE)." };
        if (string.IsNullOrWhiteSpace(i.SegmentValue) || i.SegmentValue.Length > 200) errors["segmentValue"] = new() { "Segment value is required (max 200)." };
        if (string.IsNullOrWhiteSpace(i.Source)) errors["source"] = new() { "The benchmark source is required." };
        ValidationException.ThrowIf(errors);
        var from = i.EffectiveFrom ?? _clock.UtcNow;
        var segValue = i.SegmentValue.Trim();
        foreach (var prev in await _db.Set<IndustryBenchmark>().Where(b => b.Metric == metric && b.SegmentField == i.SegmentField && b.SegmentValue == segValue && b.EffectiveTo == null && b.IsActive).ToListAsync(ct))
            prev.EffectiveTo = from;
        var b = new IndustryBenchmark
        {
            TenantId = _user.TenantId, Metric = metric, SegmentField = i.SegmentField, SegmentValue = segValue, Value = i.Value, Source = i.Source!.Trim(),
            EffectiveFrom = from, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username
        };
        _db.Set<IndustryBenchmark>().Add(b);
        await _db.SaveChangesAsync(ct);
        _audit.Record("BENCHMARK_CREATE", "IndustryBenchmark", b.Id.ToString(), null, new { b.Metric, b.SegmentField, b.SegmentValue, b.Value, b.Source, b.EffectiveFrom });
        await _db.SaveChangesAsync(ct);
        return b;
    }

    public async Task<IndustryBenchmark> DeactivateAsync(long id, string reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RulesConfig);
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A reason is required.");
        var b = await _db.Set<IndustryBenchmark>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Benchmark", id);
        b.IsActive = false;
        b.EffectiveTo ??= _clock.UtcNow;
        _audit.Record("BENCHMARK_DEACTIVATE", "IndustryBenchmark", id.ToString(), null, new { reason });
        await _db.SaveChangesAsync(ct);
        return b;
    }
}
