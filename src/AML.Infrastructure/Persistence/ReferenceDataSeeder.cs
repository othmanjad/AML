using System.Text.Json;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AML.Infrastructure.Persistence;

/// <summary>
/// Idempotent seed of reference data and SAMPLE configuration. All thresholds, weights, rules, lists and jurisdiction
/// values are examples clearly marked as sample / "to be confirmed by compliance"; none are regulatory requirements.
/// </summary>
public sealed class ReferenceDataSeeder
{
    private readonly AmlDbContext _db;
    private readonly ICryptoService _crypto;
    private readonly IConfiguration _config;
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public ReferenceDataSeeder(AmlDbContext db, ICryptoService crypto, IConfiguration config)
    {
        _db = db; _crypto = crypto; _config = config;
    }

    public async Task<Tenant> SeedAsync(CancellationToken ct = default)
    {
        await SeedSecurityAsync(ct);
        await SeedLookupsAsync(ct);
        var t = await TenantAsync("JO-DEMO", "Demo E-Wallet Company (fictitious)", "RE-TEST-0001", ct);
        await SeedTenantAsync(t, ct);
        var b = await TenantAsync("TENANT-B", "Second Legal Entity (fictitious)", "RE-TEST-0002", ct);
        await SeedTenantAsync(b, ct);
        await SeedUsersAsync(t, b, ct);
        return t;
    }

    private async Task<Tenant> TenantAsync(string code, string name, string re, CancellationToken ct)
    {
        var t = await _db.Set<Tenant>().FirstOrDefaultAsync(x => x.Code == code, ct);
        if (t is not null) return t;
        t = new Tenant { Code = code, Name = name, BaseCurrency = "JOD", TimeZone = "Asia/Amman", WeekendDays = "Friday,Saturday", ReportingEntityId = re };
        _db.Set<Tenant>().Add(t);
        await _db.SaveChangesAsync(ct);
        return t;
    }

    private async Task SeedSecurityAsync(CancellationToken ct)
    {
        var existing = await _db.Set<Permission>().Select(p => p.Code).ToListAsync(ct);
        foreach (var p in Perm.All().Except(existing))
            _db.Set<Permission>().Add(new Permission { Code = p, Module = p.Split('.')[0], Description = p });
        await _db.SaveChangesAsync(ct);
        var perms = await _db.Set<Permission>().ToListAsync(ct);
        foreach (var (code, list) in RoleCodes.Defaults)
        {
            if (await _db.Set<Role>().AnyAsync(r => r.Code == code, ct)) continue;
            var role = new Role { Code = code, Name = code, Description = $"Seeded role {code} (configurable)" };
            _db.Set<Role>().Add(role);
            await _db.SaveChangesAsync(ct);
            foreach (var p in list.Distinct()) _db.Set<RolePermission>().Add(new RolePermission { RoleId = role.Id, PermissionId = perms.First(x => x.Code == p).Id });
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task SeedUsersAsync(Tenant a, Tenant b, CancellationToken ct)
    {
        // Development-only local accounts; password supplied via configuration/environment (never in source control).
        var password = _config["DevAuth:SeedPassword"];
        if (string.IsNullOrWhiteSpace(password) || !string.Equals(_config["DevAuth:Enabled"], "true", StringComparison.OrdinalIgnoreCase)) return;
        var users = new (string User, string Name, string Role, string? Team, string? Scope, Tenant T)[]
        {
            ("admin", "System Administrator (dev)", RoleCodes.SystemAdministrator, "IT", null, a),
            ("manager", "AML Manager (dev)", RoleCodes.AmlManager, "AML", null, a),
            ("officer", "Compliance Officer (dev)", RoleCodes.ComplianceOfficer, "FIU-A", null, a),
            ("officer2", "Compliance Officer 2 (dev)", RoleCodes.ComplianceOfficer, "FIU-A", null, a),
            ("senior", "Senior Reviewer (dev)", RoleCodes.SeniorReviewer, "SeniorReview", null, a),
            ("mlro", "MLRO (dev)", RoleCodes.Mlro, "MLRO", null, a),
            ("investigator", "Investigator (dev)", RoleCodes.Investigator, "FIU-A", null, a),
            ("auditor", "Auditor read-only (dev)", RoleCodes.Auditor, "Audit", null, a),
            ("ops", "Operations User (dev)", RoleCodes.OperationsUser, "Operations", null, a),
            ("regional", "Regional Compliance Officer (dev, North region scope)", RoleCodes.ComplianceOfficer, "North", "NORTH", a),
            ("tenantb", "Tenant B Officer (dev)", RoleCodes.ComplianceOfficer, "FIU-B", null, b),
        };
        var roles = await _db.Set<Role>().ToListAsync(ct);
        foreach (var u in users)
        {
            if (await _db.Set<AppUser>().AnyAsync(x => x.Username == u.User, ct)) continue;
            var au = new AppUser { TenantId = u.T.Id, Username = u.User, DisplayName = u.Name, PasswordHash = _crypto.HashPassword(password), Team = u.Team, DataScopeRegion = u.Scope, CreatedAt = DateTime.UtcNow };
            _db.Set<AppUser>().Add(au);
            await _db.SaveChangesAsync(ct);
            _db.Set<UserRole>().Add(new UserRole { UserId = au.Id, RoleId = roles.First(r => r.Code == u.Role).Id });
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task SeedLookupsAsync(CancellationToken ct)
    {
        if (await _db.Set<LookupValue>().AnyAsync(ct)) return;
        void L(string cat, string code, string en, string ar, int order, string? attr = null) =>
            _db.Set<LookupValue>().Add(new LookupValue { Category = cat, Code = code, LabelEn = en, LabelAr = ar, SortOrder = order, Attribute = attr });
        var countries = new (string C, string En, string Ar, string? A)[]
        {
            ("JO", "Jordan", "الأردن", null), ("SA", "Saudi Arabia", "السعودية", null), ("AE", "United Arab Emirates", "الإمارات", null), ("EG", "Egypt", "مصر", null),
            ("IQ", "Iraq", "العراق", "Elevated"), ("SY", "Syria", "سوريا", "HighRisk"), ("LB", "Lebanon", "لبنان", "Elevated"), ("PS", "Palestine", "فلسطين", null),
            ("KW", "Kuwait", "الكويت", null), ("QA", "Qatar", "قطر", null), ("BH", "Bahrain", "البحرين", null), ("OM", "Oman", "عُمان", null), ("YE", "Yemen", "اليمن", "Elevated"),
            ("TR", "Turkey", "تركيا", null), ("US", "United States", "الولايات المتحدة", null), ("GB", "United Kingdom", "المملكة المتحدة", null), ("DE", "Germany", "ألمانيا", null),
            ("FR", "France", "فرنسا", null), ("IN", "India", "الهند", null), ("PK", "Pakistan", "باكستان", null), ("PH", "Philippines", "الفلبين", null), ("CN", "China", "الصين", null),
            ("IR", "Iran", "إيران", "HighRisk"), ("KP", "North Korea", "كوريا الشمالية", "HighRisk"), ("MM", "Myanmar", "ميانمار", "HighRisk"), ("AF", "Afghanistan", "أفغانستان", "Elevated"),
            ("RU", "Russia", "روسيا", null), ("SD", "Sudan", "السودان", "Elevated"), ("LY", "Libya", "ليبيا", "Elevated"), ("MA", "Morocco", "المغرب", null), ("TN", "Tunisia", "تونس", null),
            ("DZ", "Algeria", "الجزائر", null), ("CA", "Canada", "كندا", null), ("AU", "Australia", "أستراليا", null), ("IT", "Italy", "إيطاليا", null), ("ES", "Spain", "إسبانيا", null)
        };
        var i = 0;
        foreach (var c in countries) L("COUNTRY", c.C, c.En, c.Ar, i++, c.A);
        i = 0;
        foreach (var (c, en, ar) in new[] { ("JOD", "Jordanian Dinar", "دينار أردني"), ("USD", "US Dollar", "دولار أمريكي"), ("EUR", "Euro", "يورو"), ("SAR", "Saudi Riyal", "ريال سعودي"),
                     ("AED", "UAE Dirham", "درهم إماراتي"), ("GBP", "Pound Sterling", "جنيه إسترليني"), ("EGP", "Egyptian Pound", "جنيه مصري"), ("IQD", "Iraqi Dinar", "دينار عراقي"), ("XYZ", "Test currency without rate", "عملة اختبار") })
            L("CURRENCY", c, en, ar, i++);
        foreach (var (c, en, ar) in new[] { ("Individual", "Individual", "فرد"), ("Corporate", "Corporate", "شركة"), ("Merchant", "Merchant", "تاجر"), ("Agent", "Agent", "وكيل") }) L("CUSTOMER_TYPE", c, en, ar, 0);
        foreach (var (c, en, ar) in new[] { ("Prospect", "Prospect", "عميل محتمل"), ("Active", "Active", "نشط"), ("Restricted", "Restricted", "مقيد"), ("Suspended", "Suspended", "موقوف"), ("Dormant", "Dormant", "خامل"), ("Exited", "Exited", "منتهية العلاقة"), ("Rejected", "Rejected", "مرفوض") })
            L("CUSTOMER_STATUS", c, en, ar, 0);
        foreach (var (c, en, ar) in new[] { ("Personal", "Personal", "شخصية"), ("Business", "Business", "تجارية"), ("Agent", "Agent float", "وكيل"), ("Internal", "Internal", "داخلية") }) L("WALLET_TYPE", c, en, ar, 0);
        foreach (var (c, en, ar) in new[] { ("P2P", "Person to person", "تحويل بين الأفراد"), ("CashIn", "Cash in", "إيداع نقدي"), ("CashOut", "Cash out", "سحب نقدي"), ("MerchantPayment", "Merchant payment", "دفع للتاجر"),
                     ("Remittance", "Remittance", "حوالة"), ("BankTransfer", "Bank transfer", "تحويل بنكي"), ("Reversal", "Reversal", "عكس"), ("Refund", "Refund", "استرداد"), ("Topup", "Top-up", "شحن"), ("BalanceInquiry", "Balance inquiry", "استعلام رصيد") })
            L("TXN_TYPE", c, en, ar, 0);
        foreach (var (c, en, ar) in new[] { ("Pending", "Pending", "معلق"), ("Completed", "Completed", "مكتمل"), ("Failed", "Failed", "فاشل"), ("Reversed", "Reversed", "معكوس"), ("PartiallyReversed", "Partially reversed", "معكوس جزئياً"), ("Held", "Held", "محجوز") })
            L("TXN_STATUS", c, en, ar, 0);
        foreach (var (c, en, ar, o) in new[] { ("Low", "Low", "منخفض", 1), ("Medium", "Medium", "متوسط", 2), ("High", "High", "مرتفع", 3), ("Critical", "Critical", "حرج", 4) })
        { L("RISK_LEVEL", c, en, ar, o); L("ALERT_SEVERITY", c, en, ar, o); }
        i = 0;
        foreach (var (c, en, ar) in new[] { (AlertStatuses.New, "New", "جديد"), (AlertStatuses.Assigned, "Assigned", "مُسند"), (AlertStatuses.UnderReview, "Under review", "قيد المراجعة"),
                     (AlertStatuses.PendingInformation, "Pending information", "بانتظار معلومات"), (AlertStatuses.Escalated, "Escalated", "مُصعّد"), (AlertStatuses.PendingClosureApproval, "Pending closure approval", "بانتظار اعتماد الإغلاق"),
                     (AlertStatuses.ConvertedToCase, "Converted to case", "تحول إلى قضية"), (AlertStatuses.ClosedNoSuspicion, "Closed - no suspicion identified", "مغلق - لا اشتباه"),
                     (AlertStatuses.ClosedFalsePositive, "Closed - false positive", "مغلق - إيجابي كاذب"), (AlertStatuses.ClosedDuplicate, "Closed - duplicate", "مغلق - مكرر"), (AlertStatuses.ClosedOther, "Closed - other", "مغلق - أخرى") })
            L("ALERT_STATUS", c, en, ar, i++);
        foreach (var (c, en, ar) in new[] { ("Open", "Open", "مفتوحة"), ("UnderInvestigation", "Under investigation", "قيد التحقيق"), ("PendingApproval", "Pending approval", "بانتظار الاعتماد"), ("Escalated", "Escalated", "مُصعّدة"), ("Closed", "Closed", "مغلقة") })
            L("CASE_STATUS", c, en, ar, 0);
        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedTenantAsync(Tenant t, CancellationToken ct)
    {
        if (await _db.Set<SourceSystem>().IgnoreQueryFilters().AnyAsync(s => s.TenantId == t.Id, ct)) return;
        var secret = _config["Integrations:SeedClientSecret"];
        foreach (var (code, name) in new[] { ("CORE", "Core wallet ledger"), ("WALLET", "Mobile wallet app"), ("CARDS", "Card processor") })
            _db.Set<SourceSystem>().Add(new SourceSystem
            {
                TenantId = t.Id, Code = code, Name = name, ExpectedFeed = code == "CARDS" ? "None" : "Daily",
                ClientId = string.IsNullOrWhiteSpace(secret) ? null : $"{t.Code.ToLowerInvariant()}-{code.ToLowerInvariant()}",
                ClientSecretHash = string.IsNullOrWhiteSpace(secret) ? null : _crypto.HashPassword(secret)
            });
        foreach (var (s, n) in new[] { ("SUCC", "Completed"), ("OK", "Completed"), ("DONE", "Completed"), ("PEND", "Pending"), ("INIT", "Pending"), ("FAIL", "Failed"), ("ERR", "Failed"), ("REV", "Reversed"), ("PREV", "PartiallyReversed") })
            _db.Set<TransactionStatusMapping>().Add(new TransactionStatusMapping { TenantId = t.Id, SourceSystem = "*", SourceStatus = s, NormalizedStatus = n });
        foreach (var (f, r) in new[] { ("USD", 0.709m), ("EUR", 0.77m), ("SAR", 0.189m), ("AED", 0.193m), ("GBP", 0.9m), ("EGP", 0.0146m), ("IQD", 0.00054m) })
            _db.Set<FxRate>().Add(new FxRate { TenantId = t.Id, FromCurrency = f, ToCurrency = "JOD", Rate = r, RateType = "Mid", Source = "SAMPLE TEST DATA - not a market rate", EffectiveFrom = Epoch, IsTestData = true, CreatedBy = "seed", CreatedAt = DateTime.UtcNow });
        foreach (var (d, n) in new[] { (new DateOnly(2026, 1, 1), "New Year's Day (sample)"), (new DateOnly(2026, 5, 1), "Labour Day (sample)"), (new DateOnly(2026, 5, 25), "Independence Day (sample)"), (new DateOnly(2026, 12, 25), "Christmas Day (sample)") })
            _db.Set<Holiday>().Add(new Holiday { TenantId = t.Id, Date = d, Name = n });
        var settings = new Dictionary<string, string>
        {
            ["Screening:TimeoutMs"] = "3000", ["Screening:MatchThreshold"] = "0.85", ["Screening:MaxAttempts"] = "5", ["Realtime:LatencyBudgetMs"] = "300",
            ["Realtime:HoldThreshold"] = "0.85", ["Realtime:RejectThreshold"] = "0.95", ["Realtime:ConfirmedMatchDecision"] = "Reject", ["Realtime:HoldSlaMinutes"] = "30",
            ["Risk:ReassessOnFieldChange"] = "true", ["Risk:ReassessOnReviewCompletion"] = "true", ["Risk:ReviewLeadDays"] = "30", ["Risk:EscalationRole"] = RoleCodes.AmlManager,
            ["Rules:RequireSimulation"] = "true", ["Fields:RequireApproval"] = "false", ["Ingestion:MaxAttempts"] = "3", ["Ingestion:ZeroAmountAllowedTypes"] = "BalanceInquiry,FeeWaiver",
            ["Uploads:MaxBytes"] = "10485760", ["Graph:MaxDepth"] = "3", ["Graph:MaxNodes"] = "200", ["Str:MinNarrativeLength"] = "50", ["Str:ReminderHoursBeforeDue"] = "48",
            ["AuthorityRequests:TriggerReassessment"] = "true", ["Alerts:EscalationTeam"] = "SeniorReview", ["MockCore:FailureMode"] = "None", ["Actions:MaxAttempts"] = "5",
            ["Sanctions:CompetentAuthority"] = "National sanctions authority (to be confirmed by compliance)", ["Cases:DefaultSlaWorkingDays"] = "20",
            ["EntityResolution:NameLinkThreshold"] = "0.95", ["EntityResolution:NameCandidateThreshold"] = "0.85"
        };
        foreach (var (k, v) in settings) _db.Set<SystemSetting>().Add(new SystemSetting { TenantId = t.Id, Key = k, Value = v, UpdatedAt = DateTime.UtcNow, UpdatedBy = "seed" });
        void J(string key, string value, string? unit, string source, string article) => _db.Set<JurisdictionSetting>().Add(new JurisdictionSetting
        {
            TenantId = t.Id, Jurisdiction = "JO", Key = key, Value = value, Unit = unit, LegalSource = source, ArticleReference = article, EffectiveFrom = Epoch,
            Status = "ToBeConfirmed", ComplianceOwner = "Compliance (to be assigned)", CreatedBy = "seed", CreatedAt = DateTime.UtcNow
        });
        J("UBO_THRESHOLD_PERCENT", "25", "Percent", "CBJ AML/CFT Instructions for payment/transfer companies (No. 12 of 2018 or successor) - TO BE CONFIRMED", "Beneficial owner definition - to be confirmed");
        J("STR_FILING_DEADLINE", "2", "WorkingDays", "AML/CFT Law No. 20 of 2021 and AMLU instructions - TO BE CONFIRMED", "Reporting obligation - to be confirmed");
        J("RECORD_RETENTION_YEARS", "5", "Years", "FATF R.11 minimum; national retention period TO BE CONFIRMED", "Record keeping - to be confirmed");
        J("FREEZE_NOTIFICATION_HOURS", "24", "Hours", "National TFS freezing procedure - TO BE CONFIRMED", "Notification of frozen assets - to be confirmed");
        J("R16_THRESHOLD", "1000", "USD", "FATF R.16 (revised June 2025) - national implementation TO BE CONFIRMED", "Originator/beneficiary information - to be confirmed");

        foreach (var (code, en, ar, order) in new[] { ("CORE_DETAILS", "Core details", "البيانات الأساسية", 1), ("FINANCIAL_PROFILE", "Financial profile", "الملف المالي", 2), ("KYC", "Identification / KYC", "التعريف / اعرف عميلك", 3),
                     ("CORPORATE", "Corporate details", "بيانات الشركة", 4), ("AGENT", "Agent / merchant", "الوكيل / التاجر", 5) })
            _db.Set<CustomerFieldGroup>().Add(new CustomerFieldGroup { TenantId = t.Id, Code = code, LabelEn = en, LabelAr = ar, SortOrder = order });
        CustomerFieldDefinition F(string code, string en, string ar, string group, string type, string types = "", string req = "", bool search = false, bool sens = false, bool enc = false, string? val = null, int order = 0) =>
            new() { TenantId = t.Id, Code = code, LabelEn = en, LabelAr = ar, GroupCode = group, DataType = type, CustomerTypes = types, RequiredForTypes = req, IsSearchable = search, IsFilterable = search && !enc,
                IsSensitive = sens, IsEncrypted = enc, ValidationJson = val, SortOrder = order, Status = ConfigStatus.Published, Version = 1, EffectiveFrom = Epoch, CreatedBy = "seed", CreatedAt = DateTime.UtcNow,
                PublishedBy = "seed", PublishedAt = DateTime.UtcNow, IsSample = true };
        _db.Set<CustomerFieldDefinition>().AddRange(
            F("OCCUPATION", "Occupation", "المهنة", "FINANCIAL_PROFILE", FieldDataTypes.SingleSelect, "Individual", search: true, order: 1),
            F("SOURCE_OF_INCOME", "Source of income", "مصدر الدخل", "FINANCIAL_PROFILE", FieldDataTypes.SingleSelect, "Individual", search: true, order: 2),
            F("PRODUCT_TIER", "Product tier", "فئة المنتج", "CORE_DETAILS", FieldDataTypes.SingleSelect, "", search: true, order: 3),
            F("PREFERRED_LANGUAGE", "Preferred language", "اللغة المفضلة", "CORE_DETAILS", FieldDataTypes.ShortText, "", order: 4),
            F("INCORP_REG_NO", "Commercial registration number", "رقم السجل التجاري", "CORPORATE", FieldDataTypes.Identifier, "Corporate,Merchant", search: true, val: "{\"regex\":\"^[0-9]{6,12}$\"}", order: 1),
            F("BUSINESS_ACTIVITY", "Business activity", "النشاط التجاري", "CORPORATE", FieldDataTypes.ShortText, "Corporate,Merchant", order: 2),
            F("AGENT_MCC", "Merchant category code", "رمز فئة التاجر", "AGENT", FieldDataTypes.SingleSelect, "Agent,Merchant", search: true, order: 1),
            F("SECONDARY_ID", "Secondary identity number", "رقم الهوية الثانوي", "KYC", FieldDataTypes.Identifier, "Individual", search: true, sens: true, enc: true, val: "{\"regex\":\"^[0-9]{10}$\"}", order: 1));
        void O(string field, string code, string en, string ar, int order) => _db.Set<CustomerFieldOption>().Add(new CustomerFieldOption { TenantId = t.Id, FieldCode = field, OptionCode = code, LabelEn = en, LabelAr = ar, SortOrder = order, EffectiveFrom = Epoch });
        O("OCCUPATION", "EMPLOYEE", "Employee", "موظف", 1); O("OCCUPATION", "SELF_EMPLOYED", "Self-employed", "أعمال حرة", 2); O("OCCUPATION", "CASH_INTENSIVE", "Cash-intensive business", "نشاط نقدي مكثف", 3);
        O("OCCUPATION", "PUBLIC_OFFICIAL", "Public official", "موظف عام", 4); O("OCCUPATION", "STUDENT", "Student", "طالب", 5); O("OCCUPATION", "UNEMPLOYED", "Unemployed", "عاطل عن العمل", 6);
        O("SOURCE_OF_INCOME", "SALARY", "Salary", "راتب", 1); O("SOURCE_OF_INCOME", "BUSINESS", "Business income", "دخل تجاري", 2); O("SOURCE_OF_INCOME", "REMITTANCES", "Remittances received", "حوالات واردة", 3);
        O("PRODUCT_TIER", "BASIC", "Basic (limited)", "أساسي (محدود)", 1); O("PRODUCT_TIER", "STANDARD", "Standard", "قياسي", 2); O("PRODUCT_TIER", "PREMIUM", "Premium", "مميز", 3);
        O("AGENT_MCC", "6051", "6051 Quasi-cash", "6051 شبه نقدي", 1); O("AGENT_MCC", "4829", "4829 Money transfer", "4829 تحويل أموال", 2); O("AGENT_MCC", "5411", "5411 Grocery", "5411 بقالة", 3);
        _db.Set<CustomerFormLayout>().Add(new CustomerFormLayout { TenantId = t.Id, CustomerType = "Individual", RoleCode = "*", LayoutJson = "[{\"group\":\"CORE_DETAILS\",\"fields\":[\"PRODUCT_TIER\",\"PREFERRED_LANGUAGE\"]},{\"group\":\"FINANCIAL_PROFILE\",\"fields\":[\"OCCUPATION\",\"SOURCE_OF_INCOME\"]},{\"group\":\"KYC\",\"fields\":[\"SECONDARY_ID\"]}]", CreatedAt = DateTime.UtcNow, CreatedBy = "seed" });
        _db.Set<CustomerFormLayout>().Add(new CustomerFormLayout { TenantId = t.Id, CustomerType = "Individual", RoleCode = RoleCodes.OperationsUser, LayoutJson = "[{\"group\":\"CORE_DETAILS\",\"fields\":[\"PREFERRED_LANGUAGE\",\"PRODUCT_TIER\"]},{\"group\":\"KYC\",\"fields\":[\"SECONDARY_ID\"]}]", CreatedAt = DateTime.UtcNow, CreatedBy = "seed" });
        await _db.SaveChangesAsync(ct);

        foreach (var (c, n, ar) in new[] { ("CUSTOMER_PROFILE", "Customer profile", "ملف العميل"), ("GEOGRAPHIC", "Geographic exposure", "التعرض الجغرافي"), ("PRODUCT_CHANNEL", "Product / channel", "المنتج / القناة"),
                     ("TRANSACTION_BEHAVIOUR", "Transaction behaviour", "السلوك المعاملاتي"), ("SCREENING", "Screening / compliance", "الفحص / الامتثال") })
            _db.Set<RiskFactorCategory>().Add(new RiskFactorCategory { TenantId = t.Id, Code = c, Name = n, NameAr = ar });
        await _db.SaveChangesAsync(ct);
        _db.Set<RiskMethodology>().Add(StandardMethodology(t.Id));
        _db.Set<RiskMethodology>().Add(AgentMethodology(t.Id));
        await _db.SaveChangesAsync(ct);

        _db.Set<AcceptancePolicy>().Add(new AcceptancePolicy { TenantId = t.Id, Code = "ACCEPT", Version = 1, Status = ConfigStatus.Active, EffectiveFrom = Epoch, SeniorApprovalRiskLevels = "High,Critical", ConditionalRiskLevels = "", RequireCompleteKyc = true, PepRequiresSeniorApproval = true });
        _db.Set<DueDiligenceLevelDefinition>().AddRange(
            new DueDiligenceLevelDefinition { TenantId = t.Id, Code = "SDD", Name = "Simplified due diligence (sample)", RequiredFieldCodes = "", RequiredDocuments = "", ReviewFrequencyMonths = 36 },
            new DueDiligenceLevelDefinition { TenantId = t.Id, Code = "CDD", Name = "Standard due diligence (sample)", RequiredFieldCodes = "OCCUPATION,SOURCE_OF_INCOME", RequiredDocuments = "", ReviewFrequencyMonths = 24 },
            new DueDiligenceLevelDefinition { TenantId = t.Id, Code = "EDD", Name = "Enhanced due diligence (sample)", RequiredFieldCodes = "OCCUPATION,SOURCE_OF_INCOME", RequiredDocuments = "ProofOfAddress,BankStatement", RequiresSourceOfFunds = true, RequiresSourceOfWealth = true, ApprovalPermission = Perm.CddSeniorApprove, ReviewFrequencyMonths = 12 });
        _db.Set<DueDiligenceSelectionRule>().AddRange(
            new DueDiligenceSelectionRule { TenantId = t.Id, Priority = 1, ConditionType = "Pep", LevelCode = "EDD" },
            new DueDiligenceSelectionRule { TenantId = t.Id, Priority = 2, ConditionType = "RiskLevelIn", ConditionValue = "High,Critical", LevelCode = "EDD" },
            new DueDiligenceSelectionRule { TenantId = t.Id, Priority = 3, ConditionType = "HighRiskJurisdiction", LevelCode = "EDD" },
            new DueDiligenceSelectionRule { TenantId = t.Id, Priority = 4, ConditionType = "ComplexOwnership", ConditionValue = "3", LevelCode = "EDD" },
            new DueDiligenceSelectionRule { TenantId = t.Id, Priority = 5, ConditionType = "FieldEquals", ConditionValue = "PRODUCT_TIER=BASIC", LevelCode = "SDD" },
            new DueDiligenceSelectionRule { TenantId = t.Id, Priority = 99, ConditionType = "Default", LevelCode = "CDD" });

        foreach (var (code, name, type, mandatory) in new[] { ("UN-TEST", "UN Security Council list (TEST DATA)", "Sanctions", true), ("NATIONAL-TEST", "National TFS list (TEST DATA)", "Sanctions", true),
                     ("OFAC-TEST", "OFAC SDN (TEST DATA)", "Sanctions", false), ("PF-TEST", "Proliferation financing list (TEST DATA)", "ProliferationFinancing", true), ("PEP-TEST", "PEP list (TEST DATA)", "PEP", false),
                     ("ADVERSE-TEST", "Adverse media (TEST DATA)", "AdverseMedia", false), ("INTERNAL", "Internal watchlist", "Internal", false) })
            _db.Set<WatchlistSource>().Add(new WatchlistSource { TenantId = t.Id, Code = code, Name = name, ListType = type, IsMandatory = mandatory, CurrentVersion = "TEST-0", IsTestData = true });
        _db.Set<ScreeningProviderStatus>().Add(new ScreeningProviderStatus { TenantId = t.Id, ProviderCode = "INTERNAL-LISTS", IsAvailable = true, SimulationMode = "Normal" });
        await _db.SaveChangesAsync(ct);
        var srcs = await _db.Set<WatchlistSource>().IgnoreQueryFilters().Where(s => s.TenantId == t.Id).ToListAsync(ct);
        void E(string src, string id, string name, string category, string? aliases = null, DateOnly? dob = null, string? nat = null)
        {
            var s = srcs.First(x => x.Code == src);
            var al = aliases?.Split('|').ToList();
            _db.Set<WatchlistEntry>().Add(new WatchlistEntry
            {
                TenantId = t.Id, SourceId = s.Id, SourceCode = src, EntryId = id, PrimaryName = name, AliasesJson = al is null ? null : JsonSerializer.Serialize(al), DateOfBirth = dob, Nationality = nat,
                Category = category, VersionAdded = "TEST-0", IsTestData = true, NameSearchKey = Domain.Services.ArabicNameNormalizer.SearchKey(new[] { name }.Concat(al ?? new()).ToArray()),
                EntryHash = _crypto.IntegrityHash($"{name}|{string.Join(";", al ?? new())}|{dob:yyyy-MM-dd}|{nat}|{category}")
            });
        }
        E("UN-TEST", "UN-T-0001", "ACME TERROR LTD", "Terrorism");
        E("UN-TEST", "UN-T-0002", "Zorvan Testlisted Qadir", "Sanctions", "Zorvan Qadir", new DateOnly(1970, 3, 3), "SY");
        E("NATIONAL-TEST", "NAT-T-0001", "Fictitious Sanctioned Trading Est", "Sanctions");
        E("PF-TEST", "PF-T-0001", "Nonexistent Missile Parts Co", "ProliferationFinancing");
        E("PEP-TEST", "PEP-T-0001", "Testminister Kareem Fakename", "PEP", "Kareem Fakename|كريم فيكنيم");
        E("OFAC-TEST", "OFAC-T-0001", "Mohammed Testlisted Example", "Sanctions", null, new DateOnly(1980, 1, 1), "IR");
        _db.Set<ComplianceActionPolicy>().AddRange(
            new ComplianceActionPolicy { TenantId = t.Id, TriggerType = "ConfirmedMandatoryListMatch", Categories = "Sanctions,Terrorism,ProliferationFinancing", ActionType = ActionTypes.FreezeWallet, IsAutomatic = true, RequiresApproval = false },
            new ComplianceActionPolicy { TenantId = t.Id, TriggerType = "RuleAction", ActionType = ActionTypes.HoldTransaction, IsAutomatic = true, RequiresApproval = false },
            new ComplianceActionPolicy { TenantId = t.Id, TriggerType = "Manual", ActionType = ActionTypes.FreezeWallet, RequiresApproval = true },
            new ComplianceActionPolicy { TenantId = t.Id, TriggerType = "Manual", ActionType = ActionTypes.SuspendAgent, RequiresApproval = true },
            new ComplianceActionPolicy { TenantId = t.Id, TriggerType = "Manual", ActionType = ActionTypes.RequireKycRefresh, RequiresApproval = false });
        _db.Set<RealtimeFallbackPolicy>().AddRange(
            new RealtimeFallbackPolicy { TenantId = t.Id, Priority = 20, MinAmount = 1000, Mode = "FailClosed", ClosedDecision = RealtimeDecisions.Reject },
            new RealtimeFallbackPolicy { TenantId = t.Id, Priority = 10, TransactionType = "P2P", MaxAmount = 200, Mode = "FailOpen" },
            new RealtimeFallbackPolicy { TenantId = t.Id, Priority = 0, Mode = "FailClosed", ClosedDecision = RealtimeDecisions.Hold });
        _db.Set<PaymentTransparencyRule>().AddRange(
            new PaymentTransparencyRule { TenantId = t.Id, Code = "R16-XB", Corridor = "CrossBorder", TransactionTypes = "Remittance,P2P,BankTransfer", ThresholdAmount = 1000, ThresholdCurrency = "USD", RequiredFields = "OriginatorName,OriginatorAddress,OriginatorDateOfBirth,BeneficiaryName,BeneficiaryAccount", AlwaysRequiredFields = "OriginatorName,BeneficiaryName", EffectiveFrom = Epoch },
            new PaymentTransparencyRule { TenantId = t.Id, Code = "R16-DOM", Corridor = "Domestic", TransactionTypes = "BankTransfer,Remittance", ThresholdAmount = 0, ThresholdCurrency = "JOD", RequiredFields = "BeneficiaryName,BeneficiaryAccount", AlwaysRequiredFields = "OriginatorName", EffectiveFrom = Epoch });
        foreach (var (sev, days) in new[] { ("Low", 10), ("Medium", 5), ("High", 3), ("Critical", 1) }) _db.Set<AlertSlaPolicy>().Add(new AlertSlaPolicy { TenantId = t.Id, Severity = sev, WorkingDays = days });
        _db.Set<AlertClosurePolicy>().AddRange(
            new AlertClosurePolicy { TenantId = t.Id, Code = "HIGH_SEVERITY_4EYES", ConditionType = "Severity", ConditionValues = "High,Critical", RequiresSecondApproval = true, MinNarrativeLength = 20 },
            new AlertClosurePolicy { TenantId = t.Id, Code = "HIGH_RISK_CUSTOMER_4EYES", ConditionType = "CustomerRiskLevel", ConditionValues = "High,Critical", RequiresSecondApproval = true, MinNarrativeLength = 20 },
            new AlertClosurePolicy { TenantId = t.Id, Code = "DISPOSITION_CHECKLIST", ConditionType = "Any", RequiresSecondApproval = false, RequiredChecklistItems = "reviewedTransactions,reviewedCustomerProfile", MinNarrativeLength = 10 });
        foreach (var (f, to) in new[] { ("Open", "UnderInvestigation"), ("Open", "Closed"), ("UnderInvestigation", "PendingApproval"), ("UnderInvestigation", "Escalated"), ("UnderInvestigation", "Closed"),
                     ("Escalated", "UnderInvestigation"), ("PendingApproval", "UnderInvestigation"), ("Closed", "UnderInvestigation") })
            _db.Set<CaseStatusTransition>().Add(new CaseStatusTransition { TenantId = t.Id, FromStatus = f, ToStatus = to });
        _db.Set<RetentionPolicy>().AddRange(
            new RetentionPolicy { TenantId = t.Id, RecordClass = "Customer", RetentionMonths = 60, ClockEvent = "RelationshipEnd", Disposal = "Anonymise", LegalSource = "FATF R.11 minimum; national period to be confirmed" },
            new RetentionPolicy { TenantId = t.Id, RecordClass = "Transaction", RetentionMonths = 60, ClockEvent = "TransactionDate", Disposal = "Delete", LegalSource = "To be confirmed" },
            new RetentionPolicy { TenantId = t.Id, RecordClass = "Case", RetentionMonths = 60, ClockEvent = "CaseClosure", Disposal = "Delete", LegalSource = "To be confirmed" },
            new RetentionPolicy { TenantId = t.Id, RecordClass = "AuditLog", RetentionMonths = 84, ClockEvent = "Creation", Disposal = "Delete", LegalSource = "Institutional policy - to be confirmed" });
        foreach (var (c, n, r) in new[] { ("STRUCTURING", "Structuring below thresholds", "High"), ("RAPID_MOVEMENT", "Rapid movement of funds", "High"), ("MULE_ACCOUNTS", "Money mule accounts", "High"),
                     ("FUNNEL_ACCOUNTS", "Funnel accounts (many-to-one)", "Medium"), ("AGENT_ABUSE", "Agent network abuse", "Medium"), ("CIRCULAR_FLOWS", "Circular / round-trip flows", "Medium"),
                     ("TRADE_BASED", "Trade-based money laundering", "Medium"), ("PF_EVASION", "Proliferation financing sanctions evasion", "High") })
            _db.Set<Typology>().Add(new Typology { TenantId = t.Id, Code = c, Name = n, RiskRating = r });
        _db.Set<TypologyControl>().Add(new TypologyControl { TenantId = t.Id, TypologyCode = "PF_EVASION", ControlType = "Control", ControlReference = "PF list screening (PF-TEST) with delta rescreening" });
        _db.Set<QaSamplingConfig>().AddRange(
            new QaSamplingConfig { TenantId = t.Id, Code = "FP_10PCT", Outcome = AlertStatuses.ClosedFalsePositive, Percentage = 10, Seed = 42 },
            new QaSamplingConfig { TenantId = t.Id, Code = "NOSUSP_5PCT", Outcome = AlertStatuses.ClosedNoSuspicion, Percentage = 5, Seed = 7 });
        _db.Set<InstitutionalRiskAssessment>().AddRange(
            new InstitutionalRiskAssessment { TenantId = t.Id, Period = "2026 (sample)", Dimension = "Product", Item = "Mobile wallet P2P", InherentRisk = "High", ControlEffectiveness = "Adequate", ResidualRisk = "Medium", CreatedAt = DateTime.UtcNow },
            new InstitutionalRiskAssessment { TenantId = t.Id, Period = "2026 (sample)", Dimension = "Channel", Item = "Agent network", InherentRisk = "High", ControlEffectiveness = "Needs improvement", ResidualRisk = "High", CreatedAt = DateTime.UtcNow },
            new InstitutionalRiskAssessment { TenantId = t.Id, Period = "2026 (sample)", Dimension = "Geography", Item = "Cross-border remittances", InherentRisk = "High", ControlEffectiveness = "Adequate", ResidualRisk = "Medium", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(ct);
        await SeedRulesAsync(t.Id, ct);
    }

    private static RiskMethodology StandardMethodology(long tenant)
    {
        var m = new RiskMethodology
        {
            TenantId = tenant, Code = "CRA-STD", Name = "Customer risk methodology - SAMPLE (not regulatory)", Version = 1, Status = ConfigStatus.Approved, AggregationMethod = AggregationMethods.WeightedAverage,
            ScorePrecision = 2, AppliesToCustomerTypes = "Individual,Corporate,Merchant", ApprovalRequiredLevels = "", EffectiveFrom = Epoch, Owner = "Compliance", IsSample = true,
            Rationale = "Seeded example methodology; weights/thresholds are illustrative and must be calibrated and approved.", CreatedBy = "seed", CreatedAt = DateTime.UtcNow, SubmittedBy = "seed", ApprovedBy = "seed-approver", ApprovedAt = DateTime.UtcNow
        };
        m.Factors.Add(Factor(tenant, "NATIONALITY", "Nationality risk", "GEOGRAPHIC", "customer.nationality", 2, 60, null, "",
            Map("In", "IR,KP,MM,SY", 100, 10, "High-risk jurisdictions (sample)"), Map("In", "AF,YE,IQ,LB,SD,LY", 70, 5, "Elevated (sample)"), Map("Default", null, 10, 0)));
        m.Factors.Add(Factor(tenant, "RESIDENCE", "Residence risk", "GEOGRAPHIC", "customer.residence", 1, 60, null, "",
            Map("In", "IR,KP,MM,SY", 100, 10), Map("In", "AF,YE,IQ,LB,SD,LY", 70, 5), Map("Default", null, 10, 0)));
        m.Factors.Add(Factor(tenant, "CUSTOMER_TYPE", "Customer type", "CUSTOMER_PROFILE", "customer.type", 1, 50, null, "",
            Map("Equals", "Individual", 20, 1), Map("Equals", "Corporate", 40, 1), Map("Equals", "Merchant", 50, 1), Map("Default", null, 50, 0)));
        m.Factors.Add(Factor(tenant, "OCCUPATION", "Occupation", "CUSTOMER_PROFILE", "field:OCCUPATION", 1, 50, null, "Individual",
            Map("Equals", "CASH_INTENSIVE", 80, 1), Map("Equals", "PUBLIC_OFFICIAL", 70, 1), Map("Equals", "EMPLOYEE", 20, 1), Map("Default", null, 40, 0)));
        m.Factors.Add(Factor(tenant, "DECLARED_INCOME", "Declared monthly income", "CUSTOMER_PROFILE", "profile.declaredMonthlyIncome", 1, 50, 730, "Individual",
            Map("Range", null, 40, 1, null, 0, 499.9999m), Map("Range", null, 20, 1, null, 500, 5000), Map("Range", null, 30, 1, null, 5000.0001m, null)));
        m.Factors.Add(Factor(tenant, "PEP", "Politically exposed person", "SCREENING", "screening.pep", 2, 100, null, "", Map("Boolean", "true", 100, 1), Map("Boolean", "false", 0, 1)));
        m.Factors.Add(Factor(tenant, "UBO_PEP", "PEP beneficial owner", "SCREENING", "ubo.pepOwner", 1, 80, null, "Corporate,Merchant", Map("Boolean", "true", 100, 1), Map("Boolean", "false", 0, 1)));
        var vol = Factor(tenant, "VOLUME_VS_EXPECTED", "Activity vs expected volume", "TRANSACTION_BEHAVIOUR", "txn.volumeVsExpected", 1, null, null, "",
            Map("Range", null, 10, 1, null, 0, 1.5m), Map("Range", null, 50, 1, null, 1.5001m, 3), Map("Range", null, 90, 1, null, 3.0001m, null));
        vol.MissingDataHandling = "ExcludeWithWarning";
        m.Factors.Add(vol);
        m.Factors.Add(Factor(tenant, "CROSS_BORDER", "Cross-border activity share", "TRANSACTION_BEHAVIOUR", "txn.crossBorderRatio90d", 1, 30, null, "",
            Map("Range", null, 10, 1, null, 0, 20), Map("Range", null, 50, 1, null, 20.0001m, 60), Map("Range", null, 80, 1, null, 60.0001m, null)));
        AddThresholds(m, tenant);
        m.Overrides.Add(new RiskOverridePolicy { TenantId = tenant, Code = "PEP_MIN_HIGH", ConditionDataSource = "screening.pep", ConditionOperator = "eq", ConditionValue = "true", MinimumLevel = RiskLevels.High, Reason = "PEP relationship requires at least High (sample policy)", Priority = 10, EffectiveFrom = Epoch });
        m.Overrides.Add(new RiskOverridePolicy { TenantId = tenant, Code = "SANCTIONS_CONFIRMED", ConditionDataSource = "screening.sanctionsConfirmed", ConditionOperator = "eq", ConditionValue = "true", MinimumLevel = RiskLevels.Critical, Reason = "Confirmed sanctions/TF/PF match - sanctions workflow", Priority = 100, IsSanctionsWorkflow = true, EffectiveFrom = Epoch });
        m.Floors.Add(new RiskCategoryFloor { TenantId = tenant, CategoryCode = "GEOGRAPHIC", TriggerLevel = RiskLevels.High, MinimumLevel = RiskLevels.High });
        return m;
    }

    private static RiskMethodology AgentMethodology(long tenant)
    {
        var m = new RiskMethodology
        {
            TenantId = tenant, Code = "CRA-AGENT", Name = "Agent & merchant risk methodology - SAMPLE", Version = 1, Status = ConfigStatus.Approved, AggregationMethod = AggregationMethods.WeightedAverage,
            AppliesToCustomerTypes = "Agent", EffectiveFrom = Epoch, IsSample = true, Rationale = "Seeded example for agents", CreatedBy = "seed", CreatedAt = DateTime.UtcNow, SubmittedBy = "seed", ApprovedBy = "seed-approver", ApprovedAt = DateTime.UtcNow
        };
        m.Factors.Add(Factor(tenant, "AGENT_MCC", "Business type / MCC", "CUSTOMER_PROFILE", "field:AGENT_MCC", 2, 60, null, "", Map("In", "6051,4829", 80, 1), Map("Default", null, 30, 0)));
        m.Factors.Add(Factor(tenant, "AGENT_LOCATION", "Agent location / region", "GEOGRAPHIC", "customer.region", 1, 50, null, "", Map("In", "BORDER", 80, 1), Map("Default", null, 30, 0)));
        m.Factors.Add(Factor(tenant, "AGENT_CASHOUT", "Cash-out share", "TRANSACTION_BEHAVIOUR", "agent.cashOutRatio90d", 2, 50, null, "",
            Map("Range", null, 20, 1, null, 0, 60), Map("Range", null, 60, 1, null, 60.0001m, 85), Map("Range", null, 90, 1, null, 85.0001m, null)));
        m.Factors.Add(Factor(tenant, "AGENT_ONBOARDING", "Customers onboarded in 30 days", "PRODUCT_CHANNEL", "agent.newCustomers30d", 1, 40, null, "",
            Map("Range", null, 10, 1, null, 0, 50), Map("Range", null, 60, 1, null, 51, 200), Map("Range", null, 90, 1, null, 201, null)));
        m.Factors.Add(Factor(tenant, "AGENT_SCREENING", "Agent screening", "SCREENING", "screening.pep", 1, 100, null, "", Map("Boolean", "true", 100, 1), Map("Boolean", "false", 0, 1)));
        AddThresholds(m, tenant);
        m.Overrides.Add(new RiskOverridePolicy { TenantId = tenant, Code = "SANCTIONS_CONFIRMED", ConditionDataSource = "screening.sanctionsConfirmed", ConditionOperator = "eq", ConditionValue = "true", MinimumLevel = RiskLevels.Critical, Reason = "Confirmed sanctions match", Priority = 100, IsSanctionsWorkflow = true, EffectiveFrom = Epoch });
        return m;
    }

    private static void AddThresholds(RiskMethodology m, long tenant)
    {
        foreach (var (lvl, min, max, months, rank) in new[] { (RiskLevels.Low, 0m, 24.99m, 36, 0), (RiskLevels.Medium, 25m, 49.99m, 24, 1), (RiskLevels.High, 50m, 74.99m, 6, 2), (RiskLevels.Critical, 75m, 100m, 3, 3) })
            m.Thresholds.Add(new RiskThreshold { TenantId = tenant, Level = lvl, MinScore = min, MaxScore = max, ReviewMonths = months, Rank = rank, IsSample = true, EffectiveFrom = Epoch });
    }

    private static RiskFactorDefinition Factor(long tenant, string code, string name, string cat, string ds, decimal weight, decimal? missing, int? fresh, string applies, params RiskFactorScoreMapping[] maps)
    {
        var f = new RiskFactorDefinition
        {
            TenantId = tenant, Code = code, Name = name, CategoryCode = cat, DataSource = ds, Weight = weight, MissingDataHandling = "AssignScore", MissingDataScore = missing, FreshnessDays = fresh,
            ApplicableCustomerTypes = applies, EffectiveFrom = Epoch, FactorType = maps.Any(x => x.MatchType == "Range") ? "Numeric" : maps.Any(x => x.MatchType == "Boolean") ? "Boolean" : "Categorical"
        };
        foreach (var mp in maps) { mp.TenantId = tenant; f.Mappings.Add(mp); }
        return f;
    }

    private static RiskFactorScoreMapping Map(string type, string? value, decimal score, int priority, string? label = null, decimal? min = null, decimal? max = null) =>
        new() { MatchType = type, Value = value, Score = score, Priority = priority, Label = label, RangeMin = min, RangeMax = max, EffectiveFrom = Epoch };

    public static readonly (string Code, string Name, string Category, string Severity, string Typology, string Json)[] SampleRules =
    {
        ("HIGH_VALUE_PROFILE", "High activity compared with declared profile", "ProfileDeviation", "Medium", "MULE_ACCOUNTS",
            """{"subject":"Customer","window":{"type":"Calendar","unit":"Month"},"condition":{"aggregate":"profileRatio","profileField":"profile.expectedMonthlyVolume","operator":"gt","value":3},"action":{"severity":"Medium"}}"""),
        ("UNUSUAL_FREQUENCY", "Unusual transaction frequency", "Frequency", "Medium", "MULE_ACCOUNTS",
            """{"subject":"Customer","window":{"type":"Rolling","hours":24},"condition":{"aggregate":"count","operator":"gt","value":20}}"""),
        ("STRUCTURING", "Multiple transactions just below a threshold", "Structuring", "High", "STRUCTURING",
            """{"subject":"Customer","window":{"type":"Rolling","hours":24},"condition":{"aggregate":"count","filter":{"field":"txn.baseAmount","operator":"between","value":[900,999.99]},"operator":"gte","value":3},"action":{"severity":"High"}}"""),
        ("RAPID_MOVEMENT", "Rapid movement of incoming funds to outgoing transfers", "RapidMovement", "High", "RAPID_MOVEMENT",
            """{"subject":"Customer","window":{"type":"Rolling","hours":24},"condition":{"aggregate":"rapidMovement","maxMinutes":30,"operator":"gte","value":90}}"""),
        ("MANY_TO_ONE", "Many senders to one receiver", "Funnel", "Medium", "FUNNEL_ACCOUNTS",
            """{"subject":"Customer","window":{"type":"Rolling","hours":24},"condition":{"aggregate":"distinctCounterparties","direction":"in","operator":"gte","value":5}}"""),
        ("ONE_TO_MANY", "One sender to many receivers", "Fanout", "Medium", "MULE_ACCOUNTS",
            """{"subject":"Customer","window":{"type":"Rolling","hours":24},"condition":{"aggregate":"distinctCounterparties","direction":"out","operator":"gte","value":5}}"""),
        ("DORMANT_SPIKE", "Dormant customer activity spike", "Dormancy", "Medium", "MULE_ACCOUNTS",
            """{"subject":"Customer","window":{"type":"Rolling","days":1},"lookbackDays":400,"condition":{"op":"and","conditions":[{"aggregate":"dormancyDays","operator":"gte","value":180},{"field":"txn.baseAmount","operator":"gt","value":500}]}}"""),
        ("GEO_DEVIATION", "Transfers to new destination countries", "Geographic", "Medium", "MULE_ACCOUNTS",
            """{"subject":"Customer","window":{"type":"Rolling","days":7},"lookbackDays":90,"condition":{"aggregate":"newCountries","direction":"out","operator":"gte","value":1}}"""),
        ("HIGH_REVERSAL_RATE", "High reversal/refund rate", "Reversals", "Low", "MULE_ACCOUNTS",
            """{"subject":"Customer","reversalHandling":"Include","window":{"type":"Rolling","days":30},"condition":{"op":"and","conditions":[{"aggregate":"count","operator":"gte","value":5},{"aggregate":"ratio","measure":"count","numerator":{"field":"txn.status","operator":"in","value":["Reversed","PartiallyReversed"]},"operator":"gte","value":30}]}}"""),
        ("AGENT_REPEAT", "Repeated transactions through agents", "AgentActivity", "Low", "AGENT_ABUSE",
            """{"subject":"Customer","window":{"type":"Rolling","days":7},"condition":{"aggregate":"count","filter":{"field":"txn.channel","operator":"eq","value":"Agent"},"operator":"gte","value":10}}"""),
        ("CIRCULAR_FLOW", "Circular / related-party flow", "Circular", "High", "CIRCULAR_FLOWS",
            """{"subject":"Customer","window":{"type":"Rolling","days":7},"condition":{"aggregate":"cycle","maxDepth":4,"operator":"eq","value":1}}"""),
        ("AGENT_CASHOUT_RATIO", "Agent cash-out ratio", "Agent", "Medium", "AGENT_ABUSE",
            """{"subject":"Agent","window":{"type":"Rolling","days":30},"condition":{"op":"and","conditions":[{"aggregate":"count","operator":"gte","value":10},{"aggregate":"ratio","measure":"amount","numerator":{"field":"txn.type","operator":"eq","value":"CashOut"},"operator":"gte","value":90}]}}"""),
        ("AGENT_ONBOARDING_SPIKE", "Many new customers onboarded by one agent", "Agent", "Medium", "AGENT_ABUSE",
            """{"subject":"Agent","window":{"type":"Rolling","days":7},"condition":{"aggregate":"newCustomers","operator":"gt","value":50}}"""),
        ("AGENT_PEER_DEVIATION", "Agent volume versus peer group", "Agent", "Medium", "AGENT_ABUSE",
            """{"subject":"Agent","window":{"type":"Rolling","days":30},"condition":{"aggregate":"peerRatio","peerGroup":"typeAndRegion","operator":"gte","value":3}}"""),
        ("AGENT_OUT_OF_HOURS", "Agent activity outside expected hours", "Agent", "Low", "AGENT_ABUSE",
            """{"subject":"Agent","window":{"type":"Rolling","hours":1},"condition":{"op":"or","conditions":[{"field":"txn.localHour","operator":"lt","value":8},{"field":"txn.localHour","operator":"gte","value":22}]}}"""),
        ("DEVICE_SHARED", "Many customers on one device", "Device", "Medium", "MULE_ACCOUNTS",
            """{"subject":"Device","window":{"type":"Rolling","days":30},"condition":{"aggregate":"customersPerDevice","operator":"gte","value":5}}"""),
        ("RT_DAILY_LIMIT", "Real-time daily outgoing total (pre-computed aggregates)", "Realtime", "High", "STRUCTURING",
            """{"subject":"Customer","realtimeCapable":true,"window":{"type":"Calendar","unit":"Day"},"condition":{"aggregate":"sum","operator":"gt","value":10000},"action":{"severity":"High","complianceAction":"HoldTransaction"}}"""),
    };

    private async Task SeedRulesAsync(long tenant, CancellationToken ct)
    {
        foreach (var (code, name, cat, sev, typ, json) in SampleRules)
        {
            var r = new AmlRule
            {
                TenantId = tenant, Code = code, Name = name + " (SAMPLE)", Description = "Seeded sample scenario - disabled until reviewed, calibrated and approved.", Category = cat, Severity = sev,
                TypologyCodes = typ, IsSample = true, IsRealtimeCapable = json.Contains("\"realtimeCapable\":true"), CreatedAt = DateTime.UtcNow, CreatedBy = "seed"
            };
            r.Versions.Add(new AmlRuleVersion
            {
                TenantId = tenant, Version = 1, Status = ConfigStatus.Disabled, DefinitionJson = json, Severity = sev, EffectiveFrom = Epoch, CreatedBy = "seed", CreatedAt = DateTime.UtcNow,
                Rationale = "Sample scenario; thresholds illustrative only", ValidationResult = "Valid"
            });
            _db.Set<AmlRule>().Add(r);
        }
        await _db.SaveChangesAsync(ct);
    }
}
