using System.Text.Json;
using System.Text.RegularExpressions;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Rules;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Fields;

public sealed record FieldDefinitionDto(long Id, string Code, int Version, string Status, string LabelEn, string LabelAr, string? Description,
    string GroupCode, string DataType, string CustomerTypes, string RequiredForTypes, bool IsSearchable, bool IsFilterable, bool IsSensitive,
    bool IsEncrypted, string? ValidationJson, string? DefaultValue, int SortOrder, DateTime EffectiveFrom, DateTime? EffectiveTo,
    string? CreatedBy, string? PublishedBy, DateTime? PublishedAt, string? ApprovedBy, bool IsSample, string RowVersion);

public sealed class FieldDefinitionInput
{
    public string Code { get; set; } = "";
    public string LabelEn { get; set; } = "";
    public string? LabelAr { get; set; }
    public string? Description { get; set; }
    public string GroupCode { get; set; } = "";
    public string DataType { get; set; } = FieldDataTypes.ShortText;
    public string? CustomerTypes { get; set; }
    public string? RequiredForTypes { get; set; }
    public bool IsSearchable { get; set; }
    public bool IsFilterable { get; set; }
    public bool IsSensitive { get; set; }
    public bool IsEncrypted { get; set; }
    public string? ValidationJson { get; set; }
    public string? DefaultValue { get; set; }
    public int SortOrder { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public string? RowVersion { get; set; }
}

public sealed record FieldOptionDto(long Id, string FieldCode, string OptionCode, string LabelEn, string LabelAr, int SortOrder, bool IsActive);

public sealed record WhereUsedItem(string ConsumerType, string Reference, string Version, string Status, bool IsActiveConsumer, string Detail);

public sealed record FormFieldDto(string Code, int Version, string LabelEn, string LabelAr, string? Description, string GroupCode, string DataType,
    bool Required, bool IsSearchable, bool IsFilterable, bool IsSensitive, string? ValidationJson, string? DefaultValue, int SortOrder,
    IReadOnlyList<FieldOptionDto> Options);

public sealed record FormGroupDto(string Code, string LabelEn, string LabelAr, int SortOrder, IReadOnlyList<string> FieldCodes);

public sealed record FormMetadataDto(string CustomerType, string LayoutRole, int LayoutVersion, IReadOnlyList<FormGroupDto> Groups, IReadOnlyList<FormFieldDto> Fields);

public sealed class FieldConfigService
{
    private static readonly Regex CodePattern = new("^[A-Z][A-Z0-9_]{1,49}$", RegexOptions.Compiled);
    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly TenantSettingsService _settings;
    private readonly DependencyService _deps;

    public FieldConfigService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, TenantSettingsService settings, DependencyService deps)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _settings = settings; _deps = deps;
    }

    private IQueryable<CustomerFieldDefinition> Defs => _db.Set<CustomerFieldDefinition>();

    public static FieldDefinitionDto ToDto(CustomerFieldDefinition d) => new(d.Id, d.Code, d.Version, d.Status, d.LabelEn, d.LabelAr, d.Description,
        d.GroupCode, d.DataType, d.CustomerTypes, d.RequiredForTypes, d.IsSearchable, d.IsFilterable, d.IsSensitive, d.IsEncrypted, d.ValidationJson,
        d.DefaultValue, d.SortOrder, d.EffectiveFrom, d.EffectiveTo, d.CreatedBy, d.PublishedBy, d.PublishedAt, d.ApprovedBy, d.IsSample,
        Convert.ToBase64String(d.RowVersion));

    public async Task<List<FieldDefinitionDto>> ListAsync(string? code, string? status, CancellationToken ct)
    {
        var q = Defs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(code)) q = q.Where(d => d.Code == code);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(d => d.Status == status);
        var list = await q.OrderBy(d => d.GroupCode).ThenBy(d => d.SortOrder).ThenBy(d => d.Code).ThenByDescending(d => d.Version).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<FieldDefinitionDto> GetAsync(long id, CancellationToken ct) =>
        ToDto(await Defs.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Field", id));

    public async Task<FieldDefinitionDto> CreateAsync(FieldDefinitionInput input, CancellationToken ct)
    {
        input.Code = input.Code.Trim().ToUpperInvariant();
        await ValidateInputAsync(input, ct);
        if (await Defs.AnyAsync(d => d.Code == input.Code, ct))
            throw new ConflictException($"Field code '{input.Code}' already exists. Create a new version of the existing field instead.");
        var d = new CustomerFieldDefinition
        {
            TenantId = _user.TenantId, Code = input.Code, Version = 1, Status = ConfigStatus.Draft, CreatedBy = _user.Username, CreatedAt = _clock.UtcNow,
            EffectiveFrom = input.EffectiveFrom ?? _clock.UtcNow
        };
        Apply(d, input);
        Defs_Add(d);
        _audit.Record("FIELD_CREATE_DRAFT", "CustomerFieldDefinition", d.Code, null, ToAudit(d));
        await SaveUniqueAsync(ct, $"Field code '{input.Code}' already exists.");
        return ToDto(d);
    }

    private void Defs_Add(CustomerFieldDefinition d) => _db.Set<CustomerFieldDefinition>().Add(d);

    public async Task<FieldDefinitionDto> UpdateDraftAsync(long id, FieldDefinitionInput input, CancellationToken ct)
    {
        var d = await _db.Set<CustomerFieldDefinition>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Field", id);
        if (d.Status != ConfigStatus.Draft) throw new ConflictException($"Only draft versions can be edited (status {d.Status}). Create a new version.");
        _db.Entry(d).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(input.RowVersion, "field");
        input.Code = d.Code;
        await ValidateInputAsync(input, ct);
        var old = ToAudit(d);
        Apply(d, input);
        if (input.EffectiveFrom is { } ef) d.EffectiveFrom = ef;
        _audit.Record("FIELD_UPDATE_DRAFT", "CustomerFieldDefinition", d.Code, old, ToAudit(d));
        await _db.SaveChangesAsync(ct);
        return ToDto(d);
    }

    public async Task<FieldDefinitionDto> NewVersionAsync(long id, CancellationToken ct)
    {
        var src = await _db.Set<CustomerFieldDefinition>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Field", id);
        if (await Defs.AnyAsync(d => d.Code == src.Code && (d.Status == ConfigStatus.Draft || d.Status == ConfigStatus.PendingApproval), ct))
            throw new ConflictException($"Field {src.Code} already has a draft or pending version.");
        var max = await Defs.Where(d => d.Code == src.Code).MaxAsync(d => d.Version, ct);
        var d = new CustomerFieldDefinition
        {
            TenantId = src.TenantId, Code = src.Code, Version = max + 1, Status = ConfigStatus.Draft, LabelEn = src.LabelEn, LabelAr = src.LabelAr,
            Description = src.Description, GroupCode = src.GroupCode, DataType = src.DataType, CustomerTypes = src.CustomerTypes, RequiredForTypes = src.RequiredForTypes,
            IsSearchable = src.IsSearchable, IsFilterable = src.IsFilterable, IsSensitive = src.IsSensitive, IsEncrypted = src.IsEncrypted,
            ValidationJson = src.ValidationJson, OptionsSource = src.OptionsSource, DefaultValue = src.DefaultValue, SortOrder = src.SortOrder,
            EffectiveFrom = _clock.UtcNow, CreatedBy = _user.Username, CreatedAt = _clock.UtcNow, IsSample = src.IsSample
        };
        _db.Set<CustomerFieldDefinition>().Add(d);
        _audit.Record("FIELD_NEW_VERSION", "CustomerFieldDefinition", d.Code, null, new { d.Code, d.Version });
        await _db.SaveChangesAsync(ct);
        return ToDto(d);
    }

    public async Task<FieldDefinitionDto> PublishAsync(long id, string? rowVersion, CancellationToken ct)
    {
        var d = await _db.Set<CustomerFieldDefinition>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Field", id);
        if (rowVersion is not null) _db.Entry(d).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(rowVersion, "field");
        if (d.Status != ConfigStatus.Draft) throw new ConflictException($"Only drafts can be published (status {d.Status}).");
        await CheckPublishCompatibilityAsync(d, ct);
        var requireApproval = await _settings.BoolSettingAsync("Fields:RequireApproval", false, ct: ct);
        if (requireApproval)
        {
            d.Status = ConfigStatus.PendingApproval;
            _audit.Record("FIELD_SUBMIT_FOR_APPROVAL", "CustomerFieldDefinition", d.Code, null, new { d.Code, d.Version });
            await _db.SaveChangesAsync(ct);
            return ToDto(d);
        }
        await ActivateAsync(d, ct);
        return ToDto(d);
    }

    public async Task<FieldDefinitionDto> ApproveAsync(long id, CancellationToken ct)
    {
        Guard.Require(_user, Perm.FieldsApprove);
        var d = await _db.Set<CustomerFieldDefinition>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Field", id);
        if (d.Status != ConfigStatus.PendingApproval) throw new ConflictException("Field version is not pending approval.");
        Guard.FourEyes(d.CreatedBy ?? "", _user.Username, "Field publication");
        await CheckPublishCompatibilityAsync(d, ct);
        d.ApprovedBy = _user.Username;
        await ActivateAsync(d, ct);
        return ToDto(d);
    }

    private async Task ActivateAsync(CustomerFieldDefinition d, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (d.EffectiveFrom < now.AddMinutes(-5) && d.Version > 1) d.EffectiveFrom = now;
        var previous = await _db.Set<CustomerFieldDefinition>()
            .Where(x => x.Code == d.Code && x.Id != d.Id && x.Status == ConfigStatus.Published).ToListAsync(ct);
        foreach (var p in previous)
        {
            p.Status = ConfigStatus.Superseded;
            p.EffectiveTo = d.EffectiveFrom;
        }
        d.Status = ConfigStatus.Published;
        d.PublishedBy = _user.Username;
        d.PublishedAt = now;
        _audit.Record("FIELD_PUBLISH", "CustomerFieldDefinition", d.Code, previous.Select(p => new { p.Version, p.DataType }).FirstOrDefault(),
            new { d.Code, d.Version, d.DataType, d.EffectiveFrom, d.ApprovedBy });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Blocks incompatible changes: type changes with stored values or active consumers, encryption flips with stored values.</summary>
    private async Task CheckPublishCompatibilityAsync(CustomerFieldDefinition d, CancellationToken ct)
    {
        var prev = await _db.Set<CustomerFieldDefinition>().AsNoTracking()
            .Where(x => x.Code == d.Code && x.Id != d.Id && (x.Status == ConfigStatus.Published || x.Status == ConfigStatus.Inactive))
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        if (prev is null) return;
        var problems = new List<string>();
        var values = _db.Set<CustomerFieldValue>().Where(v => v.FieldCode == d.Code);
        if (prev.DataType != d.DataType)
        {
            var count = await values.CountAsync(ct);
            if (count > 0 && !FieldValueValidator.IsCompatibleTypeChange(prev.DataType, d.DataType))
            {
                var sample = await values.OrderBy(v => v.Id).Select(v => new { v.CustomerId, v.ValueText, v.ValueNumber, v.MaskedDisplay }).Take(10).ToListAsync(ct);
                problems.Add($"Type change {prev.DataType} -> {d.DataType} is incompatible with {count} stored value(s) (customers: {string.Join(", ", sample.Select(s => s.CustomerId).Distinct())}).");
            }
            var consumers = (await _deps.WhereUsedFieldAsync(d.Code, ct)).Where(w => w.IsActiveConsumer).ToList();
            if (consumers.Count > 0)
                problems.Add("Type change is used by active consumers: " + string.Join("; ", consumers.Select(c => $"{c.ConsumerType} {c.Reference} v{c.Version}")));
        }
        if (prev.IsEncrypted != d.IsEncrypted && await values.AnyAsync(ct))
            problems.Add("Changing the encryption flag requires a controlled data migration of stored values.");
        if (problems.Count > 0)
            throw new ConflictException("Publish blocked: " + string.Join(" ", problems), new { field = d.Code, problems });
    }

    public async Task<FieldDefinitionDto> DeactivateAsync(long id, string? reason, CancellationToken ct)
    {
        var d = await _db.Set<CustomerFieldDefinition>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Field", id);
        if (d.Status != ConfigStatus.Published) throw new ConflictException("Only the published version can be deactivated.");
        var consumers = (await _deps.WhereUsedFieldAsync(d.Code, ct)).Where(w => w.IsActiveConsumer).ToList();
        if (consumers.Count > 0)
            throw new ConflictException($"Field {d.Code} is used by active consumers and cannot be deactivated: " +
                                        string.Join("; ", consumers.Select(c => $"{c.ConsumerType} {c.Reference} v{c.Version} ({c.Status})")), new { whereUsed = consumers });
        d.Status = ConfigStatus.Inactive;
        d.EffectiveTo = _clock.UtcNow;
        _audit.Record("FIELD_DEACTIVATE", "CustomerFieldDefinition", d.Code, new { Status = ConfigStatus.Published }, new { d.Status }, reason);
        await _db.SaveChangesAsync(ct);
        return ToDto(d);
    }

    public Task<List<WhereUsedItem>> WhereUsedAsync(string code, CancellationToken ct) => _deps.WhereUsedFieldAsync(code.ToUpperInvariant(), ct);

    // ---------- options ----------
    public async Task<List<FieldOptionDto>> OptionsAsync(string fieldCode, bool includeInactive, CancellationToken ct)
    {
        var q = _db.Set<CustomerFieldOption>().AsNoTracking().Where(o => o.FieldCode == fieldCode);
        if (!includeInactive) q = q.Where(o => o.IsActive);
        return await q.OrderBy(o => o.SortOrder).Select(o => new FieldOptionDto(o.Id, o.FieldCode, o.OptionCode, o.LabelEn, o.LabelAr, o.SortOrder, o.IsActive)).ToListAsync(ct);
    }

    public async Task<FieldOptionDto> AddOptionAsync(string fieldCode, string optionCode, string labelEn, string? labelAr, int sortOrder, CancellationToken ct)
    {
        if (!await Defs.AnyAsync(d => d.Code == fieldCode, ct)) throw new NotFoundException("Field", fieldCode);
        if (string.IsNullOrWhiteSpace(optionCode) || string.IsNullOrWhiteSpace(labelEn)) throw new ValidationException("optionCode", "Option code and English label are required.");
        if (await _db.Set<CustomerFieldOption>().AnyAsync(o => o.FieldCode == fieldCode && o.OptionCode == optionCode, ct))
            throw new ConflictException($"Option {optionCode} already exists for {fieldCode}.");
        var o = new CustomerFieldOption
        {
            TenantId = _user.TenantId, FieldCode = fieldCode, OptionCode = optionCode, LabelEn = labelEn, LabelAr = labelAr ?? labelEn,
            SortOrder = sortOrder, IsActive = true, EffectiveFrom = _clock.UtcNow
        };
        _db.Set<CustomerFieldOption>().Add(o);
        _audit.Record("FIELD_OPTION_ADD", "CustomerFieldOption", $"{fieldCode}:{optionCode}", null, new { fieldCode, optionCode, labelEn });
        await _db.SaveChangesAsync(ct);
        return new FieldOptionDto(o.Id, o.FieldCode, o.OptionCode, o.LabelEn, o.LabelAr, o.SortOrder, o.IsActive);
    }

    public async Task<FieldOptionDto> SetOptionActiveAsync(long optionId, bool active, CancellationToken ct)
    {
        var o = await _db.Set<CustomerFieldOption>().FirstOrDefaultAsync(x => x.Id == optionId, ct) ?? throw new NotFoundException("Option", optionId);
        if (!active)
        {
            var used = (await _deps.WhereUsedOptionAsync(o.FieldCode, o.OptionCode, ct)).Where(w => w.IsActiveConsumer).ToList();
            if (used.Count > 0)
                throw new ConflictException($"Option {o.OptionCode} is used by active consumers and cannot be removed: " +
                                            string.Join("; ", used.Select(u => $"{u.ConsumerType} {u.Reference} v{u.Version}")), new { whereUsed = used });
            o.EffectiveTo = _clock.UtcNow;
        }
        else o.EffectiveTo = null;
        o.IsActive = active;
        _audit.Record(active ? "FIELD_OPTION_ACTIVATE" : "FIELD_OPTION_DEACTIVATE", "CustomerFieldOption", $"{o.FieldCode}:{o.OptionCode}", null, new { active });
        await _db.SaveChangesAsync(ct);
        return new FieldOptionDto(o.Id, o.FieldCode, o.OptionCode, o.LabelEn, o.LabelAr, o.SortOrder, o.IsActive);
    }

    public Task<List<WhereUsedItem>> OptionWhereUsedAsync(string fieldCode, string optionCode, CancellationToken ct) => _deps.WhereUsedOptionAsync(fieldCode, optionCode, ct);

    // ---------- groups & layouts ----------
    public async Task<List<CustomerFieldGroup>> GroupsAsync(CancellationToken ct) =>
        await _db.Set<CustomerFieldGroup>().AsNoTracking().OrderBy(g => g.SortOrder).ToListAsync(ct);

    public async Task<CustomerFieldGroup> UpsertGroupAsync(string code, string labelEn, string? labelAr, int sortOrder, CancellationToken ct)
    {
        code = code.Trim().ToUpperInvariant();
        if (!CodePattern.IsMatch(code)) throw new ValidationException("code", "Group code must be upper-case letters, digits or underscore.");
        var g = await _db.Set<CustomerFieldGroup>().FirstOrDefaultAsync(x => x.Code == code, ct);
        var old = g is null ? null : new { g.LabelEn, g.SortOrder };
        if (g is null) _db.Set<CustomerFieldGroup>().Add(g = new CustomerFieldGroup { TenantId = _user.TenantId, Code = code });
        g.LabelEn = labelEn; g.LabelAr = labelAr ?? labelEn; g.SortOrder = sortOrder; g.IsActive = true;
        _audit.Record("FIELD_GROUP_UPSERT", "CustomerFieldGroup", code, old, new { labelEn, sortOrder });
        await _db.SaveChangesAsync(ct);
        return g;
    }

    public async Task<List<CustomerFormLayout>> LayoutsAsync(CancellationToken ct) =>
        await _db.Set<CustomerFormLayout>().AsNoTracking().OrderBy(l => l.CustomerType).ThenBy(l => l.RoleCode).ThenByDescending(l => l.Version).ToListAsync(ct);

    public async Task<CustomerFormLayout> SaveLayoutAsync(string customerType, string roleCode, string layoutJson, CancellationToken ct)
    {
        if (!CustomerTypes.All.Contains(customerType)) throw new ValidationException("customerType", "Unknown customer type.");
        List<LayoutSection>? sections;
        try { sections = JsonSerializer.Deserialize<List<LayoutSection>>(layoutJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { throw new ValidationException("layoutJson", "Layout must be a JSON array of {group, fields[]}."); }
        if (sections is null) throw new ValidationException("layoutJson", "Layout is required.");
        var codes = await Defs.Select(d => d.Code).Distinct().ToListAsync(ct);
        var unknown = sections.SelectMany(s => s.Fields).Where(f => !codes.Contains(f)).ToList();
        if (unknown.Count > 0) throw new ValidationException("layoutJson", $"Unknown field code(s): {string.Join(", ", unknown)}.");
        var current = await _db.Set<CustomerFormLayout>().Where(l => l.CustomerType == customerType && l.RoleCode == roleCode && l.Status == ConfigStatus.Published).ToListAsync(ct);
        foreach (var c in current) c.Status = ConfigStatus.Superseded;
        var layout = new CustomerFormLayout
        {
            TenantId = _user.TenantId, CustomerType = customerType, RoleCode = roleCode, LayoutJson = layoutJson,
            Version = current.Count == 0 ? (await _db.Set<CustomerFormLayout>().Where(l => l.CustomerType == customerType && l.RoleCode == roleCode).Select(l => (int?)l.Version).MaxAsync(ct) ?? 0) + 1 : current.Max(c => c.Version) + 1,
            Status = ConfigStatus.Published, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username
        };
        _db.Set<CustomerFormLayout>().Add(layout);
        _audit.Record("FORM_LAYOUT_PUBLISH", "CustomerFormLayout", $"{customerType}:{roleCode}", null, new { layout.Version, layoutJson });
        await _db.SaveChangesAsync(ct);
        return layout;
    }

    public sealed class LayoutSection
    {
        public string Group { get; set; } = "";
        public List<string> Fields { get; set; } = new();
    }

    /// <summary>Form metadata used by the UI to render customer forms (no frontend change needed for new fields).</summary>
    public async Task<FormMetadataDto> FormMetadataAsync(string customerType, DateTime? asOf, CancellationToken ct)
    {
        if (!CustomerTypes.All.Contains(customerType)) throw new ValidationException("customerType", $"Unknown customer type '{customerType}'.");
        var at = asOf ?? _clock.UtcNow;
        var defs = (await EffectiveDefinitionsAsync(at, ct)).Where(d => d.AppliesTo(customerType)).ToList();
        var codes = defs.Select(d => d.Code).ToList();
        var options = await _db.Set<CustomerFieldOption>().AsNoTracking().Where(o => codes.Contains(o.FieldCode) && o.IsActive).OrderBy(o => o.SortOrder).ToListAsync(ct);
        var groups = await _db.Set<CustomerFieldGroup>().AsNoTracking().Where(g => g.IsActive).OrderBy(g => g.SortOrder).ToListAsync(ct);

        var layouts = await _db.Set<CustomerFormLayout>().AsNoTracking().Where(l => l.CustomerType == customerType && l.Status == ConfigStatus.Published).ToListAsync(ct);
        var layout = layouts.Where(l => _user.Roles.Contains(l.RoleCode)).OrderByDescending(l => l.Version).FirstOrDefault()
                     ?? layouts.Where(l => l.RoleCode == "*").OrderByDescending(l => l.Version).FirstOrDefault();

        var fieldDtos = defs.OrderBy(d => d.SortOrder).ThenBy(d => d.Code).Select(d => new FormFieldDto(d.Code, d.Version, d.LabelEn, d.LabelAr, d.Description,
            d.GroupCode, d.DataType, d.IsRequiredFor(customerType), d.IsSearchable, d.IsFilterable, d.IsSensitive, d.ValidationJson, d.DefaultValue, d.SortOrder,
            options.Where(o => o.FieldCode == d.Code).Select(o => new FieldOptionDto(o.Id, o.FieldCode, o.OptionCode, o.LabelEn, o.LabelAr, o.SortOrder, o.IsActive)).ToList())).ToList();

        List<FormGroupDto> groupDtos;
        if (layout is not null)
        {
            var sections = JsonSerializer.Deserialize<List<LayoutSection>>(layout.LayoutJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            var placed = sections.SelectMany(s => s.Fields).ToHashSet();
            groupDtos = sections.Select((s, i) =>
            {
                var g = groups.FirstOrDefault(x => x.Code == s.Group);
                return new FormGroupDto(s.Group, g?.LabelEn ?? s.Group, g?.LabelAr ?? s.Group, i, s.Fields.Where(codes.Contains).ToList());
            }).ToList();
            // Fields not placed by the layout still render in their configured group (never silently hidden).
            foreach (var grp in fieldDtos.Where(f => !placed.Contains(f.Code)).GroupBy(f => f.GroupCode))
            {
                var g = groups.FirstOrDefault(x => x.Code == grp.Key);
                var existing = groupDtos.FindIndex(x => x.Code == grp.Key);
                if (existing >= 0) groupDtos[existing] = groupDtos[existing] with { FieldCodes = groupDtos[existing].FieldCodes.Concat(grp.Select(f => f.Code)).ToList() };
                else groupDtos.Add(new FormGroupDto(grp.Key, g?.LabelEn ?? grp.Key, g?.LabelAr ?? grp.Key, 100 + (g?.SortOrder ?? 0), grp.Select(f => f.Code).ToList()));
            }
        }
        else
        {
            groupDtos = groups.Select(g => new FormGroupDto(g.Code, g.LabelEn, g.LabelAr, g.SortOrder, fieldDtos.Where(f => f.GroupCode == g.Code).Select(f => f.Code).ToList()))
                .Where(g => g.FieldCodes.Count > 0).ToList();
        }
        return new FormMetadataDto(customerType, layout?.RoleCode ?? "(default)", layout?.Version ?? 0, groupDtos, fieldDtos);
    }

    /// <summary>Definitions effective at the timestamp (one version per code).</summary>
    public async Task<List<CustomerFieldDefinition>> EffectiveDefinitionsAsync(DateTime at, CancellationToken ct)
    {
        var all = await _db.Set<CustomerFieldDefinition>().AsNoTracking()
            .Where(d => (d.Status == ConfigStatus.Published || d.Status == ConfigStatus.Superseded) && d.EffectiveFrom <= at && (d.EffectiveTo == null || d.EffectiveTo > at))
            .ToListAsync(ct);
        return all.GroupBy(d => d.Code).Select(g => g.OrderByDescending(d => d.Version).First()).ToList();
    }

    private async Task ValidateInputAsync(FieldDefinitionInput i, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string k, string m) { if (!errors.TryGetValue(k, out var l)) errors[k] = l = new(); l.Add(m); }
        if (!CodePattern.IsMatch(i.Code)) Add("code", "Code must start with a letter and contain only A-Z, 0-9 or underscore (max 50).");
        if (string.IsNullOrWhiteSpace(i.LabelEn)) Add("labelEn", "English label is required.");
        if (!FieldDataTypes.All.Contains(i.DataType)) Add("dataType", $"Unsupported data type '{i.DataType}'.");
        if (!await _db.Set<CustomerFieldGroup>().AnyAsync(g => g.Code == i.GroupCode, ct)) Add("groupCode", $"Unknown field group '{i.GroupCode}'.");
        foreach (var t in RiskScoringEngine.Split(i.CustomerTypes).Concat(RiskScoringEngine.Split(i.RequiredForTypes)))
            if (!CustomerTypes.All.Contains(t)) Add("customerTypes", $"Unknown customer type '{t}'.");
        var applies = RiskScoringEngine.Split(i.CustomerTypes);
        if (applies.Length > 0 && RiskScoringEngine.Split(i.RequiredForTypes).Any(r => !applies.Contains(r)))
            Add("requiredForTypes", "A field can only be required for customer types it applies to.");
        if (i.IsEncrypted && !i.IsSensitive) Add("isEncrypted", "Encrypted fields must also be marked sensitive.");
        if (i.IsEncrypted && i.DataType is FieldDataTypes.Decimal or FieldDataTypes.Integer or FieldDataTypes.Date or FieldDataTypes.DateTime && i.IsFilterable)
            Add("isFilterable", "Encrypted numeric/date fields cannot support range filters; use exact-match search only.");
        if (!string.IsNullOrWhiteSpace(i.ValidationJson))
        {
            try
            {
                var cfg = FieldValidationConfig.Parse(i.ValidationJson);
                if (cfg.Regex is not null) _ = new Regex(cfg.Regex, RegexOptions.None, TimeSpan.FromMilliseconds(200));
                if (cfg.Min > cfg.Max) Add("validationJson", "min must not exceed max.");
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                Add("validationJson", "Invalid validation configuration: " + ex.Message);
            }
        }
        ValidationException.ThrowIf(errors);
    }

    private static void Apply(CustomerFieldDefinition d, FieldDefinitionInput i)
    {
        d.LabelEn = i.LabelEn.Trim();
        d.LabelAr = string.IsNullOrWhiteSpace(i.LabelAr) ? i.LabelEn.Trim() : i.LabelAr.Trim();
        d.Description = i.Description;
        d.GroupCode = i.GroupCode;
        d.DataType = i.DataType;
        d.CustomerTypes = string.Join(",", RiskScoringEngine.Split(i.CustomerTypes));
        d.RequiredForTypes = string.Join(",", RiskScoringEngine.Split(i.RequiredForTypes));
        d.IsSearchable = i.IsSearchable;
        d.IsFilterable = i.IsFilterable;
        d.IsSensitive = i.IsSensitive;
        d.IsEncrypted = i.IsEncrypted;
        d.ValidationJson = string.IsNullOrWhiteSpace(i.ValidationJson) ? null : i.ValidationJson;
        d.DefaultValue = i.DefaultValue;
        d.SortOrder = i.SortOrder;
    }

    private static object ToAudit(CustomerFieldDefinition d) => new
    {
        d.Code, d.Version, d.Status, d.LabelEn, d.GroupCode, d.DataType, d.CustomerTypes, d.RequiredForTypes, d.IsSearchable, d.IsSensitive, d.IsEncrypted, d.ValidationJson
    };

    private async Task SaveUniqueAsync(CancellationToken ct, string conflictMessage)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true ||
                                           ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ConflictException(conflictMessage);
        }
    }
}

/// <summary>Dependency graph between fields, options, risk factors, methodologies and rules (where-used / publish blocking).</summary>
public sealed class DependencyService
{
    private readonly IAmlDbContext _db;
    public DependencyService(IAmlDbContext db) => _db = db;

    private static readonly string[] ActiveMethodologyStatuses = { ConfigStatus.Approved, ConfigStatus.PendingApproval };
    private static readonly string[] ActiveRuleStatuses = { ConfigStatus.Active, ConfigStatus.Approved, ConfigStatus.PendingApproval };

    public async Task<List<WhereUsedItem>> WhereUsedFieldAsync(string code, CancellationToken ct)
    {
        var ds = "field:" + code;
        var result = new List<WhereUsedItem>();
        var factors = await (from f in _db.Set<RiskFactorDefinition>().AsNoTracking()
                             join m in _db.Set<RiskMethodology>().AsNoTracking() on f.MethodologyId equals m.Id
                             where f.DataSource == ds && m.Status != ConfigStatus.Retired && m.Status != ConfigStatus.Superseded && m.Status != ConfigStatus.Rejected
                             select new { FCode = f.Code, f.IsActive, MCode = m.Code, m.Version, m.Status }).ToListAsync(ct);
        result.AddRange(factors.Select(f => new WhereUsedItem("RiskFactor", $"{f.MCode}/{f.FCode}", f.Version.ToString(), f.Status,
            f.IsActive && ActiveMethodologyStatuses.Contains(f.Status), $"Methodology {f.MCode} v{f.Version} factor {f.FCode}")));
        var overrides = await (from o in _db.Set<RiskOverridePolicy>().AsNoTracking()
                               join m in _db.Set<RiskMethodology>().AsNoTracking() on o.MethodologyId equals m.Id
                               where o.ConditionDataSource == ds && m.Status != ConfigStatus.Retired && m.Status != ConfigStatus.Superseded
                               select new { o.Code, MCode = m.Code, m.Version, m.Status }).ToListAsync(ct);
        result.AddRange(overrides.Select(o => new WhereUsedItem("RiskOverride", $"{o.MCode}/{o.Code}", o.Version.ToString(), o.Status,
            ActiveMethodologyStatuses.Contains(o.Status), "Override condition")));

        foreach (var rv in await RuleVersionsAsync(ct))
        {
            if (!rv.Fields.Contains(ds)) continue;
            result.Add(new WhereUsedItem("Rule", rv.RuleCode, rv.Version.ToString(), rv.Status, ActiveRuleStatuses.Contains(rv.Status), $"Rule {rv.RuleCode} v{rv.Version} condition"));
        }
        return result;
    }

    public async Task<List<WhereUsedItem>> WhereUsedOptionAsync(string fieldCode, string optionCode, CancellationToken ct)
    {
        var ds = "field:" + fieldCode;
        var result = new List<WhereUsedItem>();
        var mappings = await (from mp in _db.Set<RiskFactorScoreMapping>().AsNoTracking()
                              join f in _db.Set<RiskFactorDefinition>().AsNoTracking() on mp.FactorId equals f.Id
                              join m in _db.Set<RiskMethodology>().AsNoTracking() on f.MethodologyId equals m.Id
                              where f.DataSource == ds && m.Status != ConfigStatus.Retired && m.Status != ConfigStatus.Superseded && m.Status != ConfigStatus.Rejected
                              select new { mp.MatchType, mp.Value, f.Code, f.IsActive, MCode = m.Code, m.Version, m.Status }).ToListAsync(ct);
        foreach (var mp in mappings)
        {
            var hit = mp.MatchType == "Equals" && string.Equals(mp.Value, optionCode, StringComparison.OrdinalIgnoreCase) ||
                      mp.MatchType == "In" && RiskScoringEngine.Split(mp.Value).Contains(optionCode, StringComparer.OrdinalIgnoreCase);
            if (hit) result.Add(new WhereUsedItem("ScoreMapping", $"{mp.MCode}/{mp.Code}", mp.Version.ToString(), mp.Status,
                mp.IsActive && ActiveMethodologyStatuses.Contains(mp.Status), $"Mapping value '{optionCode}'"));
        }
        foreach (var rv in await RuleVersionsAsync(ct))
            if (rv.Values.Any(v => v.Field == ds && string.Equals(v.Value, optionCode, StringComparison.OrdinalIgnoreCase)))
                result.Add(new WhereUsedItem("Rule", rv.RuleCode, rv.Version.ToString(), rv.Status, ActiveRuleStatuses.Contains(rv.Status), $"Rule literal '{optionCode}'"));
        return result;
    }

    private sealed record RuleRef(string RuleCode, int Version, string Status, HashSet<string> Fields, List<(string Field, string Value)> Values);

    private async Task<List<RuleRef>> RuleVersionsAsync(CancellationToken ct)
    {
        var rows = await (from v in _db.Set<AmlRuleVersion>().AsNoTracking()
                          join r in _db.Set<AmlRule>().AsNoTracking() on v.RuleId equals r.Id
                          where v.Status != ConfigStatus.Retired && v.Status != ConfigStatus.Superseded && v.Status != ConfigStatus.Rejected
                          select new { r.Code, v.Version, v.Status, v.DefinitionJson }).ToListAsync(ct);
        var list = new List<RuleRef>();
        foreach (var r in rows)
        {
            try
            {
                var def = RuleDefinition.Parse(r.DefinitionJson);
                list.Add(new RuleRef(r.Code, r.Version, r.Status, RuleValidator.ReferencedFields(def).ToHashSet(), RuleValidator.ReferencedValues(def).ToList()));
            }
            catch (JsonException) { /* invalid drafts have no dependencies */ }
        }
        return list;
    }

    /// <summary>Validates that factor data sources referencing dynamic fields exist and are published.</summary>
    public async Task<List<string>> ValidateFactorSourcesAsync(IEnumerable<string> dataSources, CancellationToken ct)
    {
        var errors = new List<string>();
        var fieldCodes = dataSources.Where(d => d.StartsWith("field:")).Select(d => d[6..]).Distinct().ToList();
        if (fieldCodes.Count == 0) return errors;
        var published = await _db.Set<CustomerFieldDefinition>().AsNoTracking()
            .Where(d => fieldCodes.Contains(d.Code) && d.Status == ConfigStatus.Published).Select(d => d.Code).ToListAsync(ct);
        errors.AddRange(fieldCodes.Except(published).Select(c => $"Data source field:{c} does not reference a published customer field."));
        return errors;
    }

    public async Task<Dictionary<string, string>> PublishedFieldTypesAsync(CancellationToken ct) =>
        await _db.Set<CustomerFieldDefinition>().AsNoTracking().Where(d => d.Status == ConfigStatus.Published)
            .ToDictionaryAsync(d => d.Code, d => d.DataType, ct);
}
