using AML.Application.Common;
using AML.Application.Fields;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Risk;

public sealed class MethodologyInput
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string AggregationMethod { get; set; } = AggregationMethods.WeightedAverage;
    public decimal? Cap { get; set; }
    public int ScorePrecision { get; set; } = 2;
    public string? AppliesToCustomerTypes { get; set; }
    public string? ApprovalRequiredLevels { get; set; }
    public string? CategoryMatrixJson { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? Owner { get; set; }
    public string? Rationale { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class FactorInputDto
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string CategoryCode { get; set; } = "";
    public string? Description { get; set; }
    public string DataSource { get; set; } = "";
    public string FactorType { get; set; } = "Categorical";
    public decimal Weight { get; set; }
    public string? ApplicableCustomerTypes { get; set; }
    public string MissingDataHandling { get; set; } = "AssignScore";
    public decimal? MissingDataScore { get; set; }
    public int? FreshnessDays { get; set; }
    public bool IsActive { get; set; } = true;
    public List<MappingInput>? Mappings { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class MappingInput
{
    public string MatchType { get; set; } = "Equals";
    public string? Value { get; set; }
    public decimal? RangeMin { get; set; }
    public decimal? RangeMax { get; set; }
    public decimal Score { get; set; }
    public int Priority { get; set; }
    public string? Label { get; set; }
}

public sealed class ThresholdInput
{
    public string Level { get; set; } = "";
    public decimal MinScore { get; set; }
    public decimal MaxScore { get; set; }
    public int ReviewMonths { get; set; } = 12;
}

public sealed class OverrideInput
{
    public string Code { get; set; } = "";
    public string ConditionDataSource { get; set; } = "";
    public string ConditionOperator { get; set; } = "eq";
    public string ConditionValue { get; set; } = "true";
    public string MinimumLevel { get; set; } = RiskLevels.High;
    public string Reason { get; set; } = "";
    public int Priority { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsSanctionsWorkflow { get; set; }
}

public sealed record MappingDto(long Id, string MatchType, string? Value, decimal? RangeMin, decimal? RangeMax, decimal Score, int Priority, string? Label);
public sealed record FactorDto(long Id, long MethodologyId, string Code, string Name, string CategoryCode, string? Description, string DataSource, string FactorType,
    decimal Weight, string ApplicableCustomerTypes, string MissingDataHandling, decimal? MissingDataScore, int? FreshnessDays, bool IsActive, int Version,
    string RowVersion, IReadOnlyList<MappingDto> Mappings);
public sealed record ThresholdDto(long Id, string Level, decimal MinScore, decimal MaxScore, int ReviewMonths, bool IsSample);
public sealed record OverrideDto(long Id, string Code, string ConditionDataSource, string ConditionOperator, string ConditionValue, string MinimumLevel, string Reason,
    int Priority, bool RequiresApproval, bool IsSanctionsWorkflow, bool IsActive);
public sealed record FloorDto(long Id, string CategoryCode, string TriggerLevel, string MinimumLevel);
public sealed record MethodologyDto(long Id, string Code, string Name, int Version, string Status, string AggregationMethod, decimal? Cap, int ScorePrecision,
    string AppliesToCustomerTypes, string ApprovalRequiredLevels, string? CategoryMatrixJson, DateTime EffectiveFrom, DateTime? EffectiveTo, string? Owner, string? Rationale,
    string? CreatedBy, DateTime CreatedAt, string? SubmittedBy, string? ApprovedBy, DateTime? ApprovedAt, long? PreviousVersionId, bool IsSample, string RowVersion,
    IReadOnlyList<FactorDto> Factors, IReadOnlyList<ThresholdDto> Thresholds, IReadOnlyList<OverrideDto> Overrides, IReadOnlyList<FloorDto> Floors);

public sealed class RiskConfigService
{
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly DependencyService _deps;
    private readonly RiskAssessmentService _assess;

    public RiskConfigService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, DependencyService deps, RiskAssessmentService assess)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _deps = deps; _assess = assess;
    }

    public static MethodologyDto ToDto(RiskMethodology m) => new(m.Id, m.Code, m.Name, m.Version, m.Status, m.AggregationMethod, m.Cap, m.ScorePrecision,
        m.AppliesToCustomerTypes, m.ApprovalRequiredLevels, m.CategoryMatrixJson, m.EffectiveFrom, m.EffectiveTo, m.Owner, m.Rationale, m.CreatedBy, m.CreatedAt,
        m.SubmittedBy, m.ApprovedBy, m.ApprovedAt, m.PreviousVersionId, m.IsSample, Convert.ToBase64String(m.RowVersion),
        m.Factors.OrderBy(f => f.CategoryCode).ThenBy(f => f.Code).Select(ToDto).ToList(),
        m.Thresholds.OrderBy(t => t.MinScore).Select(t => new ThresholdDto(t.Id, t.Level, t.MinScore, t.MaxScore, t.ReviewMonths, t.IsSample)).ToList(),
        m.Overrides.OrderByDescending(o => o.Priority).Select(o => new OverrideDto(o.Id, o.Code, o.ConditionDataSource, o.ConditionOperator, o.ConditionValue, o.MinimumLevel,
            o.Reason, o.Priority, o.RequiresApproval, o.IsSanctionsWorkflow, o.IsActive)).ToList(),
        m.Floors.Select(f => new FloorDto(f.Id, f.CategoryCode, f.TriggerLevel, f.MinimumLevel)).ToList());

    public static FactorDto ToDto(RiskFactorDefinition f) => new(f.Id, f.MethodologyId, f.Code, f.Name, f.CategoryCode, f.Description, f.DataSource, f.FactorType, f.Weight,
        f.ApplicableCustomerTypes, f.MissingDataHandling, f.MissingDataScore, f.FreshnessDays, f.IsActive, f.Version, Convert.ToBase64String(f.RowVersion),
        f.Mappings.OrderByDescending(x => x.Priority).ThenBy(x => x.RangeMin).Select(x => new MappingDto(x.Id, x.MatchType, x.Value, x.RangeMin, x.RangeMax, x.Score, x.Priority, x.Label)).ToList());

    public async Task<List<MethodologyDto>> ListAsync(CancellationToken ct)
    {
        var list = await _db.Set<RiskMethodology>().AsNoTracking().OrderBy(m => m.Code).ThenByDescending(m => m.Version).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<MethodologyDto> GetAsync(long id, CancellationToken ct) => ToDto(await TrackedGraphAsync(id, ct, tracking: false));

    public async Task<List<MethodologyDto>> HistoryAsync(string code, CancellationToken ct)
    {
        var list = await _db.Set<RiskMethodology>().AsNoTracking().Where(m => m.Code == code).OrderByDescending(m => m.Version).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    private async Task<RiskMethodology> TrackedGraphAsync(long id, CancellationToken ct, bool tracking = true)
    {
        var q = _db.Set<RiskMethodology>().Include(x => x.Factors).ThenInclude(f => f.Mappings).Include(x => x.Thresholds).Include(x => x.Overrides).Include(x => x.Floors).AsSplitQuery();
        if (!tracking) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Methodology", id);
    }

    public async Task<MethodologyDto> CreateAsync(MethodologyInput i, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Code) || string.IsNullOrWhiteSpace(i.Name)) throw new ValidationException("code", "Code and name are required.");
        if (!AggregationMethods.All.Contains(i.AggregationMethod)) throw new ValidationException("aggregationMethod", $"Unknown aggregation method '{i.AggregationMethod}'.");
        var code = i.Code.Trim().ToUpperInvariant();
        var max = await _db.Set<RiskMethodology>().Where(m => m.Code == code).Select(m => (int?)m.Version).MaxAsync(ct) ?? 0;
        if (max > 0) throw new ConflictException($"Methodology {code} exists; create a new version from the latest version instead.");
        var m = new RiskMethodology { TenantId = _user.TenantId, Code = code, Version = 1, Status = ConfigStatus.Draft, CreatedBy = _user.Username, CreatedAt = _clock.UtcNow };
        Apply(m, i);
        _db.Set<RiskMethodology>().Add(m);
        _audit.Record("RISK_METHODOLOGY_CREATE", "RiskMethodology", code, null, new { code, i.Name, i.AggregationMethod });
        await _db.SaveChangesAsync(ct);
        return await GetAsync(m.Id, ct);
    }

    private void Apply(RiskMethodology m, MethodologyInput i)
    {
        m.Name = i.Name;
        m.AggregationMethod = i.AggregationMethod;
        m.Cap = i.Cap;
        m.ScorePrecision = i.ScorePrecision;
        m.AppliesToCustomerTypes = string.Join(",", RiskScoringEngine.Split(i.AppliesToCustomerTypes));
        m.ApprovalRequiredLevels = string.Join(",", RiskScoringEngine.Split(i.ApprovalRequiredLevels));
        m.CategoryMatrixJson = i.CategoryMatrixJson;
        m.EffectiveFrom = i.EffectiveFrom ?? m.EffectiveFrom.Max(_clock.UtcNow);
        m.EffectiveTo = i.EffectiveTo;
        m.Owner = i.Owner;
        m.Rationale = i.Rationale;
    }

    private static void EnsureDraft(RiskMethodology m)
    {
        if (m.Status != ConfigStatus.Draft) throw new ConflictException($"Methodology {m.Code} v{m.Version} is {m.Status}; only drafts can be edited. Create a new version.");
    }

    public async Task<MethodologyDto> UpdateAsync(long id, MethodologyInput i, CancellationToken ct)
    {
        var m = await _db.Set<RiskMethodology>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Methodology", id);
        EnsureDraft(m);
        _db.Entry(m).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(i.RowVersion, "methodology");
        var old = new { m.Name, m.AggregationMethod, m.Cap, m.EffectiveFrom, m.ApprovalRequiredLevels };
        Apply(m, i);
        _audit.Record("RISK_METHODOLOGY_UPDATE", "RiskMethodology", $"{m.Code} v{m.Version}", old, new { m.Name, m.AggregationMethod, m.Cap, m.EffectiveFrom, m.ApprovalRequiredLevels });
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Clones a methodology version (factors, mappings, thresholds, overrides, floors) into a new draft version.</summary>
    public async Task<MethodologyDto> NewVersionAsync(long id, CancellationToken ct) => ToDto(await CloneAsync(id, ct));

    private async Task<RiskMethodology> CloneAsync(long id, CancellationToken ct)
    {
        var src = await TrackedGraphAsync(id, ct, tracking: false);
        var existingDraft = await _db.Set<RiskMethodology>().Where(m => m.Code == src.Code && (m.Status == ConfigStatus.Draft || m.Status == ConfigStatus.PendingApproval)).FirstOrDefaultAsync(ct);
        if (existingDraft is not null) throw new ConflictException($"Methodology {src.Code} already has draft/pending version v{existingDraft.Version} (id {existingDraft.Id}).", new { draftId = existingDraft.Id });
        var max = await _db.Set<RiskMethodology>().Where(m => m.Code == src.Code).MaxAsync(m => m.Version, ct);
        var now = _clock.UtcNow;
        var m = new RiskMethodology
        {
            TenantId = src.TenantId, Code = src.Code, Name = src.Name, Version = max + 1, Status = ConfigStatus.Draft, AggregationMethod = src.AggregationMethod, Cap = src.Cap,
            ScorePrecision = src.ScorePrecision, AppliesToCustomerTypes = src.AppliesToCustomerTypes, ApprovalRequiredLevels = src.ApprovalRequiredLevels,
            CategoryMatrixJson = src.CategoryMatrixJson, EffectiveFrom = now > src.EffectiveFrom ? now : src.EffectiveFrom, Owner = src.Owner, Rationale = null,
            CreatedBy = _user.Username, CreatedAt = now, PreviousVersionId = src.Id, IsSample = src.IsSample
        };
        foreach (var f in src.Factors)
        {
            var nf = new RiskFactorDefinition
            {
                TenantId = f.TenantId, Code = f.Code, Name = f.Name, CategoryCode = f.CategoryCode, Description = f.Description, DataSource = f.DataSource,
                FactorType = f.FactorType, Weight = f.Weight, ScoringMode = f.ScoringMode, ApplicableCustomerTypes = f.ApplicableCustomerTypes,
                MissingDataHandling = f.MissingDataHandling, MissingDataScore = f.MissingDataScore, FreshnessDays = f.FreshnessDays, IsActive = f.IsActive,
                EffectiveFrom = f.EffectiveFrom, EffectiveTo = f.EffectiveTo, Version = f.Version
            };
            foreach (var mp in f.Mappings)
                nf.Mappings.Add(new RiskFactorScoreMapping
                {
                    TenantId = mp.TenantId, MatchType = mp.MatchType, Value = mp.Value, RangeMin = mp.RangeMin, RangeMax = mp.RangeMax, Score = mp.Score, Priority = mp.Priority,
                    EffectiveFrom = mp.EffectiveFrom, EffectiveTo = mp.EffectiveTo, Label = mp.Label
                });
            m.Factors.Add(nf);
        }
        foreach (var t in src.Thresholds)
            m.Thresholds.Add(new RiskThreshold { TenantId = t.TenantId, Level = t.Level, MinScore = t.MinScore, MaxScore = t.MaxScore, ReviewMonths = t.ReviewMonths, Rank = t.Rank, IsSample = t.IsSample, EffectiveFrom = t.EffectiveFrom, EffectiveTo = t.EffectiveTo });
        foreach (var o in src.Overrides)
            m.Overrides.Add(new RiskOverridePolicy
            {
                TenantId = o.TenantId, Code = o.Code, ConditionDataSource = o.ConditionDataSource, ConditionOperator = o.ConditionOperator, ConditionValue = o.ConditionValue,
                MinimumLevel = o.MinimumLevel, Reason = o.Reason, Priority = o.Priority, RequiresApproval = o.RequiresApproval, IsSanctionsWorkflow = o.IsSanctionsWorkflow,
                IsActive = o.IsActive, EffectiveFrom = o.EffectiveFrom, EffectiveTo = o.EffectiveTo
            });
        foreach (var fl in src.Floors)
            m.Floors.Add(new RiskCategoryFloor { TenantId = fl.TenantId, CategoryCode = fl.CategoryCode, TriggerLevel = fl.TriggerLevel, MinimumLevel = fl.MinimumLevel });
        _db.Set<RiskMethodology>().Add(m);
        _audit.Record("RISK_METHODOLOGY_NEW_VERSION", "RiskMethodology", $"{m.Code} v{m.Version}", null, new { from = $"v{src.Version}" });
        await _db.SaveChangesAsync(ct);
        return m;
    }

    // ---------- factors ----------
    public async Task<FactorDto> AddFactorAsync(long methodologyId, FactorInputDto i, CancellationToken ct)
    {
        var m = await _db.Set<RiskMethodology>().FirstOrDefaultAsync(x => x.Id == methodologyId, ct) ?? throw new NotFoundException("Methodology", methodologyId);
        EnsureDraft(m);
        await ValidateFactorAsync(i, ct);
        if (await _db.Set<RiskFactorDefinition>().AnyAsync(f => f.MethodologyId == methodologyId && f.Code == i.Code, ct))
            throw new ConflictException($"Factor {i.Code} already exists in this methodology version.");
        var f = new RiskFactorDefinition { TenantId = m.TenantId, MethodologyId = methodologyId, EffectiveFrom = m.EffectiveFrom, Version = 1 };
        ApplyFactor(f, i);
        _db.Set<RiskFactorDefinition>().Add(f);
        _audit.Record("RISK_FACTOR_ADD", "RiskMethodology", $"{m.Code} v{m.Version}", null, new { i.Code, i.DataSource, i.Weight });
        await _db.SaveChangesAsync(ct);
        return ToDto(await _db.Set<RiskFactorDefinition>().AsNoTracking().Include(x => x.Mappings).FirstAsync(x => x.Id == f.Id, ct));
    }

    /// <summary>
    /// Edits a factor. Factors of a draft are edited in place (factor version incremented). Editing a factor of a
    /// published methodology creates a new draft methodology version, so historical assessments keep their configuration.
    /// </summary>
    public async Task<FactorDto> UpdateFactorAsync(long factorId, FactorInputDto i, CancellationToken ct)
    {
        var f = await _db.Set<RiskFactorDefinition>().Include(x => x.Mappings).FirstOrDefaultAsync(x => x.Id == factorId, ct) ?? throw new NotFoundException("Factor", factorId);
        var m = await _db.Set<RiskMethodology>().FirstAsync(x => x.Id == f.MethodologyId, ct);
        _db.Entry(f).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(i.RowVersion, "factor");
        await ValidateFactorAsync(i, ct);
        if (m.Status != ConfigStatus.Draft)
        {
            // concurrency check against the published factor before branching
            _db.Entry(f).State = EntityState.Unchanged;
            var current = await _db.Set<RiskFactorDefinition>().AsNoTracking().FirstAsync(x => x.Id == factorId, ct);
            if (!current.RowVersion.SequenceEqual(Guard.RowVersion(i.RowVersion, "factor")))
                throw new ConflictException("The factor was modified by another user. Reload and retry.");
            var draft = await _db.Set<RiskMethodology>().FirstOrDefaultAsync(x => x.Code == m.Code && x.Status == ConfigStatus.Draft, ct) ?? await CloneAsync(m.Id, ct);
            var df = await _db.Set<RiskFactorDefinition>().Include(x => x.Mappings).FirstAsync(x => x.MethodologyId == draft.Id && x.Code == f.Code, ct);
            var old = new { df.Weight, df.DataSource, df.Version };
            ApplyFactor(df, i);
            df.Version = f.Version + 1;
            _audit.Record("RISK_FACTOR_EDIT_NEW_VERSION", "RiskMethodology", $"{draft.Code} v{draft.Version}", old, new { i.Code, i.Weight, i.DataSource, df.Version });
            await _db.SaveChangesAsync(ct);
            return ToDto(await _db.Set<RiskFactorDefinition>().AsNoTracking().Include(x => x.Mappings).FirstAsync(x => x.Id == df.Id, ct));
        }
        var before = new { f.Weight, f.DataSource, f.Version, mappings = f.Mappings.Count };
        ApplyFactor(f, i);
        f.Version++;
        _audit.Record("RISK_FACTOR_UPDATE", "RiskMethodology", $"{m.Code} v{m.Version}", before, new { i.Code, i.Weight, i.DataSource, f.Version });
        await _db.SaveChangesAsync(ct);
        return ToDto(await _db.Set<RiskFactorDefinition>().AsNoTracking().Include(x => x.Mappings).FirstAsync(x => x.Id == f.Id, ct));
    }

    public async Task DeleteFactorAsync(long factorId, CancellationToken ct)
    {
        var f = await _db.Set<RiskFactorDefinition>().Include(x => x.Mappings).FirstOrDefaultAsync(x => x.Id == factorId, ct) ?? throw new NotFoundException("Factor", factorId);
        var m = await _db.Set<RiskMethodology>().FirstAsync(x => x.Id == f.MethodologyId, ct);
        EnsureDraft(m);
        _db.Set<RiskFactorScoreMapping>().RemoveRange(f.Mappings);
        _db.Set<RiskFactorDefinition>().Remove(f);
        _audit.Record("RISK_FACTOR_DELETE", "RiskMethodology", $"{m.Code} v{m.Version}", new { f.Code }, null);
        await _db.SaveChangesAsync(ct);
    }

    private async Task ValidateFactorAsync(FactorInputDto i, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string k, string msg) { if (!errors.TryGetValue(k, out var l)) errors[k] = l = new(); l.Add(msg); }
        if (string.IsNullOrWhiteSpace(i.Code)) Add("code", "Factor code is required.");
        if (string.IsNullOrWhiteSpace(i.Name)) Add("name", "Factor name is required.");
        if (!await _db.Set<RiskFactorCategory>().AnyAsync(c => c.Code == i.CategoryCode, ct)) Add("categoryCode", $"Unknown category '{i.CategoryCode}'.");
        var published = (await _deps.PublishedFieldTypesAsync(ct)).Keys.ToList();
        if (!RiskDataSourceProvider.IsKnown(i.DataSource, published)) Add("dataSource", $"Unknown data source '{i.DataSource}' (not in the typed data-source registry or not a published field).");
        if (i.Weight < 0) Add("weight", "Weight cannot be negative.");
        if (i.MissingDataHandling is not ("AssignScore" or "ExcludeWithWarning")) Add("missingDataHandling", "Allowed: AssignScore, ExcludeWithWarning.");
        if (i.MissingDataScore is < 0 or > 100) Add("missingDataScore", "Must be within 0-100.");
        foreach (var mp in i.Mappings ?? new())
        {
            if (mp.MatchType is not ("Equals" or "In" or "Range" or "Boolean" or "Default")) Add("mappings", $"Unknown match type '{mp.MatchType}'.");
            if (mp.Score is < 0 or > 100) Add("mappings", $"Mapping score {mp.Score} is outside the normalized 0-100 range.");
            if (mp.MatchType == "Range" && mp.RangeMin > mp.RangeMax) Add("mappings", "Range min must not exceed max.");
        }
        if (i.Mappings is not null)
        {
            var tmp = new RiskFactorDefinition { Code = i.Code };
            tmp.Mappings.AddRange(i.Mappings.Select(x => new RiskFactorScoreMapping { MatchType = x.MatchType, Value = x.Value, RangeMin = x.RangeMin, RangeMax = x.RangeMax, Score = x.Score, Priority = x.Priority }));
            foreach (var e in RiskMethodologyValidator.ValidateMappings(tmp)) Add("mappings", e);
        }
        ValidationException.ThrowIf(errors);
    }

    private void ApplyFactor(RiskFactorDefinition f, FactorInputDto i)
    {
        f.Code = i.Code.Trim().ToUpperInvariant();
        f.Name = i.Name;
        f.CategoryCode = i.CategoryCode;
        f.Description = i.Description;
        f.DataSource = i.DataSource;
        f.FactorType = i.FactorType;
        f.Weight = i.Weight;
        f.ApplicableCustomerTypes = string.Join(",", RiskScoringEngine.Split(i.ApplicableCustomerTypes));
        f.MissingDataHandling = i.MissingDataHandling;
        f.MissingDataScore = i.MissingDataScore;
        f.FreshnessDays = i.FreshnessDays;
        f.IsActive = i.IsActive;
        if (i.Mappings is not null)
        {
            _db.Set<RiskFactorScoreMapping>().RemoveRange(f.Mappings);
            f.Mappings.Clear();
            foreach (var mp in i.Mappings)
                f.Mappings.Add(new RiskFactorScoreMapping
                {
                    TenantId = _user.TenantId, MatchType = mp.MatchType, Value = mp.Value, RangeMin = mp.RangeMin, RangeMax = mp.RangeMax, Score = mp.Score,
                    Priority = mp.Priority, Label = mp.Label, EffectiveFrom = DateTime.SpecifyKind(new DateTime(2000, 1, 1), DateTimeKind.Utc)
                });
        }
    }

    // ---------- thresholds / overrides / floors ----------
    public async Task<MethodologyDto> SetThresholdsAsync(long methodologyId, List<ThresholdInput> thresholds, CancellationToken ct)
    {
        var m = await TrackedGraphAsync(methodologyId, ct);
        EnsureDraft(m);
        _db.Set<RiskThreshold>().RemoveRange(m.Thresholds);
        m.Thresholds.Clear();
        var rank = 0;
        foreach (var t in thresholds.OrderBy(t => t.MinScore))
            m.Thresholds.Add(new RiskThreshold { TenantId = m.TenantId, Level = t.Level, MinScore = t.MinScore, MaxScore = t.MaxScore, ReviewMonths = t.ReviewMonths, Rank = rank++, EffectiveFrom = m.EffectiveFrom });
        var errs = RiskMethodologyValidator.Validate(m).Where(e => e.StartsWith("Threshold")).ToList();
        if (errs.Count > 0) throw new ValidationException(new Dictionary<string, string[]> { ["thresholds"] = errs.ToArray() }, string.Join(" ", errs));
        _audit.Record("RISK_THRESHOLDS_SET", "RiskMethodology", $"{m.Code} v{m.Version}", null, thresholds);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(methodologyId, ct);
    }

    public async Task<MethodologyDto> AddOverrideAsync(long methodologyId, OverrideInput o, CancellationToken ct)
    {
        var m = await TrackedGraphAsync(methodologyId, ct);
        EnsureDraft(m);
        var published = (await _deps.PublishedFieldTypesAsync(ct)).Keys.ToList();
        if (!RiskDataSourceProvider.IsKnown(o.ConditionDataSource, published)) throw new ValidationException("conditionDataSource", $"Unknown data source '{o.ConditionDataSource}'.");
        if (string.IsNullOrWhiteSpace(o.Reason)) throw new ValidationException("reason", "Override reason is required.");
        m.Overrides.Add(new RiskOverridePolicy
        {
            TenantId = m.TenantId, Code = o.Code, ConditionDataSource = o.ConditionDataSource, ConditionOperator = o.ConditionOperator, ConditionValue = o.ConditionValue,
            MinimumLevel = o.MinimumLevel, Reason = o.Reason, Priority = o.Priority, RequiresApproval = o.RequiresApproval, IsSanctionsWorkflow = o.IsSanctionsWorkflow,
            EffectiveFrom = m.EffectiveFrom
        });
        _audit.Record("RISK_OVERRIDE_POLICY_ADD", "RiskMethodology", $"{m.Code} v{m.Version}", null, o);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(methodologyId, ct);
    }

    public async Task<MethodologyDto> RemoveOverrideAsync(long methodologyId, long overrideId, CancellationToken ct)
    {
        var m = await TrackedGraphAsync(methodologyId, ct);
        EnsureDraft(m);
        var o = m.Overrides.FirstOrDefault(x => x.Id == overrideId) ?? throw new NotFoundException("Override", overrideId);
        _db.Set<RiskOverridePolicy>().Remove(o);
        _audit.Record("RISK_OVERRIDE_POLICY_REMOVE", "RiskMethodology", $"{m.Code} v{m.Version}", new { o.Code }, null);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(methodologyId, ct);
    }

    public async Task<MethodologyDto> AddFloorAsync(long methodologyId, string category, string triggerLevel, string minimumLevel, CancellationToken ct)
    {
        var m = await TrackedGraphAsync(methodologyId, ct);
        EnsureDraft(m);
        m.Floors.Add(new RiskCategoryFloor { TenantId = m.TenantId, CategoryCode = category, TriggerLevel = triggerLevel, MinimumLevel = minimumLevel });
        _audit.Record("RISK_FLOOR_ADD", "RiskMethodology", $"{m.Code} v{m.Version}", null, new { category, triggerLevel, minimumLevel });
        await _db.SaveChangesAsync(ct);
        return await GetAsync(methodologyId, ct);
    }

    // ---------- lifecycle ----------
    public async Task<List<string>> ValidateAsync(long id, CancellationToken ct)
    {
        var m = await TrackedGraphAsync(id, ct, tracking: false);
        var errors = RiskMethodologyValidator.Validate(m);
        var published = (await _deps.PublishedFieldTypesAsync(ct)).Keys.ToList();
        foreach (var f in m.Factors.Where(f => f.IsActive))
            if (!RiskDataSourceProvider.IsKnown(f.DataSource, published)) errors.Add($"Factor {f.Code}: data source '{f.DataSource}' is not in the registry or references an unpublished field.");
        foreach (var o in m.Overrides)
            if (!RiskDataSourceProvider.IsKnown(o.ConditionDataSource, published)) errors.Add($"Override {o.Code}: unknown data source '{o.ConditionDataSource}'.");
        var cats = await _db.Set<RiskFactorCategory>().Select(c => c.Code).ToListAsync(ct);
        foreach (var fl in m.Floors.Where(fl => !cats.Contains(fl.CategoryCode))) errors.Add($"Floor references unknown category {fl.CategoryCode}.");
        return errors;
    }

    public async Task<MethodologyDto> SubmitAsync(long id, string? rationale, CancellationToken ct)
    {
        var m = await _db.Set<RiskMethodology>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Methodology", id);
        EnsureDraft(m);
        var errors = await ValidateAsync(id, ct);
        if (errors.Count > 0) throw new ValidationException(new Dictionary<string, string[]> { ["methodology"] = errors.ToArray() }, "Methodology validation failed: " + string.Join(" ", errors));
        if (!string.IsNullOrWhiteSpace(rationale)) m.Rationale = rationale;
        if (string.IsNullOrWhiteSpace(m.Rationale)) throw new ValidationException("rationale", "A change rationale is required before submission for approval.");
        m.Status = ConfigStatus.PendingApproval;
        m.SubmittedBy = _user.Username;
        _audit.Record("RISK_METHODOLOGY_SUBMIT", "RiskMethodology", $"{m.Code} v{m.Version}", null, new { m.Rationale });
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Four-eyes approval/publication. The approver must differ from the author and submitter.</summary>
    public async Task<MethodologyDto> ApproveAsync(long id, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RiskApprove);
        var m = await _db.Set<RiskMethodology>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Methodology", id);
        if (m.Status != ConfigStatus.PendingApproval) throw new ConflictException("Methodology is not pending approval.");
        Guard.FourEyes(m.CreatedBy ?? "", _user.Username, "Risk methodology approval");
        Guard.FourEyes(m.SubmittedBy ?? "", _user.Username, "Risk methodology approval");
        var errors = await ValidateAsync(id, ct);
        if (errors.Count > 0) throw new ValidationException(new Dictionary<string, string[]> { ["methodology"] = errors.ToArray() }, "Methodology validation failed.");
        // Close overlapping approved versions of the same code (no overlap: older version ends where the new one starts).
        var others = await _db.Set<RiskMethodology>().Where(x => x.Code == m.Code && x.Id != m.Id && x.Status == ConfigStatus.Approved).ToListAsync(ct);
        foreach (var o in others)
        {
            if (o.EffectiveFrom >= m.EffectiveFrom) { o.Status = ConfigStatus.Superseded; o.EffectiveTo = o.EffectiveFrom; }
            else if (o.EffectiveTo is null || o.EffectiveTo > m.EffectiveFrom) o.EffectiveTo = m.EffectiveFrom;
        }
        m.Status = ConfigStatus.Approved;
        m.ApprovedBy = _user.Username;
        m.ApprovedAt = _clock.UtcNow;
        _audit.Record("RISK_METHODOLOGY_APPROVE", "RiskMethodology", $"{m.Code} v{m.Version}", others.Select(o => new { o.Version, o.EffectiveTo }).ToList(),
            new { m.Version, m.EffectiveFrom, m.ApprovedBy, m.Rationale });
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<MethodologyDto> PublishAsync(long id, string? rationale, CancellationToken ct)
    {
        var m = await _db.Set<RiskMethodology>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Methodology", id);
        return m.Status == ConfigStatus.Draft ? await SubmitAsync(id, rationale, ct) : await ApproveAsync(id, ct);
    }

    public async Task<MethodologyDto> RejectAsync(long id, string reason, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RiskApprove);
        var m = await _db.Set<RiskMethodology>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Methodology", id);
        if (m.Status != ConfigStatus.PendingApproval) throw new ConflictException("Methodology is not pending approval.");
        m.Status = ConfigStatus.Draft;
        _audit.Record("RISK_METHODOLOGY_REJECT", "RiskMethodology", $"{m.Code} v{m.Version}", null, null, reason);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public Task<SimulationResultDto> SimulateAsync(long id, SimulationRequest r, CancellationToken ct) =>
        _assess.SimulateAsync(id, r.CustomerId, r.CustomerType, r.Inputs, r.AsOf, ct);

    public async Task<List<RiskFactorCategory>> CategoriesAsync(CancellationToken ct) => await _db.Set<RiskFactorCategory>().AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct);

    public async Task<RiskFactorCategory> AddCategoryAsync(string code, string name, string? nameAr, CancellationToken ct)
    {
        code = code.Trim().ToUpperInvariant();
        if (await _db.Set<RiskFactorCategory>().AnyAsync(c => c.Code == code, ct)) throw new ConflictException($"Category {code} exists.");
        var c = new RiskFactorCategory { TenantId = _user.TenantId, Code = code, Name = name, NameAr = nameAr ?? name, IsCustom = true };
        _db.Set<RiskFactorCategory>().Add(c);
        _audit.Record("RISK_CATEGORY_ADD", "RiskFactorCategory", code, null, new { name });
        await _db.SaveChangesAsync(ct);
        return c;
    }
}

public sealed class SimulationRequest
{
    public long? CustomerId { get; set; }
    public string? CustomerType { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement>? Inputs { get; set; }
    public DateTime? AsOf { get; set; }
}

internal static class DateExt
{
    public static DateTime Max(this DateTime a, DateTime b) => a > b ? a : b;
}
