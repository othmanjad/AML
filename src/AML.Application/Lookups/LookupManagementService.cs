using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AML.Application.Common;
using AML.Application.Customers;
using AML.Application.Risk;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AML.Application.Lookups;

public sealed class CodeMappingInput
{
    public string SourceSystem { get; set; } = "";
    public string Category { get; set; } = "";
    public string ExternalCode { get; set; } = "";
    public string? ExternalLabel { get; set; }
    public string Code { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class LookupScoreInput
{
    public string Category { get; set; } = "";
    public string Code { get; set; } = "";
    public decimal Score { get; set; }
    public string? Rationale { get; set; }
    public DateTime? EffectiveFrom { get; set; }
}

public sealed record RemapResult(int ValuesApplied, int CustomersReassessed, IReadOnlyList<string> Warnings);
public sealed record ImportResult(int Received, int Created, int Updated, int Rejected, IReadOnlyList<string> Errors, RemapResult Remap);

/// <summary>
/// Lookup management: translations of source-system codes to AML codes, the queue of codes still unmapped, and the central
/// risk score of each AML code (four-eyes approval). Adding a mapping applies it to customers waiting for it and re-assesses them;
/// approving a score re-assesses the customers holding that code.
/// </summary>
public sealed class LookupManagementService
{
    private static readonly Regex CategoryCode = new("^[A-Z0-9_]{1,64}$");
    public const int ReassessLimit = 2000;

    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly CodeMappingService _codes;
    private readonly CustomerService _customers;
    private readonly RiskAssessmentService _risk;
    private readonly ILogger<LookupManagementService> _log;

    public LookupManagementService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, CodeMappingService codes, CustomerService customers,
        RiskAssessmentService risk, ILogger<LookupManagementService> log)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _codes = codes; _customers = customers; _risk = risk; _log = log;
    }

    // ---------------- categories ----------------

    /// <summary>Categories that can be mapped and scored, with their AML codes (null codes = free text).</summary>
    public async Task<List<object>> CategoriesAsync(CancellationToken ct)
    {
        var result = new List<object>
        {
            new { category = CodeMappingService.CustomerType, label = "Customer type", appliesTo = "customer.customerType", codes = CustomerTypes.All.Select(c => new { code = c, label = c }) }
        };
        var lookups = await _db.Set<LookupValue>().AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Category).ThenBy(l => l.SortOrder).ThenBy(l => l.Code)
            .Select(l => new { l.Category, l.Code, l.LabelEn, l.LabelAr, l.Attribute }).ToListAsync(ct);
        foreach (var g in lookups.GroupBy(l => l.Category))
            result.Add(new
            {
                category = g.Key, label = g.Key, appliesTo = g.Key switch
                {
                    "COUNTRY" => "customer.nationality / residence / incorporation, Country fields, transaction countries",
                    "TXN_TYPE" => "transaction type", "CHANNEL" => "transaction channel", _ => "reference list"
                },
                codes = g.Select(x => new { code = x.Code, label = x.LabelEn, labelAr = x.LabelAr, attribute = x.Attribute })
            });
        var fields = await _db.Set<CustomerFieldDefinition>().AsNoTracking()
            .Where(f => f.Status == ConfigStatus.Published && (f.DataType == FieldDataTypes.SingleSelect || f.DataType == FieldDataTypes.MultiSelect)).ToListAsync(ct);
        var options = await _db.Set<CustomerFieldOption>().AsNoTracking().Where(o => o.IsActive).OrderBy(o => o.SortOrder).ToListAsync(ct);
        foreach (var f in fields.GroupBy(f => f.Code).Select(g => g.OrderByDescending(x => x.Version).First()))
            result.Add(new { category = f.Code, label = f.LabelEn, appliesTo = $"field:{f.Code}", codes = options.Where(o => o.FieldCode == f.Code).Select(o => new { code = o.OptionCode, label = o.LabelEn, labelAr = o.LabelAr }) });
        foreach (var free in new[] { (CodeMappingService.Region, "customer.region"), (CodeMappingService.SourceOfFunds, "profile.sourceOfFunds"), (CodeMappingService.Occupation, "profile.occupation") })
            if (!result.Any(r => (string)r.GetType().GetProperty("category")!.GetValue(r)! == free.Item1))
                result.Add(new { category = free.Item1, label = free.Item1, appliesTo = free.Item2, codes = Array.Empty<object>() });
        return result;
    }

    // ---------------- mappings ----------------

    public async Task<List<LookupCodeMapping>> MappingsAsync(string? source, string? category, string? search, CancellationToken ct)
    {
        var q = _db.Set<LookupCodeMapping>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(m => m.SourceSystem == source);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(m => m.Category == category);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(m => m.ExternalCode.Contains(search) || m.Code.Contains(search) || (m.ExternalLabel != null && m.ExternalLabel.Contains(search)));
        return await q.OrderBy(m => m.SourceSystem).ThenBy(m => m.Category).ThenBy(m => m.ExternalCode).Take(5000).ToListAsync(ct);
    }

    private async Task<List<string>> ValidateAsync(CodeMappingInput i, CancellationToken ct)
    {
        var errors = new List<string>();
        if (!await _db.Set<SourceSystem>().AnyAsync(s => s.Code == i.SourceSystem, ct)) errors.Add($"Unknown source system '{i.SourceSystem}'.");
        if (!CategoryCode.IsMatch(i.Category)) errors.Add($"Category '{i.Category}': A-Z, 0-9 and _ only.");
        if (string.IsNullOrWhiteSpace(i.ExternalCode) || i.ExternalCode.Length > 100) errors.Add("External code is required (max 100).");
        if (string.IsNullOrWhiteSpace(i.Code) || i.Code.Length > 100) errors.Add("AML code is required (max 100).");
        else if (await _codes.AmlCodesAsync(i.Category, ct) is { } known && !known.Contains(i.Code))
            errors.Add($"'{i.Code}' is not an AML code of {i.Category}.");
        return errors;
    }

    private static CodeMappingInput Normalize(CodeMappingInput i)
    {
        i.SourceSystem = i.SourceSystem.Trim().ToUpperInvariant();
        i.Category = i.Category.Trim().ToUpperInvariant();
        i.ExternalCode = i.ExternalCode.Trim();
        i.Code = i.Code.Trim();
        return i;
    }

    public async Task<object> CreateMappingAsync(CodeMappingInput input, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminLookups);
        var i = Normalize(input);
        var errors = await ValidateAsync(i, ct);
        if (errors.Count > 0) throw new ValidationException(new Dictionary<string, string[]> { ["mapping"] = errors.ToArray() }, string.Join(" ", errors));
        if (await _db.Set<LookupCodeMapping>().AnyAsync(m => m.SourceSystem == i.SourceSystem && m.Category == i.Category && m.ExternalCode == i.ExternalCode, ct))
            throw new ConflictException($"A mapping for {i.SourceSystem} {i.Category} '{i.ExternalCode}' already exists; edit it instead.");
        var m = new LookupCodeMapping
        {
            TenantId = _user.TenantId, SourceSystem = i.SourceSystem, Category = i.Category, ExternalCode = i.ExternalCode, ExternalLabel = i.ExternalLabel, Code = await CanonicalAsync(i, ct),
            IsActive = i.IsActive, Notes = i.Notes, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username
        };
        _db.Set<LookupCodeMapping>().Add(m);
        _audit.Record("LOOKUP_MAPPING_CREATE", "LookupCodeMapping", $"{m.SourceSystem}:{m.Category}:{m.ExternalCode}", null, new { m.Code, m.IsActive });
        await _db.SaveChangesAsync(ct);
        _codes.Invalidate();
        var remap = m.IsActive ? await ApplyPendingAsync(m, ct) : new RemapResult(0, 0, Array.Empty<string>());
        return new { mapping = m, remap };
    }

    private async Task<string> CanonicalAsync(CodeMappingInput i, CancellationToken ct) =>
        await _codes.AmlCodesAsync(i.Category, ct) is { } known ? known.First(k => string.Equals(k, i.Code, StringComparison.OrdinalIgnoreCase)) : i.Code;

    public async Task<object> UpdateMappingAsync(long id, CodeMappingInput input, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminLookups);
        var m = await _db.Set<LookupCodeMapping>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Mapping", id);
        if (!string.IsNullOrWhiteSpace(input.RowVersion)) _db.Entry(m).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(input.RowVersion, "mapping");
        var i = Normalize(new CodeMappingInput { SourceSystem = m.SourceSystem, Category = m.Category, ExternalCode = m.ExternalCode, Code = input.Code, IsActive = input.IsActive });
        var errors = await ValidateAsync(i, ct);
        if (errors.Count > 0) throw new ValidationException(new Dictionary<string, string[]> { ["mapping"] = errors.ToArray() }, string.Join(" ", errors));
        var before = new { m.Code, m.IsActive };
        m.Code = await CanonicalAsync(i, ct);
        m.IsActive = input.IsActive;
        m.ExternalLabel = input.ExternalLabel ?? m.ExternalLabel;
        m.Notes = input.Notes ?? m.Notes;
        m.UpdatedAt = _clock.UtcNow;
        m.UpdatedBy = _user.Username;
        _audit.Record("LOOKUP_MAPPING_UPDATE", "LookupCodeMapping", $"{m.SourceSystem}:{m.Category}:{m.ExternalCode}", before, new { m.Code, m.IsActive });
        await _db.SaveChangesAsync(ct);
        _codes.Invalidate();
        // A changed translation applies to values received from now on; values waiting in the unmapped queue are applied now.
        var remap = m.IsActive ? await ApplyPendingAsync(m, ct) : new RemapResult(0, 0, Array.Empty<string>());
        return new { mapping = m, remap };
    }

    /// <summary>CSV: sourceSystem,category,externalCode,code[,externalLabel] (header row required). Existing keys are updated.</summary>
    public async Task<ImportResult> ImportAsync(Stream csv, CancellationToken ct)
    {
        Guard.Require(_user, Perm.AdminLookups);
        using var reader = new StreamReader(csv);
        var header = (await reader.ReadLineAsync(ct))?.Split(',').Select(h => h.Trim().ToLowerInvariant()).ToList() ?? new();
        int Col(string n) => header.IndexOf(n.ToLowerInvariant());
        if (Col("sourceSystem") < 0 || Col("category") < 0 || Col("externalCode") < 0 || Col("code") < 0)
            throw new ValidationException("file", "CSV header must contain sourceSystem, category, externalCode, code (and optionally externalLabel).");
        var errors = new List<string>();
        int received = 0, created = 0, updated = 0, line = 1;
        var touched = new List<LookupCodeMapping>();
        string? row;
        while ((row = await reader.ReadLineAsync(ct)) is not null)
        {
            line++;
            if (string.IsNullOrWhiteSpace(row)) continue;
            if (++received > 10000) { errors.Add("More than 10,000 rows; the rest was ignored."); break; }
            var cells = row.Split(',');
            string Cell(string n) => Col(n) is var ix and >= 0 && ix < cells.Length ? cells[ix].Trim().Trim('"') : "";
            var i = Normalize(new CodeMappingInput { SourceSystem = Cell("sourceSystem"), Category = Cell("category"), ExternalCode = Cell("externalCode"), Code = Cell("code"), ExternalLabel = Col("externalLabel") >= 0 ? Cell("externalLabel") : null });
            var e = await ValidateAsync(i, ct);
            if (e.Count > 0) { errors.Add($"Line {line}: {string.Join(" ", e)}"); continue; }
            var m = await _db.Set<LookupCodeMapping>().FirstOrDefaultAsync(x => x.SourceSystem == i.SourceSystem && x.Category == i.Category && x.ExternalCode == i.ExternalCode, ct);
            if (m is null)
            {
                m = new LookupCodeMapping { TenantId = _user.TenantId, SourceSystem = i.SourceSystem, Category = i.Category, ExternalCode = i.ExternalCode, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username };
                _db.Set<LookupCodeMapping>().Add(m);
                created++;
            }
            else { m.UpdatedAt = _clock.UtcNow; m.UpdatedBy = _user.Username; updated++; }
            m.Code = await CanonicalAsync(i, ct);
            m.ExternalLabel = string.IsNullOrWhiteSpace(i.ExternalLabel) ? m.ExternalLabel : i.ExternalLabel;
            m.IsActive = true;
            touched.Add(m);
        }
        _audit.Record("LOOKUP_MAPPING_IMPORT", "LookupCodeMapping", "csv", null, new { received, created, updated, rejected = errors.Count });
        await _db.SaveChangesAsync(ct);
        _codes.Invalidate();
        int applied = 0, reassessed = 0;
        var warnings = new List<string>();
        foreach (var m in touched)
        {
            var r = await ApplyPendingAsync(m, ct);
            applied += r.ValuesApplied; reassessed += r.CustomersReassessed; warnings.AddRange(r.Warnings);
        }
        return new ImportResult(received, created, updated, errors.Count, errors, new RemapResult(applied, reassessed, warnings));
    }

    // ---------------- unmapped queue ----------------

    public async Task<List<object>> UnmappedAsync(CancellationToken ct)
    {
        var rows = await _db.Set<CustomerSourceValue>().AsNoTracking().Where(v => v.Status == "Unmapped")
            .GroupBy(v => new { v.SourceSystem, v.Category, v.ExternalCode })
            .Select(g => new { g.Key.SourceSystem, g.Key.Category, g.Key.ExternalCode, customers = g.Select(x => x.CustomerId).Distinct().Count(), targets = g.Select(x => x.Target).Distinct().Count(), firstSeen = g.Min(x => x.ReceivedAt), lastSeen = g.Max(x => x.ReceivedAt) })
            .OrderByDescending(x => x.customers).Take(1000).ToListAsync(ct);
        return rows.Cast<object>().ToList();
    }

    /// <summary>Applies a mapping to the customer values waiting for it, then re-assesses the affected customers.</summary>
    private async Task<RemapResult> ApplyPendingAsync(LookupCodeMapping m, CancellationToken ct)
    {
        var pending = await _db.Set<CustomerSourceValue>().Where(v => v.SourceSystem == m.SourceSystem && v.Category == m.Category && v.ExternalCode == m.ExternalCode && v.Status == "Unmapped")
            .OrderBy(v => v.Id).Take(ReassessLimit).ToListAsync(ct);
        var warnings = new List<string>();
        var customers = new HashSet<long>();
        var applied = 0;
        foreach (var v in pending)
        {
            try
            {
                if (v.Target.StartsWith("field:", StringComparison.Ordinal))
                {
                    var field = v.Target[6..];
                    var def = await _db.Set<CustomerFieldDefinition>().AsNoTracking().Where(f => f.Code == field && f.Status == ConfigStatus.Published).OrderByDescending(f => f.Version).FirstOrDefaultAsync(ct);
                    JsonElement value;
                    if (def?.DataType == FieldDataTypes.MultiSelect)
                    {
                        var current = await _db.Set<CustomerFieldValue>().AsNoTracking().Where(x => x.CustomerId == v.CustomerId && x.FieldCode == field && x.EffectiveTo == null).Select(x => x.ValueJson).FirstOrDefaultAsync(ct);
                        var list = current is null ? new List<string>() : JsonSerializer.Deserialize<List<string>>(current) ?? new();
                        if (!list.Contains(m.Code)) list.Add(m.Code);
                        value = JsonSerializer.SerializeToElement(list);
                    }
                    else value = JsonSerializer.SerializeToElement(m.Code);
                    await _customers.UpdateAttributesAsync(v.CustomerId, new AttributesUpdateRequest { Values = new() { [field] = value }, Reason = $"Lookup mapping {m.SourceSystem} {m.Category} '{m.ExternalCode}' → '{m.Code}'", Source = "LookupMapping" }, ct);
                }
                else
                {
                    var c = await _db.Set<Customer>().FirstOrDefaultAsync(x => x.Id == v.CustomerId, ct);
                    if (c is null) continue;
                    switch (v.Target)
                    {
                        case "customer.nationalityCountry": c.NationalityCountry = m.Code; break;
                        case "customer.residenceCountry": c.ResidenceCountry = m.Code; break;
                        case "customer.incorporationCountry": c.IncorporationCountry = m.Code; break;
                        case "customer.region": c.Region = m.Code; break;
                        default: continue;
                    }
                    c.UpdatedAt = _clock.UtcNow;
                    c.UpdatedBy = _user.Username;
                    _audit.Record("CUSTOMER_UPDATE", "Customer", c.Id.ToString(CultureInfo.InvariantCulture), null, new { field = v.Target, value = m.Code }, $"Lookup mapping {m.SourceSystem} {m.Category} '{m.ExternalCode}'");
                }
                v.Status = "Mapped";
                v.MappedCode = m.Code;
                v.ResolvedAt = _clock.UtcNow;
                applied++;
                customers.Add(v.CustomerId);
                await _db.SaveChangesAsync(ct);
            }
            catch (AppException ex)
            {
                warnings.Add($"Customer {v.CustomerId} {v.Target}: {ex.Message}");
            }
        }
        var idStrings = customers.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList();
        var marker = $"code '{m.ExternalCode}' for {m.Category}";
        foreach (var dq in await _db.Set<DataQualityIssue>().Where(d => d.IssueType == "UnmappedCode" && d.Status == "Open" && idStrings.Contains(d.EntityId) && d.Description.Contains(marker)).ToListAsync(ct))
        {
            dq.Status = "Resolved";
            dq.ResolvedAt = _clock.UtcNow;
            dq.ResolvedBy = _user.Username;
        }
        await _db.SaveChangesAsync(ct);
        var reassessed = await ReassessAsync(customers, $"lookupmap:{m.Id}:{m.Code}", $"Lookup mapping {m.Category} '{m.ExternalCode}' → '{m.Code}'", warnings, ct);
        return new RemapResult(applied, reassessed, warnings);
    }

    private async Task<int> ReassessAsync(IEnumerable<long> customerIds, string key, string reason, List<string> warnings, CancellationToken ct)
    {
        var n = 0;
        foreach (var id in customerIds.Take(ReassessLimit))
        {
            try
            {
                await _risk.AssessAsync(id, "FieldChange", $"{key}:{id}", reason, null, ct);
                n++;
            }
            catch (AppException ex)
            {
                warnings.Add($"Customer {id} not re-assessed: {ex.Message}");
                _log.LogWarning("Lookup re-assessment skipped for {CustomerId}: {Reason}", id, ex.Message);
            }
        }
        return n;
    }

    // ---------------- risk scores ----------------

    public async Task<List<LookupRiskScore>> ScoresAsync(string? category, bool includeHistory, CancellationToken ct)
    {
        var q = _db.Set<LookupRiskScore>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(s => s.Category == category);
        if (!includeHistory) q = q.Where(s => s.Status == "PendingApproval" || (s.Status == "Approved" && s.EffectiveTo == null));
        return await q.OrderBy(s => s.Category).ThenBy(s => s.Code).ThenByDescending(s => s.Id).Take(5000).ToListAsync(ct);
    }

    public async Task<LookupRiskScore> ProposeScoreAsync(LookupScoreInput i, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RiskConfig);
        var category = i.Category.Trim().ToUpperInvariant();
        var errors = new List<string>();
        if (!CategoryCode.IsMatch(category)) errors.Add($"Category '{i.Category}': A-Z, 0-9 and _ only.");
        if (i.Score is < 0 or > 100) errors.Add("Score must be within 0-100.");
        if (string.IsNullOrWhiteSpace(i.Rationale)) errors.Add("A rationale is required.");
        var code = i.Code.Trim();
        if (await _codes.AmlCodesAsync(category, ct) is { } known)
        {
            var hit = known.FirstOrDefault(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase));
            if (hit is null) errors.Add($"'{code}' is not an AML code of {category}.");
            else code = hit;
        }
        if (errors.Count > 0) throw new ValidationException(new Dictionary<string, string[]> { ["score"] = errors.ToArray() }, string.Join(" ", errors));
        if (await _db.Set<LookupRiskScore>().AnyAsync(s => s.Category == category && s.Code == code && s.Status == "PendingApproval", ct))
            throw new ConflictException($"A score for {category} '{code}' is already pending approval.");
        var s = new LookupRiskScore
        {
            TenantId = _user.TenantId, Category = category, Code = code, Score = i.Score, Rationale = i.Rationale, Status = "PendingApproval",
            EffectiveFrom = i.EffectiveFrom ?? _clock.UtcNow, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username
        };
        _db.Set<LookupRiskScore>().Add(s);
        _audit.Record("LOOKUP_SCORE_PROPOSE", "LookupRiskScore", $"{category}:{code}", null, new { s.Score, s.Rationale, s.EffectiveFrom });
        await _db.SaveChangesAsync(ct);
        return s;
    }

    public async Task<object> DecideScoreAsync(long id, bool approve, string? comment, CancellationToken ct)
    {
        Guard.Require(_user, Perm.RiskApprove);
        var s = await _db.Set<LookupRiskScore>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Lookup score", id);
        if (s.Status != "PendingApproval") throw new ConflictException("Only pending scores can be approved or rejected.");
        if (string.Equals(s.CreatedBy, _user.Username, StringComparison.OrdinalIgnoreCase)) throw new ForbiddenException("Separation of duties: the proposer cannot approve the score.");
        if (!approve)
        {
            if (string.IsNullOrWhiteSpace(comment)) throw new ValidationException("comment", "A reason is required to reject.");
            s.Status = "Rejected";
            _audit.Record("LOOKUP_SCORE_REJECT", "LookupRiskScore", $"{s.Category}:{s.Code}", null, new { comment });
            await _db.SaveChangesAsync(ct);
            return new { score = s, reassessed = 0 };
        }
        foreach (var prev in await _db.Set<LookupRiskScore>().Where(x => x.Category == s.Category && x.Code == s.Code && x.Status == "Approved" && x.EffectiveTo == null).ToListAsync(ct))
        {
            prev.EffectiveTo = s.EffectiveFrom;
            prev.Status = "Superseded";
        }
        s.Status = "Approved";
        s.ApprovedBy = _user.Username;
        s.ApprovedAt = _clock.UtcNow;
        _audit.Record("LOOKUP_SCORE_APPROVE", "LookupRiskScore", $"{s.Category}:{s.Code}", null, new { s.Score, s.EffectiveFrom, comment });
        await _db.SaveChangesAsync(ct);
        var warnings = new List<string>();
        var affected = s.EffectiveFrom <= _clock.UtcNow ? await CustomersWithCodeAsync(s.Category, s.Code, ct) : new List<long>();
        var reassessed = await ReassessAsync(affected, $"lookupscore:{s.Id}", $"Lookup score {s.Category} '{s.Code}' = {s.Score:0.##}", warnings, ct);
        return new { score = s, affectedCustomers = affected.Count, reassessed, warnings };
    }

    /// <summary>Customers whose current data holds the code (core columns or current field values), capped.</summary>
    private async Task<List<long>> CustomersWithCodeAsync(string category, string code, CancellationToken ct)
    {
        var customers = _db.Set<Customer>().AsNoTracking();
        IQueryable<long> q = category switch
        {
            CodeMappingService.Country => customers.Where(c => c.NationalityCountry == code || c.ResidenceCountry == code || c.IncorporationCountry == code).Select(c => c.Id),
            CodeMappingService.CustomerType => customers.Where(c => c.CustomerType == code).Select(c => c.Id),
            CodeMappingService.Region => customers.Where(c => c.Region == code).Select(c => c.Id),
            _ => _db.Set<CustomerFieldValue>().AsNoTracking().Where(v => v.FieldCode == category && v.EffectiveTo == null && (v.ValueText == code || (v.ValueJson != null && v.ValueJson.Contains("\"" + code + "\"")))).Select(v => v.CustomerId)
        };
        var ids = await q.Distinct().Take(ReassessLimit).ToListAsync(ct);
        // Only customers with an existing risk profile are re-assessed (onboarding assesses the others).
        return await _db.Set<CurrentCustomerRiskProfile>().AsNoTracking().Where(p => ids.Contains(p.CustomerId)).Select(p => p.CustomerId).ToListAsync(ct);
    }
}
