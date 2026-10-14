using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using AML.Application.Common;
using AML.Application.Fields;
using AML.Domain.Common;
using AML.Domain.Entities;
using AML.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AML.Application.Customers;

public sealed class CustomerService
{
    private static readonly Dictionary<string, string[]> StatusTransitions = new()
    {
        ["Prospect"] = new[] { "Active", "Rejected" },
        ["Active"] = new[] { "Restricted", "Suspended", "Dormant", "Exited" },
        ["Restricted"] = new[] { "Active", "Suspended", "Exited" },
        ["Suspended"] = new[] { "Active", "Restricted", "Exited" },
        ["Dormant"] = new[] { "Active", "Exited" },
        ["Rejected"] = Array.Empty<string>(),
        ["Exited"] = Array.Empty<string>(),
    };

    private readonly IAmlDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly ICryptoService _crypto;
    private readonly AccessPolicy _access;
    private readonly FieldConfigService _fields;
    private readonly TenantSettingsService _settings;
    private readonly ICorrelationContext _corr;

    public CustomerService(IAmlDbContext db, ICurrentUser user, IClock clock, IAuditService audit, ICryptoService crypto, AccessPolicy access,
        FieldConfigService fields, TenantSettingsService settings, ICorrelationContext corr)
    {
        _db = db; _user = user; _clock = clock; _audit = audit; _crypto = crypto; _access = access; _fields = fields; _settings = settings; _corr = corr;
    }

    private static readonly Dictionary<string, Expression<Func<CustomerListItem, object?>>> Sorts = new()
    {
        ["id"] = c => c.Id, ["externalId"] = c => c.ExternalId, ["name"] = c => c.NameLatin, ["type"] = c => c.CustomerType, ["status"] = c => c.Status,
        ["riskLevel"] = c => c.RiskLevel, ["riskScore"] = c => c.RiskScore, ["createdAt"] = c => c.CreatedAt, ["updatedAt"] = c => c.UpdatedAt,
        ["nextReviewDate"] = c => c.NextReviewDate, ["country"] = c => c.NationalityCountry, ["kycStatus"] = c => c.KycStatus
    };

    public async Task<PagedResult<CustomerListItem>> ListAsync(CustomerQuery f, PageRequest page, CancellationToken ct)
    {
        var q = _access.Customers().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            var key = string.Join(' ', ArabicNameNormalizer.Tokens(s));
            if (long.TryParse(s, out var id))
                q = q.Where(c => c.Id == id || c.ExternalId == s);
            else if (key.Length > 0)
                q = q.Where(c => c.ExternalId == s || c.NameSearchKey!.Contains(key));
            else
                q = q.Where(c => c.ExternalId == s);
        }
        if (!string.IsNullOrWhiteSpace(f.CustomerType)) q = q.Where(c => c.CustomerType == f.CustomerType);
        if (!string.IsNullOrWhiteSpace(f.Status)) q = q.Where(c => c.Status == f.Status);
        if (!string.IsNullOrWhiteSpace(f.KycStatus)) q = q.Where(c => c.KycStatus == f.KycStatus);
        if (!string.IsNullOrWhiteSpace(f.ScreeningStatus)) q = q.Where(c => c.ScreeningStatus == f.ScreeningStatus);
        if (!string.IsNullOrWhiteSpace(f.Country)) q = q.Where(c => c.NationalityCountry == f.Country || c.ResidenceCountry == f.Country || c.IncorporationCountry == f.Country);
        if (!string.IsNullOrWhiteSpace(f.SourceSystem)) q = q.Where(c => c.SourceSystem == f.SourceSystem);
        if (!string.IsNullOrWhiteSpace(f.AssignedOfficer)) q = q.Where(c => c.AssignedOfficer == f.AssignedOfficer);
        if (f.CreatedFrom is { } cf) q = q.Where(c => c.CreatedAt >= cf);
        if (f.CreatedTo is { } cto) q = q.Where(c => c.CreatedAt <= cto);
        if (f.UpdatedFrom is { } uf) q = q.Where(c => c.UpdatedAt >= uf);
        if (f.UpdatedTo is { } uto) q = q.Where(c => c.UpdatedAt <= uto);
        if (!string.IsNullOrWhiteSpace(f.Wallet))
        {
            var w = f.Wallet.Trim();
            var owners = _db.Set<Wallet>().Where(x => x.ExternalWalletId == w && x.CustomerId != null).Select(x => x.CustomerId!.Value);
            q = q.Where(c => owners.Contains(c.Id));
        }
        if (f.FieldFilter is { Count: > 0 }) q = await ApplyFieldFiltersAsync(q, f.FieldFilter, ct);

        var profiles = _db.Set<CurrentCustomerRiskProfile>();
        var today = DateOnly.FromDateTime(_clock.UtcNow);
        var rows = from c in q
                   join p in profiles on c.Id equals p.CustomerId into pj
                   from p in pj.DefaultIfEmpty()
                   select new CustomerListItem { Id = c.Id, SourceSystem = c.SourceSystem, ExternalId = c.ExternalId, CustomerType = c.CustomerType, Status = c.Status, NameLatin = c.NameLatin, NameArabic = c.NameArabic, NationalityCountry = c.NationalityCountry, ResidenceCountry = c.ResidenceCountry, RiskLevel = p == null ? null : p.Level, RiskScore = p == null ? null : p.Score, KycStatus = c.KycStatus, ScreeningStatus = c.ScreeningStatus, NextReviewDate = p == null ? null : p.NextReviewDate, AssignedOfficer = c.AssignedOfficer, Region = c.Region, DueDiligenceLevel = c.DueDiligenceLevel, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt };
        if (!string.IsNullOrWhiteSpace(f.RiskLevel)) rows = rows.Where(r => r.RiskLevel == f.RiskLevel);
        if (f.ReviewOverdue == true)
        {
            var overdueTasks = _db.Set<RiskReviewTask>().Where(t => t.Status != "Completed" && t.DueDate < today).Select(t => t.CustomerId);
            rows = rows.Where(r => (r.NextReviewDate != null && r.NextReviewDate < today) || overdueTasks.Contains(r.Id));
        }
        return await rows.SortBy(page, Sorts, "id", defaultDesc: true).ToPagedAsync(page, ct);
    }

    private async Task<IQueryable<Customer>> ApplyFieldFiltersAsync(IQueryable<Customer> q, List<string> filters, CancellationToken ct)
    {
        var defs = await _fields.EffectiveDefinitionsAsync(_clock.UtcNow, ct);
        var values = _db.Set<CustomerFieldValue>().Where(v => v.EffectiveTo == null);
        foreach (var raw in filters)
        {
            var parts = raw.Split(':', 3);
            if (parts.Length != 3) throw new ValidationException("fieldFilter", "Field filters use the form CODE:op:value.");
            var (code, op, value) = (parts[0].Trim().ToUpperInvariant(), parts[1].Trim(), parts[2]);
            var def = defs.FirstOrDefault(d => d.Code == code) ?? throw new ValidationException("fieldFilter", $"Unknown field '{code}'.");
            if (!def.IsSearchable && !def.IsFilterable)
                throw new ValidationException("fieldFilter", $"Field '{code}' is not searchable or filterable.");
            var fv = values.Where(v => v.FieldCode == code);
            if (def.IsEncrypted)
            {
                if (op != "eq") throw new ValidationException("fieldFilter", $"Encrypted field '{code}' supports exact match only (use POST /api/customers/search).");
                var token = _crypto.SearchToken(value);
                fv = fv.Where(v => v.ValueToken == token);
            }
            else if (FieldDataTypes.IsNumeric(def.DataType))
            {
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) throw new ValidationException("fieldFilter", $"'{value}' is not a number.");
                fv = op switch
                {
                    "eq" => fv.Where(v => v.ValueNumber == d), "gt" => fv.Where(v => v.ValueNumber > d), "gte" => fv.Where(v => v.ValueNumber >= d),
                    "lt" => fv.Where(v => v.ValueNumber < d), "lte" => fv.Where(v => v.ValueNumber <= d),
                    _ => throw new ValidationException("fieldFilter", $"Operator '{op}' is not supported for numeric fields.")
                };
            }
            else if (FieldDataTypes.IsTemporal(def.DataType))
            {
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                    throw new ValidationException("fieldFilter", $"'{value}' is not a date.");
                fv = op switch
                {
                    "eq" => fv.Where(v => v.ValueDate == dt), "gt" => fv.Where(v => v.ValueDate > dt), "gte" => fv.Where(v => v.ValueDate >= dt),
                    "lt" => fv.Where(v => v.ValueDate < dt), "lte" => fv.Where(v => v.ValueDate <= dt),
                    _ => throw new ValidationException("fieldFilter", $"Operator '{op}' is not supported for date fields.")
                };
            }
            else if (def.DataType == FieldDataTypes.Boolean)
            {
                var b = bool.TryParse(value, out var pb) && pb;
                fv = fv.Where(v => v.ValueBool == b);
            }
            else if (def.DataType == FieldDataTypes.MultiSelect)
            {
                var needle = "\"" + value + "\"";
                fv = fv.Where(v => v.ValueJson!.Contains(needle));
            }
            else
            {
                fv = op switch
                {
                    "eq" => fv.Where(v => v.ValueText == value),
                    "contains" => fv.Where(v => v.ValueText!.Contains(value)),
                    _ => throw new ValidationException("fieldFilter", $"Operator '{op}' is not supported for text fields.")
                };
            }
            var ids = fv.Select(v => v.CustomerId);
            q = q.Where(c => ids.Contains(c.Id));
        }
        return q;
    }

    /// <summary>Exact-match search on sensitive values using keyed HMAC tokens; tokens are never returned or logged.</summary>
    public async Task<List<CustomerListItem>> SensitiveSearchAsync(SensitiveSearchRequest r, CancellationToken ct)
    {
        var ids = new HashSet<long>();
        var any = false;
        if (!string.IsNullOrWhiteSpace(r.IdentityNumber))
        {
            any = true;
            var token = _crypto.SearchToken(r.IdentityNumber);
            ids.UnionWith(await _db.Set<CustomerIdentification>().Where(i => i.IdNumberToken == token).Select(i => i.CustomerId).ToListAsync(ct));
        }
        if (!string.IsNullOrWhiteSpace(r.Phone))
        {
            any = true;
            var token = _crypto.SearchToken(NormalizePhone(r.Phone));
            ids.UnionWith(await _db.Set<CustomerContact>().Where(c => c.ContactType == "Phone" && c.SearchToken == token).Select(c => c.CustomerId).ToListAsync(ct));
        }
        if (!string.IsNullOrWhiteSpace(r.Email))
        {
            any = true;
            var token = _crypto.SearchToken(r.Email.Trim().ToLowerInvariant());
            ids.UnionWith(await _db.Set<CustomerContact>().Where(c => c.ContactType == "Email" && c.SearchToken == token).Select(c => c.CustomerId).ToListAsync(ct));
        }
        if (!string.IsNullOrWhiteSpace(r.FieldCode) && r.FieldValue is not null)
        {
            any = true;
            var code = r.FieldCode.ToUpperInvariant();
            var def = (await _fields.EffectiveDefinitionsAsync(_clock.UtcNow, ct)).FirstOrDefault(d => d.Code == code)
                      ?? throw new ValidationException("fieldCode", $"Unknown field '{code}'.");
            if (!def.IsSearchable) throw new ValidationException("fieldCode", $"Field '{code}' is not searchable.");
            var token = _crypto.SearchToken(r.FieldValue);
            ids.UnionWith(await _db.Set<CustomerFieldValue>().Where(v => v.FieldCode == code && v.EffectiveTo == null && (v.ValueToken == token || v.ValueText == r.FieldValue))
                .Select(v => v.CustomerId).ToListAsync(ct));
        }
        if (!any) throw new ValidationException("search", "Provide at least one search value.");
        _audit.Record("CUSTOMER_SENSITIVE_SEARCH", "Customer", null, null, new
        {
            criteria = new[] { r.IdentityNumber is null ? null : "identityNumber", r.Phone is null ? null : "phone", r.Email is null ? null : "email", r.FieldCode }.Where(x => x != null),
            resultCount = ids.Count
        });
        await _db.SaveChangesAsync(ct);
        var visible = await _access.Customers().Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        if (visible.Count == 0) return new List<CustomerListItem>();
        var rows = await (from c in _access.Customers().AsNoTracking().Where(c => visible.Contains(c.Id))
                          join p in _db.Set<CurrentCustomerRiskProfile>() on c.Id equals p.CustomerId into pj
                          from p in pj.DefaultIfEmpty()
                          select new CustomerListItem { Id = c.Id, SourceSystem = c.SourceSystem, ExternalId = c.ExternalId, CustomerType = c.CustomerType, Status = c.Status, NameLatin = c.NameLatin, NameArabic = c.NameArabic, NationalityCountry = c.NationalityCountry, ResidenceCountry = c.ResidenceCountry, RiskLevel = p == null ? null : p.Level, RiskScore = p == null ? null : p.Score, KycStatus = c.KycStatus, ScreeningStatus = c.ScreeningStatus, NextReviewDate = p == null ? null : p.NextReviewDate, AssignedOfficer = c.AssignedOfficer, Region = c.Region, DueDiligenceLevel = c.DueDiligenceLevel, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt }).ToListAsync(ct);
        return rows;
    }

    public static string NormalizePhone(string phone) => new string(phone.Where(ch => char.IsDigit(ch) || ch == '+').ToArray());

    public async Task<CustomerDetailDto> CreateAsync(CustomerCreateRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string k, string m) { if (!errors.TryGetValue(k, out var l)) errors[k] = l = new(); l.Add(m); }
        if (string.IsNullOrWhiteSpace(r.SourceSystem)) Add("sourceSystem", "Source system is required.");
        if (string.IsNullOrWhiteSpace(r.ExternalId)) Add("externalId", "External customer ID is required.");
        if (!CustomerTypes.All.Contains(r.CustomerType)) Add("customerType", $"Unknown customer type '{r.CustomerType}'. Allowed: {string.Join(", ", CustomerTypes.All)}.");
        if (string.IsNullOrWhiteSpace(r.NameLatin) && string.IsNullOrWhiteSpace(r.NameArabic)) Add("name", "A Latin or Arabic name is required.");
        if (r.SourceSystem is { Length: > 0 } && !await _db.Set<SourceSystem>().AnyAsync(s => s.Code == r.SourceSystem && s.IsActive, ct))
            Add("sourceSystem", $"Unknown or inactive source system '{r.SourceSystem}'.");
        await ValidateCountriesAsync(new[] { r.NationalityCountry, r.ResidenceCountry, r.IncorporationCountry }, Add, ct);
        ValidationException.ThrowIf(errors);

        if (await _db.Set<Customer>().AnyAsync(c => c.SourceSystem == r.SourceSystem && c.ExternalId == r.ExternalId, ct))
            throw new ConflictException($"A customer with source system '{r.SourceSystem}' and external ID '{r.ExternalId}' already exists.");

        var now = _clock.UtcNow;
        var c = new Customer
        {
            TenantId = _user.TenantId, SourceSystem = r.SourceSystem, ExternalId = r.ExternalId.Trim(), CustomerType = r.CustomerType, Status = "Prospect",
            NameLatin = r.NameLatin?.Trim(), NameArabic = r.NameArabic?.Trim(), NameSearchKey = ArabicNameNormalizer.SearchKey(r.NameLatin, r.NameArabic),
            DateOfBirth = r.DateOfBirth, NationalityCountry = r.NationalityCountry, ResidenceCountry = r.ResidenceCountry, IncorporationCountry = r.IncorporationCountry,
            RegistrationDate = r.RegistrationDate ?? DateOnly.FromDateTime(now), AssignedOfficer = r.AssignedOfficer, Region = r.Region ?? _user.DataScopeRegion,
            OnboardingAgentId = r.OnboardingAgentId, IsPep = r.IsPep, CreatedAt = now, UpdatedAt = now, CreatedBy = _user.Username, UpdatedBy = _user.Username
        };
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.Set<Customer>().Add(c);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException($"A customer with source system '{r.SourceSystem}' and external ID '{r.ExternalId}' already exists.");
        }

        var attrs = r.Attributes ?? new Dictionary<string, JsonElement>();
        await WriteAttributesAsync(c, attrs, now, "Onboarding", "UI", requireAll: true, ct);
        foreach (var i in r.Identifications ?? new()) AddIdentification(c, i);
        foreach (var ci in r.Contacts ?? new()) AddContact(c, ci);
        if (r.FinancialProfile is { } fp) AddFinancialProfile(c, fp, now);
        _audit.Record("CUSTOMER_CREATE", "Customer", c.Id.ToString(), null, new
        {
            c.SourceSystem, c.ExternalId, c.CustomerType, c.NameLatin, c.NameArabic, c.NationalityCountry, c.ResidenceCountry,
            attributes = attrs.Keys, identifications = r.Identifications?.Count ?? 0
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(c.Id, ct);
    }

    private async Task ValidateCountriesAsync(IEnumerable<string?> codes, Action<string, string> add, CancellationToken ct)
    {
        var provided = codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct().ToList();
        if (provided.Count == 0) return;
        var known = await _db.Set<LookupValue>().Where(l => l.Category == "COUNTRY" && provided.Contains(l.Code)).Select(l => l.Code).ToListAsync(ct);
        foreach (var c in provided.Except(known)) add("country", $"Unknown ISO country code '{c}'.");
    }

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true ||
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true;

    private void AddIdentification(Customer c, IdentificationInput i)
    {
        var number = i.IdNumber.Trim();
        if (number.Length < 4) throw new ValidationException("identifications", "Identity number is too short.");
        _db.Set<CustomerIdentification>().Add(new CustomerIdentification
        {
            TenantId = c.TenantId, CustomerId = c.Id, IdType = i.IdType, IdNumberEncrypted = _crypto.Encrypt(number), IdNumberToken = _crypto.SearchToken(number),
            Last4 = number[^4..], IssuingCountry = i.IssuingCountry, IssueDate = i.IssueDate, ExpiryDate = i.ExpiryDate, CreatedAt = _clock.UtcNow
        });
    }

    private void AddContact(Customer c, ContactInput i)
    {
        var value = i.Value.Trim();
        var normalized = i.ContactType switch
        {
            "Phone" => NormalizePhone(value),
            "Email" => value.ToLowerInvariant(),
            _ => value
        };
        if (i.ContactType == "Email" && !normalized.Contains('@')) throw new ValidationException("contacts", "Invalid email address.");
        _db.Set<CustomerContact>().Add(new CustomerContact
        {
            TenantId = c.TenantId, CustomerId = c.Id, ContactType = i.ContactType, ValueEncrypted = _crypto.Encrypt(normalized),
            SearchToken = i.ContactType is "Phone" or "Email" ? _crypto.SearchToken(normalized) : null,
            Masked = i.ContactType == "Email" ? MaskEmail(normalized) : FieldValueValidator.Mask(normalized), IsPrimary = i.IsPrimary, CreatedAt = _clock.UtcNow
        });
    }

    private static string MaskEmail(string e)
    {
        var at = e.IndexOf('@');
        return at <= 1 ? "***" + e[Math.Max(at, 0)..] : e[0] + new string('*', at - 1) + e[at..];
    }

    private void AddFinancialProfile(Customer c, FinancialProfileInput fp, DateTime now)
    {
        _db.Set<CustomerFinancialProfile>().Add(new CustomerFinancialProfile
        {
            TenantId = c.TenantId, CustomerId = c.Id, DeclaredMonthlyIncome = fp.DeclaredMonthlyIncome, Currency = fp.Currency, SourceOfFunds = fp.SourceOfFunds,
            SourceOfWealth = fp.SourceOfWealth, Occupation = fp.Occupation, ExpectedMonthlyVolume = fp.ExpectedMonthlyVolume, ExpectedMonthlyCount = fp.ExpectedMonthlyCount,
            EffectiveFrom = fp.EffectiveFrom ?? now, DeclaredAt = fp.DeclaredAt ?? now, CreatedBy = _user.Username
        });
    }

    public async Task<Customer> LoadVisibleAsync(long id, CancellationToken ct, bool tracking = false)
    {
        var q = _access.Customers();
        if (!tracking) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Customer", id);
    }

    public async Task<CustomerDetailDto> GetAsync(long id, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(id, ct);
        var unmask = _user.Has(Perm.CustomersUnmask);
        var today = DateOnly.FromDateTime(_clock.UtcNow);
        var ids = await _db.Set<CustomerIdentification>().AsNoTracking().Where(i => i.CustomerId == id).ToListAsync(ct);
        var contacts = await _db.Set<CustomerContact>().AsNoTracking().Where(i => i.CustomerId == id).ToListAsync(ct);
        var profile = await _db.Set<CurrentCustomerRiskProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.CustomerId == id, ct);
        var overdueTask = await _db.Set<RiskReviewTask>().AnyAsync(t => t.CustomerId == id && t.Status != "Completed" && t.DueDate < today, ct);
        var decision = await _db.Set<CustomerAcceptanceDecision>().AsNoTracking().Where(d => d.CustomerId == id && d.IsCurrent).OrderByDescending(d => d.Id).Select(d => d.Decision).FirstOrDefaultAsync(ct);
        var alertIds = _db.Set<AmlAlertCustomer>().Where(a => a.CustomerId == id).Select(a => a.AlertId);
        var alertCount = _user.Has(Perm.AlertsRead) ? await _access.Alerts().CountAsync(a => a.PrimaryCustomerId == id || alertIds.Contains(a.Id), ct) : 0;
        var caseIds = _db.Set<AmlCaseCustomer>().Where(x => x.CustomerId == id).Select(x => x.CaseId);
        var caseCount = _user.Has(Perm.CasesRead) ? await _access.Cases().CountAsync(x => x.PrimaryCustomerId == id || caseIds.Contains(x.Id), ct) : 0;
        var gaps = await KycGapsAsync(c, ct);

        if (unmask && (ids.Count > 0 || contacts.Count > 0))
        {
            _audit.Record("CUSTOMER_UNMASK_READ", "Customer", id.ToString(), null, new { identifications = ids.Count, contacts = contacts.Count });
            await _db.SaveChangesAsync(ct);
        }
        var flags = new List<string>();
        if (c.IsPep) flags.Add("PEP");
        if (c.IsFrozen) flags.Add("Frozen");
        if (profile?.SanctionsWorkflow == true) flags.Add("SanctionsWorkflow");
        if (c.IsTestData) flags.Add("TestData");

        return new CustomerDetailDto(c.Id, c.SourceSystem, c.ExternalId, c.CustomerType, c.Status, c.NameLatin, c.NameArabic, c.DateOfBirth, c.NationalityCountry,
            c.ResidenceCountry, c.IncorporationCountry, c.RegistrationDate, c.KycStatus, c.ScreeningStatus, c.AssignedOfficer, c.Region, c.DueDiligenceLevel, c.IsPep,
            c.IsFrozen, c.ExitedAt, c.IsTestData, c.CreatedAt, c.UpdatedAt, Convert.ToBase64String(c.RowVersion),
            ids.Select(i => new IdentificationDto(i.Id, i.IdType, "******" + i.Last4, i.IssuingCountry, i.IssueDate, i.ExpiryDate, i.VerificationStatus,
                i.ExpiryDate is { } e && e < today, unmask ? _crypto.Decrypt(i.IdNumberEncrypted) : null)).ToList(),
            contacts.Select(x => new ContactDto(x.Id, x.ContactType, x.Masked, x.IsPrimary, unmask ? _crypto.Decrypt(x.ValueEncrypted) : null)).ToList(),
            profile is null ? null : new RiskSummaryDto(profile.Score, profile.Level, profile.CalculatedScore, profile.CalculatedLevel, profile.MonitoringLevel,
                profile.LastAssessmentId, profile.LastAssessedAt, profile.NextReviewDate, profile.OverrideApplied, profile.OverrideType, profile.OverrideReason,
                profile.SanctionsWorkflow, profile.MethodologyCode, profile.MethodologyVersion, overdueTask || profile.NextReviewDate < today),
            decision, alertCount, caseCount, gaps, flags);
    }

    public async Task<CustomerDetailDto> UpdateAsync(long id, CustomerUpdateRequest r, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(id, ct, tracking: true);
        _db.Entry(c).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(r.RowVersion, "customer");
        var errors = new Dictionary<string, List<string>>();
        await ValidateCountriesAsync(new[] { r.NationalityCountry, r.ResidenceCountry, r.IncorporationCountry },
            (k, m) => { if (!errors.TryGetValue(k, out var l)) errors[k] = l = new(); l.Add(m); }, ct);
        ValidationException.ThrowIf(errors);
        var old = new { c.NameLatin, c.NameArabic, c.DateOfBirth, c.NationalityCountry, c.ResidenceCountry, c.IncorporationCountry, c.AssignedOfficer, c.Region, c.IsPep };
        if (r.NameLatin is not null) c.NameLatin = r.NameLatin.Trim();
        if (r.NameArabic is not null) c.NameArabic = r.NameArabic.Trim();
        if (r.DateOfBirth is not null) c.DateOfBirth = r.DateOfBirth;
        if (r.NationalityCountry is not null) c.NationalityCountry = r.NationalityCountry;
        if (r.ResidenceCountry is not null) c.ResidenceCountry = r.ResidenceCountry;
        if (r.IncorporationCountry is not null) c.IncorporationCountry = r.IncorporationCountry;
        if (r.AssignedOfficer is not null) c.AssignedOfficer = r.AssignedOfficer;
        if (r.Region is not null) c.Region = r.Region;
        if (r.IsPep is not null) c.IsPep = r.IsPep.Value;
        c.NameSearchKey = ArabicNameNormalizer.SearchKey(c.NameLatin, c.NameArabic);
        c.UpdatedAt = _clock.UtcNow;
        c.UpdatedBy = _user.Username;
        _audit.Record("CUSTOMER_UPDATE", "Customer", id.ToString(), old,
            new { c.NameLatin, c.NameArabic, c.DateOfBirth, c.NationalityCountry, c.ResidenceCountry, c.IncorporationCountry, c.AssignedOfficer, c.Region, c.IsPep }, r.Reason);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<CustomerDetailDto> ChangeStatusAsync(long id, string status, string? reason, string? rowVersion, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(id, ct, tracking: true);
        if (rowVersion is not null) _db.Entry(c).Property(x => x.RowVersion).OriginalValue = Guard.RowVersion(rowVersion, "customer");
        if (!StatusTransitions.TryGetValue(c.Status, out var allowed) || !allowed.Contains(status))
            throw new ConflictException($"Invalid status transition {c.Status} -> {status}. Allowed: {string.Join(", ", allowed ?? Array.Empty<string>())}.",
                new { allowed });
        if (status == "Active" && c.Status == "Prospect")
        {
            var decision = await _db.Set<CustomerAcceptanceDecision>().Where(d => d.CustomerId == id && d.IsCurrent).OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
            if (decision is null || decision.Decision is not ("Accepted" or "AcceptedWithConditions"))
                throw new ConflictException("Customer cannot be activated without a recorded Accepted onboarding decision.");
        }
        if (status == "Exited") throw new ConflictException("Use the relationship exit workflow (approval required) to exit a customer.");
        var old = c.Status;
        c.Status = status;
        c.UpdatedAt = _clock.UtcNow;
        c.UpdatedBy = _user.Username;
        _audit.Record("CUSTOMER_STATUS_CHANGE", "Customer", id.ToString(), new { Status = old }, new { Status = status }, reason);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------------- dynamic attributes ----------------
    public async Task<List<AttributeValueDto>> AttributesAsync(long customerId, DateTime? asOf, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(customerId, ct);
        var at = asOf ?? _clock.UtcNow;
        var values = await _db.Set<CustomerFieldValue>().AsNoTracking()
            .Where(v => v.CustomerId == customerId && v.EffectiveFrom <= at && (v.EffectiveTo == null || v.EffectiveTo > at)).ToListAsync(ct);
        var defIds = values.Select(v => v.FieldDefinitionId).Distinct().ToList();
        var defs = await _db.Set<CustomerFieldDefinition>().AsNoTracking().Where(d => defIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var current = (await _fields.EffectiveDefinitionsAsync(at, ct)).ToDictionary(d => d.Code);
        var unmask = _user.Has(Perm.CustomersUnmask);
        var result = new List<AttributeValueDto>();
        var unmasked = 0;
        foreach (var v in values.OrderBy(v => v.FieldCode))
        {
            var d = current.TryGetValue(v.FieldCode, out var cd) ? cd : defs[v.FieldDefinitionId];
            if (!current.ContainsKey(v.FieldCode) && asOf is null) continue; // deactivated fields are hidden on forms (still in history)
            var (value, display, masked) = Present(v, d, unmask);
            if (d.IsSensitive && !masked) unmasked++;
            result.Add(new AttributeValueDto(v.FieldCode, v.FieldVersion, d.LabelEn, d.LabelAr, d.GroupCode, d.DataType, d.IsSensitive, value, display, masked,
                v.EffectiveFrom, v.EffectiveTo, v.Source, v.VerificationStatus));
        }
        if (unmasked > 0)
        {
            _audit.Record("CUSTOMER_ATTRIBUTE_UNMASK_READ", "Customer", c.Id.ToString(), null, new { fields = unmasked });
            await _db.SaveChangesAsync(ct);
        }
        return result;
    }

    private (object? Value, string Display, bool Masked) Present(CustomerFieldValue v, CustomerFieldDefinition d, bool unmask)
    {
        if (v.ValueEncrypted is not null)
        {
            if (!unmask || !d.IsSensitive) return (v.MaskedDisplay, v.MaskedDisplay ?? "****", true);
            var plain = _crypto.Decrypt(v.ValueEncrypted);
            return (plain, plain, false);
        }
        object? value = v.ValueNumber is not null ? v.ValueNumber : v.ValueDate is not null ? v.ValueDate : v.ValueBool is not null ? v.ValueBool
            : v.ValueJson is null ? v.ValueText : JsonSerializer.Deserialize<List<string>>(v.ValueJson);
        var display = v.ValueNumber?.ToString(CultureInfo.InvariantCulture) ?? v.ValueDate?.ToString("yyyy-MM-dd") ?? v.ValueBool?.ToString().ToLowerInvariant() ??
                      (v.ValueJson is null ? v.ValueText ?? "" : string.Join(",", JsonSerializer.Deserialize<List<string>>(v.ValueJson)!));
        if (d.IsSensitive && !unmask) return (FieldValueValidator.Mask(display), FieldValueValidator.Mask(display), true);
        return (value, display, false);
    }

    public async Task<(List<AttributeValueDto> Values, List<(string Code, long HistoryId)> Changes)> UpdateAttributesAsync(long customerId, AttributesUpdateRequest r, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(customerId, ct, tracking: true);
        var from = r.EffectiveFrom ?? _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var changed = await WriteAttributesAsync(c, r.Values, from, r.Reason, r.Source, requireAll: false, ct);
        c.UpdatedAt = _clock.UtcNow;
        c.UpdatedBy = _user.Username;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await AttributesAsync(customerId, null, ct), changed);
    }

    private async Task<List<(string Code, long HistoryId)>> WriteAttributesAsync(Customer c, Dictionary<string, JsonElement> values, DateTime from, string? reason,
        string source, bool requireAll, CancellationToken ct)
    {
        var defs = (await _fields.EffectiveDefinitionsAsync(_clock.UtcNow, ct)).ToDictionary(d => d.Code);
        var errors = new Dictionary<string, List<string>>();
        void Add(string k, string m) { if (!errors.TryGetValue(k, out var l)) errors[k] = l = new(); l.Add(m); }
        var normalized = values.ToDictionary(k => k.Key.Trim().ToUpperInvariant(), v => v.Value);
        var parsed = new Dictionary<string, (CustomerFieldDefinition Def, ParsedFieldValue? Value)>();

        var optionCodes = normalized.Keys.ToList();
        var options = await _db.Set<CustomerFieldOption>().AsNoTracking().Where(o => optionCodes.Contains(o.FieldCode) && o.IsActive)
            .OrderBy(o => o.SortOrder).ToListAsync(ct);
        var countries = await _db.Set<LookupValue>().AsNoTracking().Where(l => l.Category == "COUNTRY" && l.IsActive).Select(l => l.Code).ToListAsync(ct);
        var currencies = await _db.Set<LookupValue>().AsNoTracking().Where(l => l.Category == "CURRENCY" && l.IsActive).Select(l => l.Code).ToListAsync(ct);

        foreach (var (code, raw) in normalized)
        {
            if (!defs.TryGetValue(code, out var def)) { Add(code, $"Unknown or unpublished field '{code}'."); continue; }
            if (!def.AppliesTo(c.CustomerType)) { Add(code, $"Field '{code}' does not apply to customer type {c.CustomerType}."); continue; }
            var opts = options.Where(o => o.FieldCode == code).Select(o => o.OptionCode).ToList();
            var known = def.DataType == FieldDataTypes.Country ? countries : def.DataType == FieldDataTypes.Currency ? currencies : null;
            var (pv, err) = FieldValueValidator.Validate(def, raw, opts, known);
            if (err is not null) { Add(code, err); continue; }
            if (pv is null && def.IsRequiredFor(c.CustomerType)) { Add(code, $"{def.LabelEn} ({code}) is required for {c.CustomerType} customers."); continue; }
            parsed[code] = (def, pv);
        }
        if (requireAll)
        {
            foreach (var def in defs.Values.Where(d => d.IsRequiredFor(c.CustomerType) && !normalized.ContainsKey(d.Code)))
                Add(def.Code, $"{def.LabelEn} ({def.Code}) is required for {c.CustomerType} customers.");
        }
        ValidationException.ThrowIf(errors);

        var changes = new List<(string, CustomerFieldChangeHistory)>();
        foreach (var (code, (def, pv)) in parsed)
        {
            var existing = await _db.Set<CustomerFieldValue>().Where(v => v.CustomerId == c.Id && v.FieldCode == code).OrderBy(v => v.EffectiveFrom).ToListAsync(ct);
            var current = existing.LastOrDefault(v => v.EffectiveFrom <= from && (v.EffectiveTo == null || v.EffectiveTo > from));
            var next = existing.FirstOrDefault(v => v.EffectiveFrom > from);
            var previousDisplay = current is null ? null : DisplayForHistory(current, def);
            if (current is not null)
            {
                if (current.EffectiveFrom == from) { _db.Set<CustomerFieldValue>().Remove(current); }
                else current.EffectiveTo = from;
            }
            CustomerFieldValue? nv = null;
            if (pv is not null)
            {
                nv = new CustomerFieldValue
                {
                    TenantId = c.TenantId, CustomerId = c.Id, FieldCode = code, FieldDefinitionId = def.Id, FieldVersion = def.Version, EffectiveFrom = from,
                    EffectiveTo = next?.EffectiveFrom, Source = source, CreatedAt = _clock.UtcNow, CreatedBy = _user.Username
                };
                if (def.IsEncrypted)
                {
                    nv.ValueEncrypted = _crypto.Encrypt(pv.Display);
                    nv.ValueToken = def.IsSearchable ? _crypto.SearchToken(pv.Display) : null;
                    nv.MaskedDisplay = FieldValueValidator.Mask(pv.Display);
                }
                else
                {
                    nv.ValueText = pv.Text; nv.ValueNumber = pv.Number; nv.ValueDate = pv.Date; nv.ValueBool = pv.Bool;
                    nv.ValueJson = pv.Multi is null ? null : JsonSerializer.Serialize(pv.Multi);
                    nv.MaskedDisplay = def.IsSensitive ? FieldValueValidator.Mask(pv.Display) : null;
                }
                _db.Set<CustomerFieldValue>().Add(nv);
            }
            var newDisplay = pv is null ? null : def.IsSensitive ? FieldValueValidator.Mask(pv.Display) : pv.Display;
            if (previousDisplay == newDisplay && current is not null && current.EffectiveFrom != from) continue;
            var h = new CustomerFieldChangeHistory
            {
                TenantId = c.TenantId, CustomerId = c.Id, FieldCode = code, FieldVersion = def.Version, PreviousValue = previousDisplay, NewValue = newDisplay,
                Actor = _user.Username, TimestampUtc = _clock.UtcNow, Reason = reason, CorrelationId = _corr.CorrelationId
            };
            _db.Set<CustomerFieldChangeHistory>().Add(h);
            changes.Add((code, h));
            _audit.Record("CUSTOMER_ATTRIBUTE_CHANGE", "Customer", c.Id.ToString(), new { field = code, value = previousDisplay },
                new { field = code, value = newDisplay, effectiveFrom = from, fieldVersion = def.Version }, reason);
        }
        await _db.SaveChangesAsync(ct);
        return changes.Select(x => (x.Item1, x.Item2.Id)).ToList();
    }

    private static string? DisplayForHistory(CustomerFieldValue v, CustomerFieldDefinition def)
    {
        if (v.ValueEncrypted is not null || def.IsSensitive) return v.MaskedDisplay;
        return v.ValueNumber?.ToString(CultureInfo.InvariantCulture) ?? v.ValueDate?.ToString("yyyy-MM-dd") ?? v.ValueBool?.ToString().ToLowerInvariant() ?? v.ValueJson ?? v.ValueText;
    }

    public async Task<List<CustomerFieldChangeHistory>> AttributeHistoryAsync(long customerId, string? fieldCode, CancellationToken ct)
    {
        await LoadVisibleAsync(customerId, ct);
        var q = _db.Set<CustomerFieldChangeHistory>().AsNoTracking().Where(h => h.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(fieldCode)) q = q.Where(h => h.FieldCode == fieldCode);
        return await q.OrderByDescending(h => h.TimestampUtc).ThenByDescending(h => h.Id).ToListAsync(ct);
    }

    // ---------------- identifications / contacts / financial profiles ----------------
    public async Task<IdentificationDto> AddIdentificationAsync(long customerId, IdentificationInput i, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(customerId, ct);
        AddIdentification(c, i);
        _audit.Record("CUSTOMER_IDENTIFICATION_ADD", "Customer", c.Id.ToString(), null, new { i.IdType, last4 = i.IdNumber.Length >= 4 ? i.IdNumber[^4..] : "", i.IssuingCountry, i.ExpiryDate });
        await _db.SaveChangesAsync(ct);
        var row = await _db.Set<CustomerIdentification>().AsNoTracking().Where(x => x.CustomerId == customerId).OrderByDescending(x => x.Id).FirstAsync(ct);
        return new IdentificationDto(row.Id, row.IdType, "******" + row.Last4, row.IssuingCountry, row.IssueDate, row.ExpiryDate, row.VerificationStatus,
            row.ExpiryDate < DateOnly.FromDateTime(_clock.UtcNow), null);
    }

    public async Task<ContactDto> AddContactAsync(long customerId, ContactInput i, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(customerId, ct);
        AddContact(c, i);
        _audit.Record("CUSTOMER_CONTACT_ADD", "Customer", c.Id.ToString(), null, new { i.ContactType });
        await _db.SaveChangesAsync(ct);
        var row = await _db.Set<CustomerContact>().AsNoTracking().Where(x => x.CustomerId == customerId).OrderByDescending(x => x.Id).FirstAsync(ct);
        return new ContactDto(row.Id, row.ContactType, row.Masked, row.IsPrimary, null);
    }

    public async Task<List<FinancialProfileDto>> FinancialProfilesAsync(long customerId, DateTime? asOf, CancellationToken ct)
    {
        await LoadVisibleAsync(customerId, ct);
        var q = _db.Set<CustomerFinancialProfile>().AsNoTracking().Where(p => p.CustomerId == customerId);
        if (asOf is { } at) q = q.Where(p => p.EffectiveFrom <= at && (p.EffectiveTo == null || p.EffectiveTo > at));
        return await q.OrderByDescending(p => p.EffectiveFrom).Select(p => new FinancialProfileDto(p.Id, p.DeclaredMonthlyIncome, p.Currency, p.SourceOfFunds,
            p.SourceOfWealth, p.Occupation, p.ExpectedMonthlyVolume, p.ExpectedMonthlyCount, p.EffectiveFrom, p.EffectiveTo, p.DeclaredAt)).ToListAsync(ct);
    }

    public async Task<FinancialProfileDto> AddFinancialProfileAsync(long customerId, FinancialProfileInput i, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(customerId, ct);
        var from = i.EffectiveFrom ?? _clock.UtcNow;
        var open = await _db.Set<CustomerFinancialProfile>().Where(p => p.CustomerId == customerId && p.EffectiveTo == null && p.EffectiveFrom <= from).ToListAsync(ct);
        foreach (var o in open) o.EffectiveTo = from;
        AddFinancialProfile(c, i, from);
        _audit.Record("CUSTOMER_FINANCIAL_PROFILE_ADD", "Customer", c.Id.ToString(), open.Select(o => new { o.DeclaredMonthlyIncome, o.SourceOfFunds }).FirstOrDefault(),
            new { i.DeclaredMonthlyIncome, i.SourceOfFunds, i.SourceOfWealth, effectiveFrom = from });
        await _db.SaveChangesAsync(ct);
        return (await FinancialProfilesAsync(customerId, from, ct)).First();
    }

    // ---------------- ownership ----------------
    public async Task<OwnershipLinkDto> AddOwnershipAsync(long entityId, OwnershipInput i, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(entityId, ct);
        if (c.CustomerType == CustomerTypes.Individual) throw new ValidationException("customer", "Ownership structures apply to corporates, merchants and agents.");
        var validTypes = new[] { "Ownership", "VotingRights", "ControlOtherMeans", "SeniorManagingOfficial", "Signatory", "Director", "ActingOnBehalf" };
        if (!validTypes.Contains(i.ControlType)) throw new ValidationException("controlType", $"Unknown control type. Allowed: {string.Join(", ", validTypes)}.");
        if (i.Percentage is < 0 or > 100) throw new ValidationException("percentage", "Percentage must be between 0 and 100.");
        if (i.OwnerCustomerId is null && string.IsNullOrWhiteSpace(i.OwnerName)) throw new ValidationException("ownerName", "Owner customer or owner name is required.");
        string? ownerName = i.OwnerName;
        var ownerType = i.OwnerType;
        if (i.OwnerCustomerId is { } oid)
        {
            var owner = await LoadVisibleAsync(oid, ct);
            ownerName ??= owner.NameLatin ?? owner.NameArabic;
            ownerType = owner.CustomerType == CustomerTypes.Individual ? "Person" : "Entity";
        }
        var link = new OwnershipLink
        {
            TenantId = c.TenantId, EntityCustomerId = entityId, OwnerCustomerId = i.OwnerCustomerId, OwnerName = ownerName, OwnerNameArabic = i.OwnerNameArabic,
            OwnerType = ownerType, Percentage = i.Percentage, ControlType = i.ControlType, OwnerDateOfBirth = i.OwnerDateOfBirth, OwnerNationality = i.OwnerNationality,
            OwnerIsPep = i.OwnerIsPep, EffectiveFrom = i.EffectiveFrom ?? _clock.UtcNow, Evidence = i.Evidence, CreatedAt = _clock.UtcNow
        };
        _db.Set<OwnershipLink>().Add(link);
        _audit.Record("OWNERSHIP_LINK_ADD", "Customer", entityId.ToString(), null, new { i.OwnerCustomerId, ownerName, ownerType, i.Percentage, i.ControlType });
        await _db.SaveChangesAsync(ct);
        await RefreshOwnershipDataQualityAsync(entityId, ct);
        return ToDto(link);
    }

    private static OwnershipLinkDto ToDto(OwnershipLink l) => new(l.Id, l.EntityCustomerId, l.OwnerCustomerId, l.OwnerName, l.OwnerType, l.Percentage, l.ControlType,
        l.OwnerIsPep, l.ScreeningStatus, l.EffectiveFrom, l.EffectiveTo, l.Evidence);

    public async Task<OwnershipAnalysisDto> OwnershipAsync(long entityId, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(entityId, ct);
        var (analysis, links, threshold, source) = await AnalyzeOwnershipAsync(c, ct);
        return new OwnershipAnalysisDto(threshold, source, links.Where(l => l.EntityCustomerId == entityId).Select(ToDto).ToList(),
            analysis.Owners.Select(o => new BeneficialOwnerDto(o.OwnerKey, o.OwnerName, o.EffectivePercentage, o.ControlTypes, o.Paths, o.IsBeneficialOwner, o.Basis, o.IsPep)).ToList(),
            analysis.Cycles.Distinct().ToList(), analysis.Unresolved.Distinct().ToList(),
            c.CustomerType != CustomerTypes.Individual && !analysis.Owners.Any(o => o.IsBeneficialOwner));
    }

    public async Task<(OwnershipAnalysis Analysis, List<OwnershipLink> Links, decimal Threshold, string Source)> AnalyzeOwnershipAsync(Customer c, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var all = await _db.Set<OwnershipLink>().AsNoTracking().Where(l => l.TenantId == c.TenantId && l.EffectiveFrom <= now && (l.EffectiveTo == null || l.EffectiveTo > now)).ToListAsync(ct);
        var j = await _settings.JurisdictionAsync("UBO_THRESHOLD_PERCENT", now, c.TenantId, ct);
        var threshold = j is not null && decimal.TryParse(j.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var t) ? t : 25m;
        var edges = all.Select(l => new OwnershipEdge(
            l.OwnerCustomerId is { } oc ? $"C{oc}" : $"L{l.Id}", l.OwnerName ?? "", l.OwnerType, $"C{l.EntityCustomerId}", l.Percentage, l.ControlType, l.OwnerIsPep)).ToList();
        var analysis = OwnershipCalculator.Analyze($"C{c.Id}", edges, threshold);
        // Mark PEP from linked customers.
        var personIds = analysis.Owners.Where(o => o.OwnerKey.StartsWith('C')).Select(o => long.Parse(o.OwnerKey[1..])).ToList();
        if (personIds.Count > 0)
        {
            var peps = await _db.Set<Customer>().AsNoTracking().Where(x => personIds.Contains(x.Id) && x.IsPep).Select(x => x.Id).ToListAsync(ct);
            foreach (var o in analysis.Owners.Where(o => o.OwnerKey.StartsWith('C') && peps.Contains(long.Parse(o.OwnerKey[1..])))) o.IsPep = true;
        }
        return (analysis, all, threshold, j is null ? "default (not configured)" : $"{j.Key}={j.Value} [{j.Status}] {j.LegalSource}");
    }

    private async Task RefreshOwnershipDataQualityAsync(long entityId, CancellationToken ct)
    {
        var c = await _db.Set<Customer>().AsNoTracking().FirstAsync(x => x.Id == entityId, ct);
        var (analysis, _, _, _) = await AnalyzeOwnershipAsync(c, ct);
        foreach (var cycle in analysis.Cycles.Distinct())
        {
            var exists = await _db.Set<DataQualityIssue>().AnyAsync(d => d.IssueType == "CircularOwnership" && d.EntityId == entityId.ToString() && d.Status == "Open", ct);
            if (exists) continue;
            _db.Set<DataQualityIssue>().Add(new DataQualityIssue
            {
                TenantId = c.TenantId, IssueType = "CircularOwnership", EntityType = "Customer", EntityId = entityId.ToString(), Description = cycle,
                Severity = "High", CreatedAt = _clock.UtcNow
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    // ---------------- KYC gaps ----------------
    public async Task<List<string>> KycGapsAsync(Customer c, CancellationToken ct)
    {
        var gaps = new List<string>();
        var today = DateOnly.FromDateTime(_clock.UtcNow);
        var level = c.DueDiligenceLevel ?? "CDD";
        var def = await _db.Set<DueDiligenceLevelDefinition>().AsNoTracking().FirstOrDefaultAsync(d => d.Code == level, ct);
        var values = await _db.Set<CustomerFieldValue>().AsNoTracking().Where(v => v.CustomerId == c.Id && v.EffectiveTo == null).Select(v => v.FieldCode).ToListAsync(ct);
        if (def is not null)
        {
            var applicable = (await _fields.EffectiveDefinitionsAsync(_clock.UtcNow, ct)).Where(d => d.AppliesTo(c.CustomerType)).Select(d => d.Code).ToHashSet();
            foreach (var f in RiskScoringEngine.Split(def.RequiredFieldCodes).Where(f => applicable.Contains(f) && !values.Contains(f)))
                gaps.Add($"Missing required {level} field {f}");
            var docs = await _db.Set<CustomerDocument>().AsNoTracking().Where(d => d.CustomerId == c.Id).ToListAsync(ct);
            foreach (var d in RiskScoringEngine.Split(def.RequiredDocuments))
            {
                var doc = docs.Where(x => x.DocumentType == d).OrderByDescending(x => x.ExpiryDate).FirstOrDefault();
                if (doc is null) gaps.Add($"Missing required {level} document {d}");
                else if (doc.ExpiryDate is { } e && e < today) gaps.Add($"Expired document {d} ({e:yyyy-MM-dd})");
            }
            var fp = await _db.Set<CustomerFinancialProfile>().AsNoTracking().Where(p => p.CustomerId == c.Id && p.EffectiveTo == null).FirstOrDefaultAsync(ct);
            if (def.RequiresSourceOfFunds && string.IsNullOrWhiteSpace(fp?.SourceOfFunds)) gaps.Add($"Missing source of funds ({level})");
            if (def.RequiresSourceOfWealth && string.IsNullOrWhiteSpace(fp?.SourceOfWealth)) gaps.Add($"Missing source of wealth ({level})");
        }
        var ids = await _db.Set<CustomerIdentification>().AsNoTracking().Where(i => i.CustomerId == c.Id).ToListAsync(ct);
        if (c.CustomerType == CustomerTypes.Individual && ids.Count == 0) gaps.Add("No identification document recorded");
        foreach (var i in ids.Where(i => i.ExpiryDate is { } e && e < today)) gaps.Add($"Expired identification {i.IdType} ******{i.Last4} ({i.ExpiryDate:yyyy-MM-dd})");
        if (c.CustomerType != CustomerTypes.Individual)
        {
            var hasOwners = await _db.Set<OwnershipLink>().AnyAsync(l => l.EntityCustomerId == c.Id && (l.ControlType == "Ownership" || l.ControlType == "ControlOtherMeans" || l.ControlType == "SeniorManagingOfficial"), ct);
            if (!hasOwners) gaps.Add("Missing beneficial ownership information");
        }
        return gaps;
    }

    // ---------------- documents ----------------
    public async Task<CustomerDocument> AddDocumentMetadataAsync(long customerId, string documentType, string? reference, DateOnly? expiry, CancellationToken ct)
    {
        var c = await LoadVisibleAsync(customerId, ct);
        if (string.IsNullOrWhiteSpace(documentType)) throw new ValidationException("documentType", "Document type is required.");
        var d = new CustomerDocument
        {
            TenantId = c.TenantId, CustomerId = customerId, DocumentType = documentType, Reference = reference, ExpiryDate = expiry,
            Status = expiry is { } e && e < DateOnly.FromDateTime(_clock.UtcNow) ? "Expired" : "Valid", CreatedAt = _clock.UtcNow
        };
        _db.Set<CustomerDocument>().Add(d);
        _audit.Record("CUSTOMER_DOCUMENT_ADD", "Customer", customerId.ToString(), null, new { documentType, reference, expiry });
        await _db.SaveChangesAsync(ct);
        return d;
    }

    public async Task<List<CustomerDocument>> DocumentsAsync(long customerId, CancellationToken ct)
    {
        await LoadVisibleAsync(customerId, ct);
        return await _db.Set<CustomerDocument>().AsNoTracking().Where(d => d.CustomerId == customerId).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
    }

    // ---------------- bulk ----------------
    /// <summary>Bulk officer assignment. All-or-nothing: if any selected customer is not visible the whole request is rejected without revealing which.</summary>
    public async Task<int> BulkAssignAsync(IReadOnlyCollection<long> ids, string officer, string? reason, CancellationToken ct)
    {
        if (ids.Count == 0 || ids.Count > 500) throw new ValidationException("customerIds", "Select between 1 and 500 customers.");
        if (string.IsNullOrWhiteSpace(officer)) throw new ValidationException("officer", "Officer is required.");
        var customers = await _access.Customers().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        if (customers.Count != ids.Distinct().Count())
            throw new NotFoundException("Customer selection", "one or more selected customers are not available");
        foreach (var c in customers)
        {
            var old = c.AssignedOfficer;
            c.AssignedOfficer = officer;
            c.UpdatedAt = _clock.UtcNow;
            _audit.Record("CUSTOMER_BULK_ASSIGN", "Customer", c.Id.ToString(), new { AssignedOfficer = old }, new { AssignedOfficer = officer }, reason);
        }
        await _db.SaveChangesAsync(ct);
        return customers.Count;
    }

    public async Task<List<AuditLog>> HistoryAsync(long customerId, CancellationToken ct)
    {
        await LoadVisibleAsync(customerId, ct);
        var id = customerId.ToString();
        var q = _db.Set<AuditLog>().AsNoTracking().Where(a => a.EntityType == "Customer" && a.EntityId == id);
        if (!_access.CanSeeRestricted) q = q.Where(a => !a.IsRestricted);
        return await q.OrderByDescending(a => a.Id).Take(500).ToListAsync(ct);
    }

    public async Task<List<EntityLink>> RelationshipsAsync(long customerId, CancellationToken ct)
    {
        await LoadVisibleAsync(customerId, ct);
        var visible = _access.Customers().Select(c => c.Id);
        return await _db.Set<EntityLink>().AsNoTracking()
            .Where(l => (l.CustomerAId == customerId && visible.Contains(l.CustomerBId)) || (l.CustomerBId == customerId && visible.Contains(l.CustomerAId)))
            .OrderByDescending(l => l.Confidence).ToListAsync(ct);
    }
}
