using System.Text.Json;
using System.Text.Json.Nodes;
using AML.Application.Common;
using AML.Application.Fields;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Rules;

public sealed class RuleCreateRequest
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Category { get; set; } = "";
    public string RuleType { get; set; } = "Batch";
    public string Severity { get; set; } = "Medium";
    public string? TypologyCodes { get; set; }
    public JsonElement Definition { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
}

public sealed class RuleVersionUpdateRequest
{
    public JsonElement Definition { get; set; }
    public string? Severity { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class RuleSubmitRequest
{
    public string? Rationale { get; set; }
    public string? TestEvidence { get; set; }
    public string? ExpectedImpact { get; set; }
}

public sealed class RuleWindowRequest
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public long? VersionId { get; set; }
    public bool CreateAlerts { get; set; }
}

public sealed class TuningRequest
{
    public string RunType { get; set; } = "ATL";
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    /// <summary>JSON pointer-like path to the threshold value inside the definition, e.g. /condition/conditions/0/value.</summary>
    public string ThresholdPath { get; set; } = "";
    public decimal CandidateValue { get; set; }
    public decimal SamplePercent { get; set; } = 20;
}

public sealed record RuleVersionDto(long Id, long RuleId, int Version, string Status, JsonElement Definition, string Severity, DateTime EffectiveFrom, DateTime? EffectiveTo,
    string? Rationale, string? TestEvidence, string? ExpectedImpact, string? ValidationResult, DateTime? ValidatedAt, DateTime? SimulatedAt, string? SimulationSummary,
    string? CreatedBy, DateTime CreatedAt, string? SubmittedBy, string? ApprovedBy, DateTime? ApprovedAt, string? PublishedBy, DateTime? PublishedAt, string RowVersion);

public sealed record RuleDto(long Id, string Code, string Name, string? Description, string Category, string RuleType, string Severity, bool IsSample, bool IsRealtimeCapable,
    string? TypologyCodes, long? ActiveVersionId, int? ActiveVersion, string Status, DateTime CreatedAt, IReadOnlyList<RuleVersionDto> Versions);

public sealed record ValidationResultDto(bool Valid, IReadOnlyList<RuleValidationError> Errors);

public sealed class RuleService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly DependencyService _deps;
    private readonly RuleExecutionService _exec;
    private readonly TenantSettingsService _settings;

    public RuleService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, DependencyService deps, RuleExecutionService exec, TenantSettingsService settings)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _deps = deps; _exec = exec; _settings = settings;
    }

    public static RuleVersionDto ToDto(AmlRuleVersion v) => new(v.Id, v.RuleId, v.Version, v.Status, ParseOrString(v.DefinitionJson), v.Severity, v.EffectiveFrom, v.EffectiveTo,
        v.Rationale, v.TestEvidence, v.ExpectedImpact, v.ValidationResult, v.ValidatedAt, v.SimulatedAt, v.SimulationSummary, v.CreatedBy, v.CreatedAt, v.SubmittedBy,
        v.ApprovedBy, v.ApprovedAt, v.PublishedBy, v.PublishedAt, Convert.ToBase64String(v.RowVersion));

    private static JsonElement ParseOrString(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse(JsonSerializer.Serialize(json)).RootElement.Clone(); }
    }

    private static string StatusOf(AmlRule r, List<AmlRuleVersion> versions)
    {
        if (versions.Any(v => v.Status == ConfigStatus.Active)) return ConfigStatus.Active;
        if (versions.Any(v => v.Status == ConfigStatus.Disabled)) return ConfigStatus.Disabled;
        return versions.OrderByDescending(v => v.Version).Select(v => v.Status).FirstOrDefault() ?? ConfigStatus.Draft;
    }

    public async Task<List<RuleDto>> ListAsync(CancellationToken ct)
    {
        var rules = await _db.Set<AmlRule>().AsNoTracking().OrderBy(r => r.Code).ToListAsync(ct);
        var versions = await _db.Set<AmlRuleVersion>().AsNoTracking().ToListAsync(ct);
        return rules.Select(r =>
        {
            var vs = versions.Where(v => v.RuleId == r.Id).OrderByDescending(v => v.Version).ToList();
            return new RuleDto(r.Id, r.Code, r.Name, r.Description, r.Category, r.RuleType, r.Severity, r.IsSample, r.IsRealtimeCapable, r.TypologyCodes, r.ActiveVersionId,
                vs.FirstOrDefault(v => v.Id == r.ActiveVersionId)?.Version, StatusOf(r, vs), r.CreatedAt, vs.Select(ToDto).ToList());
        }).ToList();
    }

    public async Task<RuleDto> GetAsync(long id, CancellationToken ct)
    {
        var r = await _db.Set<AmlRule>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Rule", id);
        var vs = await _db.Set<AmlRuleVersion>().AsNoTracking().Where(v => v.RuleId == id).OrderByDescending(v => v.Version).ToListAsync(ct);
        return new RuleDto(r.Id, r.Code, r.Name, r.Description, r.Category, r.RuleType, r.Severity, r.IsSample, r.IsRealtimeCapable, r.TypologyCodes, r.ActiveVersionId,
            vs.FirstOrDefault(v => v.Id == r.ActiveVersionId)?.Version, StatusOf(r, vs), r.CreatedAt, vs.Select(ToDto).ToList());
    }

    private async Task<List<RuleValidationError>> ValidateDefinitionAsync(string json, CancellationToken ct)
    {
        var fieldTypes = await _deps.PublishedFieldTypesAsync(ct);
        var (_, errors) = RuleValidator.ValidateJson(json, fieldTypes, ActionTypes.All);
        return errors;
    }

    private static void ThrowIfInvalid(List<RuleValidationError> errors)
    {
        if (errors.Count == 0) return;
        throw new ValidationException(errors.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()),
            "Rule definition is invalid: " + string.Join(" ", errors.Select(e => $"{e.Path}: {e.Message}")));
    }

    public async Task<RuleDto> CreateAsync(RuleCreateRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name)) throw new ValidationException("code", "Rule code and name are required.");
        var code = r.Code.Trim().ToUpperInvariant();
        if (await _db.Set<AmlRule>().AnyAsync(x => x.Code == code, ct)) throw new ConflictException($"Rule {code} already exists.");
        var json = r.Definition.ValueKind == JsonValueKind.Undefined ? "{}" : r.Definition.GetRawText();
        var errors = await ValidateDefinitionAsync(json, ct);
        ThrowIfInvalid(errors);
        var def = RuleDefinition.Parse(json);
        var rule = new AmlRule
        {
            TenantId = _user.TenantId, Code = code, Name = r.Name, Description = r.Description, Category = r.Category, RuleType = r.RuleType, Severity = r.Severity,
            TypologyCodes = r.TypologyCodes, IsRealtimeCapable = def.RealtimeCapable, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username
        };
        _db.Set<AmlRule>().Add(rule);
        await _db.SaveChangesAsync(ct);
        var v = new AmlRuleVersion
        {
            TenantId = rule.TenantId, RuleId = rule.Id, Version = 1, Status = ConfigStatus.Draft, DefinitionJson = json, Severity = r.Severity,
            EffectiveFrom = r.EffectiveFrom ?? _clock.UtcNow, EffectiveTo = r.EffectiveTo, CreatedBy = _user.Username, CreatedAt = _clock.UtcNow,
            ValidationResult = "Valid", ValidatedAt = _clock.UtcNow
        };
        _db.Set<AmlRuleVersion>().Add(v);
        _audit.Record("RULE_CREATE_DRAFT", "AmlRule", code, null, new { r.Name, r.Category, r.Severity, definition = json });
        await _db.SaveChangesAsync(ct);
        return await GetAsync(rule.Id, ct);
    }

    private async Task<AmlRuleVersion> VersionAsync(long ruleId, long? versionId, Func<AmlRuleVersion, bool>? pick, CancellationToken ct)
    {
        var q = _db.Set<AmlRuleVersion>().Where(v => v.RuleId == ruleId);
        if (versionId is { } vid) return await q.FirstOrDefaultAsync(v => v.Id == vid, ct) ?? throw new NotFoundException("Rule version", vid);
        var list = await q.OrderByDescending(v => v.Version).ToListAsync(ct);
        if (list.Count == 0) throw new NotFoundException("Rule", ruleId);
        return (pick is null ? list.FirstOrDefault() : list.FirstOrDefault(pick)) ?? throw new ConflictException("No rule version in the required state.");
    }

    public async Task<RuleVersionDto> UpdateDraftAsync(long ruleId, long versionId, RuleVersionUpdateRequest r, CancellationToken ct)
    {
        var v = await VersionAsync(ruleId, versionId, null, ct);
        if (v.Status != ConfigStatus.Draft) throw new ConflictException($"Version {v.Version} is {v.Status}; only drafts can be edited. Create a new version.");
        _db.Entry(v).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(r.RowVersion, "rule version");
        var json = r.Definition.GetRawText();
        var errors = await ValidateDefinitionAsync(json, ct);
        ThrowIfInvalid(errors);
        var old = v.DefinitionJson;
        v.DefinitionJson = json;
        if (r.Severity is not null) v.Severity = r.Severity;
        if (r.EffectiveFrom is { } ef) v.EffectiveFrom = ef;
        v.EffectiveTo = r.EffectiveTo;
        v.ValidationResult = "Valid";
        v.ValidatedAt = _clock.UtcNow;
        v.SimulatedAt = null;
        _audit.Record("RULE_UPDATE_DRAFT", "AmlRule", $"{ruleId} v{v.Version}", new { definition = old }, new { definition = json });
        await _db.SaveChangesAsync(ct);
        return ToDto(v);
    }

    public async Task<RuleVersionDto> NewVersionAsync(long ruleId, CancellationToken ct)
    {
        var all = await _db.Set<AmlRuleVersion>().Where(v => v.RuleId == ruleId).ToListAsync(ct);
        if (all.Count == 0) throw new NotFoundException("Rule", ruleId);
        if (all.Any(v => v.Status is ConfigStatus.Draft or ConfigStatus.PendingApproval or ConfigStatus.Approved))
            throw new ConflictException("A draft, pending or approved version already exists for this rule.");
        var src = all.OrderByDescending(v => v.Status == ConfigStatus.Active).ThenByDescending(v => v.Version).First();
        var v = new AmlRuleVersion
        {
            TenantId = src.TenantId, RuleId = ruleId, Version = all.Max(x => x.Version) + 1, Status = ConfigStatus.Draft, DefinitionJson = src.DefinitionJson,
            Severity = src.Severity, EffectiveFrom = _clock.UtcNow, CreatedBy = _user.Username, CreatedAt = _clock.UtcNow
        };
        _db.Set<AmlRuleVersion>().Add(v);
        _audit.Record("RULE_NEW_VERSION", "AmlRule", $"{ruleId} v{v.Version}", null, new { from = src.Version });
        await _db.SaveChangesAsync(ct);
        return ToDto(v);
    }

    public async Task<ValidationResultDto> ValidateAsync(long ruleId, long? versionId, JsonElement? definition, CancellationToken ct)
    {
        if (definition is { ValueKind: not JsonValueKind.Undefined } d)
        {
            var errs = await ValidateDefinitionAsync(d.GetRawText(), ct);
            return new ValidationResultDto(errs.Count == 0, errs);
        }
        var v = await VersionAsync(ruleId, versionId, null, ct);
        var errors = await ValidateDefinitionAsync(v.DefinitionJson, ct);
        v.ValidationResult = errors.Count == 0 ? "Valid" : JsonSerializer.Serialize(errors);
        v.ValidatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new ValidationResultDto(errors.Count == 0, errors);
    }

    /// <summary>Dry-run over historical data: never creates alerts or actions.</summary>
    public async Task<RuleRunResult> SimulateAsync(long ruleId, RuleWindowRequest r, CancellationToken ct)
    {
        ValidateWindow(r);
        var v = await VersionAsync(ruleId, r.VersionId, x => x.Status is ConfigStatus.Draft or ConfigStatus.PendingApproval or ConfigStatus.Approved, ct);
        var errors = await ValidateDefinitionAsync(v.DefinitionJson, ct);
        ThrowIfInvalid(errors);
        var result = await _exec.RunAsync(v.Id, r.From, r.To, new RuleRunOptions("Simulation", false, _user.Username), ct);
        var tracked = await _db.Set<AmlRuleVersion>().FirstAsync(x => x.Id == v.Id, ct);
        tracked.SimulatedAt = _clock.UtcNow;
        tracked.SimulationSummary = $"{result.RecordsEvaluated} evaluated, {result.Matches} matches between {r.From:O} and {r.To:O} (execution {result.ExecutionId})";
        _audit.Record("RULE_SIMULATE", "AmlRule", $"{ruleId} v{v.Version}", null, new { result.ExecutionId, result.Matches });
        await _db.SaveChangesAsync(ct);
        return result;
    }

    private static void ValidateWindow(RuleWindowRequest r)
    {
        if (r.To <= r.From) throw new ValidationException("to", "to must be after from.");
        if ((r.To - r.From).TotalDays > 400) throw new ValidationException("to", "Maximum window is 400 days.");
    }

    public async Task<RuleVersionDto> SubmitAsync(long ruleId, RuleSubmitRequest r, CancellationToken ct)
    {
        var v = await VersionAsync(ruleId, null, x => x.Status == ConfigStatus.Draft, ct);
        var errors = await ValidateDefinitionAsync(v.DefinitionJson, ct);
        ThrowIfInvalid(errors);
        var missing = new Dictionary<string, List<string>>();
        if (string.IsNullOrWhiteSpace(r.Rationale)) missing["rationale"] = new() { "A change rationale is required." };
        if (string.IsNullOrWhiteSpace(r.TestEvidence)) missing["testEvidence"] = new() { "Test evidence (e.g. simulation/backtest reference) is required." };
        if (string.IsNullOrWhiteSpace(r.ExpectedImpact)) missing["expectedImpact"] = new() { "Expected alert-volume impact is required." };
        ValidationException.ThrowIf(missing);
        if (await _settings.BoolSettingAsync("Rules:RequireSimulation", true, ct: ct) && v.SimulatedAt is null)
            throw new ConflictException("Run a simulation of this version before submitting it for approval.");
        v.Rationale = r.Rationale; v.TestEvidence = r.TestEvidence; v.ExpectedImpact = r.ExpectedImpact;
        v.Status = ConfigStatus.PendingApproval;
        v.SubmittedBy = _user.Username;
        _audit.Record("RULE_SUBMIT", "AmlRule", $"{ruleId} v{v.Version}", null, r);
        await _db.SaveChangesAsync(ct);
        return ToDto(v);
    }

    public async Task<RuleVersionDto> ApproveAsync(long ruleId, string? comment, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RulesApprove);
        var v = await VersionAsync(ruleId, null, x => x.Status == ConfigStatus.PendingApproval, ct);
        Guard.FourEyes(v.CreatedBy ?? "", _user.Username, "Rule approval");
        Guard.FourEyes(v.SubmittedBy ?? "", _user.Username, "Rule approval");
        v.Status = ConfigStatus.Approved;
        v.ApprovedBy = _user.Username;
        v.ApprovedAt = _clock.UtcNow;
        _audit.Record("RULE_APPROVE", "AmlRule", $"{ruleId} v{v.Version}", null, new { v.ApprovedBy, v.Rationale, v.ExpectedImpact }, comment);
        await _db.SaveChangesAsync(ct);
        return ToDto(v);
    }

    public async Task<RuleVersionDto> RejectAsync(long ruleId, string reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RulesApprove);
        var v = await VersionAsync(ruleId, null, x => x.Status == ConfigStatus.PendingApproval, ct);
        v.Status = ConfigStatus.Draft;
        _audit.Record("RULE_REJECT", "AmlRule", $"{ruleId} v{v.Version}", null, null, reason);
        await _db.SaveChangesAsync(ct);
        return ToDto(v);
    }

    /// <summary>Activates an approved version. The previous active version stays active until this point and is then superseded.</summary>
    public async Task<RuleVersionDto> PublishAsync(long ruleId, CancellationToken ct)
    {
        var rule = await _db.Set<AmlRule>().FirstOrDefaultAsync(r => r.Id == ruleId, ct) ?? throw new NotFoundException("Rule", ruleId);
        var all = await _db.Set<AmlRuleVersion>().Where(v => v.RuleId == ruleId).ToListAsync(ct);
        var v = all.FirstOrDefault(x => x.Status == ConfigStatus.Approved) ?? throw new ConflictException("Only an approved version can be published (independent approval required).");
        var now = _clock.UtcNow;
        foreach (var prev in all.Where(x => x.Status == ConfigStatus.Active))
        {
            prev.Status = ConfigStatus.Superseded;
            prev.EffectiveTo = v.EffectiveFrom > now ? v.EffectiveFrom : now;
        }
        if (v.EffectiveFrom < now) v.EffectiveFrom = now;
        v.Status = ConfigStatus.Active;
        v.PublishedBy = _user.Username;
        v.PublishedAt = now;
        rule.ActiveVersionId = v.Id;
        rule.IsRealtimeCapable = RuleDefinition.Parse(v.DefinitionJson).RealtimeCapable;
        rule.Severity = v.Severity;
        _audit.Record("RULE_PUBLISH", "AmlRule", $"{rule.Code} v{v.Version}", null, new { v.Version, v.EffectiveFrom, v.ApprovedBy });
        await _db.SaveChangesAsync(ct);
        return ToDto(v);
    }

    public async Task<RuleVersionDto> SetEnabledAsync(long ruleId, bool enabled, string? reason, CancellationToken ct)
    {
        var rule = await _db.Set<AmlRule>().FirstOrDefaultAsync(r => r.Id == ruleId, ct) ?? throw new NotFoundException("Rule", ruleId);
        var v = await VersionAsync(ruleId, null, x => enabled ? x.Status == ConfigStatus.Disabled : x.Status == ConfigStatus.Active, ct);
        v.Status = enabled ? ConfigStatus.Active : ConfigStatus.Disabled;
        rule.ActiveVersionId = enabled ? v.Id : rule.ActiveVersionId;
        _audit.Record(enabled ? "RULE_ENABLE" : "RULE_DISABLE", "AmlRule", $"{rule.Code} v{v.Version}", null, null, reason);
        await _db.SaveChangesAsync(ct);
        return ToDto(v);
    }

    public async Task<RuleRunResult> RunAsync(long ruleId, RuleWindowRequest r, CancellationToken ct)
    {
        ValidateWindow(r);
        var v = await VersionAsync(ruleId, null, x => x.Status == ConfigStatus.Active, ct);
        var now = _clock.UtcNow;
        if (!(v.EffectiveFrom <= now && (v.EffectiveTo is null || v.EffectiveTo > now))) throw new ConflictException("The active version is outside its effective period.");
        _audit.Record("RULE_RUN_MANUAL", "AmlRule", $"{ruleId} v{v.Version}", null, new { r.From, r.To });
        await _db.SaveChangesAsync(ct);
        return await _exec.RunAsync(v.Id, r.From, r.To, new RuleRunOptions("Manual", true, _user.Username), ct);
    }

    /// <summary>Historical backtest: dry-run unless alert creation is explicitly requested (permission required; alerts tagged as backtest).</summary>
    public async Task<RuleRunResult> BacktestAsync(long ruleId, RuleWindowRequest r, CancellationToken ct)
    {
        ValidateWindow(r);
        if (r.CreateAlerts) Guard.Require(_user, Perm.RulesBacktestAlerts);
        var v = await VersionAsync(ruleId, r.VersionId, null, ct);
        var result = await _exec.RunAsync(v.Id, r.From, r.To, new RuleRunOptions("Backtest", r.CreateAlerts, _user.Username), ct);
        _audit.Record("RULE_BACKTEST", "AmlRule", $"{ruleId} v{v.Version}", null, new { r.From, r.To, r.CreateAlerts, result.ExecutionId, result.Matches, result.AlertsCreated });
        await _db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<List<AmlRuleExecution>> ExecutionsAsync(long? ruleId, int take, CancellationToken ct)
    {
        var q = _db.Set<AmlRuleExecution>().AsNoTracking();
        if (ruleId is not null) q = q.Where(e => e.RuleId == ruleId);
        return await q.OrderByDescending(e => e.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    /// <summary>Above/below-the-line tuning: compares current threshold with a candidate and samples below-the-line events for review.</summary>
    public async Task<TuningRun> TuneAsync(long ruleId, TuningRequest r, CancellationToken ct)
    {
        Guard.Require(_user, Perm.TuningRun);
        ValidateWindow(new RuleWindowRequest { From = r.From, To = r.To });
        if (r.RunType is not ("ATL" or "BTL")) throw new ValidationException("runType", "runType must be ATL or BTL.");
        if (r.SamplePercent is <= 0 or > 100) throw new ValidationException("samplePercent", "samplePercent must be within (0,100].");
        var v = await VersionAsync(ruleId, null, x => x.Status is ConfigStatus.Active or ConfigStatus.Approved or ConfigStatus.Draft, ct);
        var node = JsonNode.Parse(v.DefinitionJson)!;
        var target = Navigate(node, r.ThresholdPath) ?? throw new ValidationException("thresholdPath", $"Path '{r.ThresholdPath}' not found in the rule definition.");
        var parent = target.Parent!;
        var key = r.ThresholdPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
        if (parent is JsonObject o) o[key] = r.CandidateValue; else if (parent is JsonArray a) a[int.Parse(key)] = r.CandidateValue;
        var candidateJson = node.ToJsonString();
        var errs = await ValidateDefinitionAsync(candidateJson, ct);
        ThrowIfInvalid(errs);
        var current = await _exec.RunAsync(v.Id, r.From, r.To, new RuleRunOptions("Tuning", false, _user.Username, SampleLimit: 5000), ct);
        var candidate = await _exec.RunAsync(v.Id, r.From, r.To, new RuleRunOptions("Tuning", false, _user.Username, candidateJson, SampleLimit: 5000), ct);
        var currentKeys = current.MatchSummaries.Select(m => JsonDocument.Parse(JsonSerializer.Serialize(m)).RootElement.GetProperty("trigger").GetString()).ToHashSet();
        var delta = candidate.MatchSummaries.Where(m => !currentKeys.Contains(JsonDocument.Parse(JsonSerializer.Serialize(m)).RootElement.GetProperty("trigger").GetString())).ToList();
        var sampleSize = (int)Math.Ceiling(delta.Count * r.SamplePercent / 100m);
        var rnd = new Random(42);
        var sample = delta.OrderBy(_ => rnd.Next()).Take(sampleSize).ToList();
        var run = new TuningRun
        {
            TenantId = _user.TenantId, RuleId = ruleId, RuleVersionId = v.Id, RunType = r.RunType, ParametersJson = JsonSerializer.Serialize(r), PeriodStart = r.From, PeriodEnd = r.To,
            CurrentMatches = current.Matches, CandidateMatches = candidate.Matches, SampleJson = JsonSerializer.Serialize(sample),
            ResultJson = JsonSerializer.Serialize(new { currentExecution = current.ExecutionId, candidateExecution = candidate.ExecutionId, additionalEvents = delta.Count, sampleSize }),
            CreatedBy = _user.Username, CreatedAt = _clock.UtcNow
        };
        _db.Set<TuningRun>().Add(run);
        _audit.Record("RULE_TUNING_RUN", "AmlRule", $"{ruleId} v{v.Version}", null, new { r.RunType, r.CandidateValue, currentMatches = current.Matches, candidateMatches = candidate.Matches, sampleSize });
        await _db.SaveChangesAsync(ct);
        return run;
    }

    private static JsonNode? Navigate(JsonNode root, string path)
    {
        JsonNode? cur = root;
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur switch
            {
                JsonObject o => o[seg],
                JsonArray a when int.TryParse(seg, out var i) && i < a.Count => a[i],
                _ => null
            };
            if (cur is null) return null;
        }
        return cur;
    }

    public async Task<List<TuningRun>> TuningRunsAsync(long ruleId, CancellationToken ct) =>
        await _db.Set<TuningRun>().AsNoTracking().Where(t => t.RuleId == ruleId).OrderByDescending(t => t.Id).ToListAsync(ct);
}
