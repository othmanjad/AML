using System.Reflection;
using AML.Application.Common;
using AML.Domain.Common;
using AML.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AML.Infrastructure.Persistence;

public sealed class AmlDbContext : DbContext, IAmlDbContext
{
    private readonly ICurrentUser? _user;

    public AmlDbContext(DbContextOptions<AmlDbContext> options, ICurrentUser? user = null) : base(options) => _user = user;

    /// <summary>Evaluated per query by the global tenant filter.</summary>
    public long CurrentTenantId => _user?.TenantId ?? 0;
    public bool BypassTenantFilter => _user is null || _user.IsSystem;

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<decimal>().HavePrecision(19, 4);
        b.Properties<DateTime>().HaveConversion<UtcConverter>();
        b.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private sealed class UtcConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcConverter() : base(v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
    }

    private sealed class NullableUtcConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcConverter() : base(v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v) { }
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        var entityTypes = typeof(Entity).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(Entity).IsAssignableFrom(t)).ToList();
        foreach (var t in entityTypes) m.Entity(t).ToTable(Plural(t.Name));

        foreach (var t in entityTypes.Where(t => typeof(ITenantScoped).IsAssignableFrom(t)))
            GetType().GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(t).Invoke(this, new object[] { m });
        foreach (var t in entityTypes.Where(t => typeof(IConcurrencyAware).IsAssignableFrom(t)))
            m.Entity(t).Property(nameof(IConcurrencyAware.RowVersion)).IsRowVersion();

        // ---------- security ----------
        m.Entity<Tenant>(e => { e.HasIndex(x => x.Code).IsUnique(); S(e.Property(x => x.Code), 50); });
        m.Entity<AppUser>(e =>
        {
            S(e.Property(x => x.Username), 100); e.HasIndex(x => x.Username).IsUnique();
            e.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Role>(e => { S(e.Property(x => x.Code), 64); e.HasIndex(x => x.Code).IsUnique(); e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade); });
        m.Entity<Permission>(e => { S(e.Property(x => x.Code), 100); e.HasIndex(x => x.Code).IsUnique(); });
        m.Entity<UserRole>(e => { e.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique(); e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<RolePermission>(e => { e.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique(); e.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<UserPreference>(e => { S(e.Property(x => x.Key), 100); e.HasIndex(x => new { x.UserId, x.Key }).IsUnique(); e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        m.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs", t => t.HasTrigger("TR_AuditLogs_Protect"));
            S(e.Property(x => x.EntityType), 100); S(e.Property(x => x.EntityId), 200); S(e.Property(x => x.Action), 100); S(e.Property(x => x.Actor), 200);
            e.HasIndex(x => new { x.EntityType, x.EntityId }); e.HasIndex(x => x.TimestampUtc); e.HasIndex(x => x.Action);
        });

        // ---------- configuration ----------
        m.Entity<LookupValue>(e => { S(e.Property(x => x.Category), 50); S(e.Property(x => x.Code), 50); e.HasIndex(x => new { x.Category, x.Code }).IsUnique(); });
        m.Entity<SourceSystem>(e => { S(e.Property(x => x.Code), 50); S(e.Property(x => x.ClientId), 100); e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); e.HasIndex(x => x.ClientId).IsUnique().HasFilter("[ClientId] IS NOT NULL"); });
        m.Entity<SystemSetting>(e => { S(e.Property(x => x.Key), 100); e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique(); });
        m.Entity<JurisdictionSetting>(e => { S(e.Property(x => x.Key), 100); e.HasIndex(x => new { x.TenantId, x.Key, x.EffectiveFrom }); });
        m.Entity<FxRate>(e =>
        {
            e.Property(x => x.Rate).HasPrecision(19, 8); S(e.Property(x => x.FromCurrency), 3); S(e.Property(x => x.ToCurrency), 3);
            e.HasIndex(x => new { x.TenantId, x.FromCurrency, x.ToCurrency, x.EffectiveFrom, x.Version }).IsUnique();
        });
        m.Entity<Holiday>(e => e.HasIndex(x => new { x.TenantId, x.Date }).IsUnique());
        m.Entity<TransactionStatusMapping>(e => { S(e.Property(x => x.SourceSystem), 50); S(e.Property(x => x.SourceStatus), 50); e.HasIndex(x => new { x.TenantId, x.SourceSystem, x.SourceStatus }).IsUnique(); });

        // ---------- customers ----------
        m.Entity<Customer>(e =>
        {
            S(e.Property(x => x.SourceSystem), 50); S(e.Property(x => x.ExternalId), 100); S(e.Property(x => x.CustomerType), 30); S(e.Property(x => x.Status), 30);
            S(e.Property(x => x.Region), 50); S(e.Property(x => x.NationalityCountry), 2); S(e.Property(x => x.ResidenceCountry), 2); S(e.Property(x => x.IncorporationCountry), 2);
            S(e.Property(x => x.KycStatus), 30); S(e.Property(x => x.ScreeningStatus), 30); S(e.Property(x => x.AssignedOfficer), 100); S(e.Property(x => x.NameSearchKey), 1000);
            e.HasIndex(x => new { x.TenantId, x.SourceSystem, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status }); e.HasIndex(x => x.Region); e.HasIndex(x => x.OnboardingAgentId); e.HasIndex(x => x.ScreeningStatus);
            S(e.Property(x => x.HouseholdId), 100); e.HasIndex(x => new { x.TenantId, x.HouseholdId });
        });
        Child<CustomerIdentification>(m, x => x.CustomerId, e => { S(e.Property(x => x.IdNumberToken), 128); e.HasIndex(x => x.IdNumberToken); });
        Child<CustomerContact>(m, x => x.CustomerId, e => { S(e.Property(x => x.SearchToken), 128); e.HasIndex(x => x.SearchToken); });
        Child<CustomerFinancialProfile>(m, x => x.CustomerId, e => e.HasIndex(x => new { x.CustomerId, x.EffectiveFrom }));
        Child<CustomerDocument>(m, x => x.CustomerId);
        Child<OwnershipLink>(m, x => x.EntityCustomerId, e => { e.Property(x => x.Percentage).HasPrecision(9, 4); e.HasOne<Customer>().WithMany().HasForeignKey(x => x.OwnerCustomerId).OnDelete(DeleteBehavior.Restrict); });
        Child<CustomerDeviceSignal>(m, x => x.CustomerId, e => { S(e.Property(x => x.DeviceId), 200); e.HasIndex(x => x.DeviceId); });
        m.Entity<EntityLink>(e => { e.Property(x => x.Confidence).HasPrecision(9, 4); e.HasIndex(x => x.CustomerAId); e.HasIndex(x => x.CustomerBId); });
        Child<CustomerAcceptanceDecision>(m, x => x.CustomerId);
        Child<EddTask>(m, x => x.CustomerId);
        Child<CustomerExitRequest>(m, x => x.CustomerId);
        m.Entity<DoNotOnboardEntry>(e => { S(e.Property(x => x.IdNumberToken), 128); e.HasIndex(x => x.IdNumberToken); });

        // ---------- dynamic fields ----------
        m.Entity<CustomerFieldGroup>(e => { S(e.Property(x => x.Code), 50); e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); });
        m.Entity<CustomerFieldDefinition>(e => { S(e.Property(x => x.Code), 50); S(e.Property(x => x.Status), 30); e.HasIndex(x => new { x.TenantId, x.Code, x.Version }).IsUnique(); e.HasIndex(x => new { x.Code, x.Status }); });
        m.Entity<CustomerFieldOption>(e => { S(e.Property(x => x.FieldCode), 50); S(e.Property(x => x.OptionCode), 100); e.HasIndex(x => new { x.TenantId, x.FieldCode, x.OptionCode }).IsUnique(); });
        Child<CustomerFieldValue>(m, x => x.CustomerId, e =>
        {
            S(e.Property(x => x.FieldCode), 50); e.Property(x => x.ValueText).HasMaxLength(4000); S(e.Property(x => x.ValueToken), 128);
            e.Property(x => x.ValueNumber).HasPrecision(28, 8);
            e.HasOne<CustomerFieldDefinition>().WithMany().HasForeignKey(x => x.FieldDefinitionId).OnDelete(DeleteBehavior.Restrict);
            // Deliberate indexing strategy for typed EAV search (no per-field columns).
            e.HasIndex(x => new { x.FieldCode, x.ValueNumber }).IncludeProperties(x => new { x.CustomerId, x.EffectiveTo }).HasDatabaseName("IX_FieldValues_Code_Number");
            e.HasIndex(x => new { x.FieldCode, x.ValueDate }).IncludeProperties(x => new { x.CustomerId, x.EffectiveTo }).HasDatabaseName("IX_FieldValues_Code_Date");
            e.HasIndex(x => new { x.FieldCode, x.ValueToken }).IncludeProperties(x => new { x.CustomerId, x.EffectiveTo }).HasDatabaseName("IX_FieldValues_Code_Token");
            e.HasIndex(x => new { x.CustomerId, x.FieldCode, x.EffectiveFrom }).HasDatabaseName("IX_FieldValues_Customer_Code_From");
        });
        m.Entity<CustomerFormLayout>(e => { S(e.Property(x => x.CustomerType), 30); S(e.Property(x => x.RoleCode), 64); e.HasIndex(x => new { x.TenantId, x.CustomerType, x.RoleCode, x.Version }).IsUnique(); });
        Child<CustomerFieldChangeHistory>(m, x => x.CustomerId);

        // ---------- wallets & transactions ----------
        m.Entity<Wallet>(e =>
        {
            S(e.Property(x => x.SourceSystem), 50); S(e.Property(x => x.ExternalWalletId), 100);
            e.HasIndex(x => new { x.TenantId, x.SourceSystem, x.ExternalWalletId }).IsUnique();
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Wallet>().WithMany().HasForeignKey(x => x.ParentWalletId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<WalletMapping>(e => { S(e.Property(x => x.SourceSystem), 50); S(e.Property(x => x.ExternalWalletId), 100); e.HasIndex(x => new { x.TenantId, x.SourceSystem, x.ExternalWalletId }); });
        m.Entity<Transaction>(e =>
        {
            S(e.Property(x => x.SourceSystem), 50); S(e.Property(x => x.SourceTransactionId), 100); S(e.Property(x => x.OriginalReference), 100);
            S(e.Property(x => x.OriginalSourceTransactionId), 100); S(e.Property(x => x.Currency), 3); S(e.Property(x => x.BaseCurrency), 3); S(e.Property(x => x.Status), 30);
            S(e.Property(x => x.TransactionType), 50); S(e.Property(x => x.Channel), 50); S(e.Property(x => x.DeviceId), 200); S(e.Property(x => x.SenderWalletExternalId), 100);
            S(e.Property(x => x.ReceiverWalletExternalId), 100); S(e.Property(x => x.SourceCountry), 2); S(e.Property(x => x.DestinationCountry), 2);
            S(e.Property(x => x.SubType), 50); S(e.Property(x => x.Mcc), 4);
            S(e.Property(x => x.InvoiceNumber), 100); S(e.Property(x => x.InvoiceCurrency), 3); S(e.Property(x => x.GoodsCategory), 100);
            e.Property(x => x.FxRate).HasPrecision(19, 8);
            e.Ignore(x => x.NetAmount);
            e.HasIndex(x => new { x.TenantId, x.SourceSystem, x.SourceTransactionId }).IsUnique();
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.SenderCustomerId, x.TimestampUtc });
            e.HasIndex(x => new { x.ReceiverCustomerId, x.TimestampUtc });
            e.HasIndex(x => new { x.AgentCustomerId, x.TimestampUtc });
            e.HasIndex(x => x.OriginalReference); e.HasIndex(x => x.OriginalSourceTransactionId); e.HasIndex(x => new { x.SourceSystem, x.BusinessDate }); e.HasIndex(x => x.DeviceId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.SenderCustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.ReceiverCustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.OriginalTransactionId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TransactionStatusHistory>(e => { e.HasIndex(x => x.TransactionId); e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<TransactionPartyResolution>(e => { e.HasIndex(x => x.TransactionId); e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<RawTransactionPayload>(e => e.HasIndex(x => x.IngestionRecordId));
        m.Entity<WalletBalanceSnapshot>(e =>
        {
            S(e.Property(x => x.SourceSystem), 50); S(e.Property(x => x.ExternalWalletId), 100); S(e.Property(x => x.Currency), 3);
            e.HasIndex(x => new { x.TenantId, x.SourceSystem, x.ExternalWalletId, x.BusinessDate }).IsUnique();
            e.HasIndex(x => new { x.CustomerId, x.BusinessDate });
        });
        m.Entity<CustomerDailyAggregate>(e => { S(e.Property(x => x.Direction), 3); e.HasIndex(x => new { x.CustomerId, x.BusinessDate, x.Direction }).IsUnique(); });

        // ---------- risk ----------
        m.Entity<RiskMethodology>(e =>
        {
            S(e.Property(x => x.Code), 50); e.HasIndex(x => new { x.TenantId, x.Code, x.Version }).IsUnique();
            e.HasMany(x => x.Factors).WithOne().HasForeignKey(x => x.MethodologyId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Thresholds).WithOne().HasForeignKey(x => x.MethodologyId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Overrides).WithOne().HasForeignKey(x => x.MethodologyId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Floors).WithOne().HasForeignKey(x => x.MethodologyId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Cap).HasPrecision(9, 4);
        });
        m.Entity<RiskFactorCategory>(e => { S(e.Property(x => x.Code), 50); e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); });
        m.Entity<RiskFactorDefinition>(e =>
        {
            S(e.Property(x => x.Code), 50); S(e.Property(x => x.DataSource), 120); e.HasIndex(x => new { x.MethodologyId, x.Code }).IsUnique(); e.HasIndex(x => x.DataSource);
            e.Property(x => x.Weight).HasPrecision(9, 4); e.Property(x => x.MissingDataScore).HasPrecision(9, 4);
            e.HasMany(x => x.Mappings).WithOne().HasForeignKey(x => x.FactorId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<RiskFactorScoreMapping>(e => { e.Property(x => x.Score).HasPrecision(9, 4); e.Property(x => x.RangeMin).HasPrecision(28, 8); e.Property(x => x.RangeMax).HasPrecision(28, 8); });
        m.Entity<RiskThreshold>(e => { e.Property(x => x.MinScore).HasPrecision(9, 4); e.Property(x => x.MaxScore).HasPrecision(9, 4); });
        m.Entity<CustomerRiskAssessment>(e =>
        {
            e.ToTable("CustomerRiskAssessments", t => t.HasTrigger("TR_RiskAssessments_Immutable"));
            S(e.Property(x => x.TriggerEventId), 200);
            e.Property(x => x.CalculatedScore).HasPrecision(9, 4); e.Property(x => x.PreviousScore).HasPrecision(9, 4);
            e.HasIndex(x => new { x.CustomerId, x.TriggerEventId }).IsUnique().HasFilter("[TriggerEventId] IS NOT NULL");
            e.HasIndex(x => new { x.CustomerId, x.CalculatedAt });
            e.HasMany(x => x.Factors).WithOne().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RiskMethodology>().WithMany().HasForeignKey(x => x.MethodologyId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<CustomerRiskAssessmentFactor>(e =>
        {
            e.ToTable("CustomerRiskAssessmentFactors", t => t.HasTrigger("TR_RiskAssessmentFactors_Immutable"));
            e.Property(x => x.NormalizedScore).HasPrecision(9, 4); e.Property(x => x.Weight).HasPrecision(9, 4); e.Property(x => x.Contribution).HasPrecision(19, 6);
        });
        m.Entity<CurrentCustomerRiskProfile>(e =>
        {
            e.HasIndex(x => x.CustomerId).IsUnique(); e.Property(x => x.Score).HasPrecision(9, 4); e.Property(x => x.CalculatedScore).HasPrecision(9, 4);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        Child<RiskOverrideRequest>(m, x => x.CustomerId, e => e.Property(x => x.CalculatedScore).HasPrecision(9, 4));
        Child<RiskReviewTask>(m, x => x.CustomerId, e => e.HasIndex(x => new { x.Status, x.DueDate }));

        // ---------- rules & alerts ----------
        m.Entity<AmlRule>(e => { S(e.Property(x => x.Code), 50); e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); e.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<AmlRuleVersion>(e => { e.HasIndex(x => new { x.RuleId, x.Version }).IsUnique(); S(e.Property(x => x.Status), 30); e.HasIndex(x => x.Status); });
        m.Entity<AmlRuleExecution>(e => { e.HasIndex(x => new { x.RuleVersionId, x.Mode, x.WindowEnd }); e.HasIndex(x => x.StartedAt); });
        m.Entity<AmlAlert>(e =>
        {
            S(e.Property(x => x.DeduplicationKey), 400); S(e.Property(x => x.AlertNumber), 100); S(e.Property(x => x.Status), 40); S(e.Property(x => x.RuleCode), 50); S(e.Property(x => x.AssignedTo), 100);
            e.Property(x => x.Score).HasPrecision(9, 4);
            e.HasIndex(x => new { x.TenantId, x.DeduplicationKey }).IsUnique();
            e.HasIndex(x => x.AlertNumber); e.HasIndex(x => new { x.Status, x.DueAt }); e.HasIndex(x => x.PrimaryCustomerId); e.HasIndex(x => x.RuleId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.PrimaryCustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<AmlAlertTransaction>(e => { e.HasIndex(x => new { x.AlertId, x.TransactionId }).IsUnique(); e.HasIndex(x => x.TransactionId); e.HasOne<AmlAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<AmlAlertCustomer>(e => { e.HasIndex(x => new { x.AlertId, x.CustomerId }).IsUnique(); e.HasIndex(x => x.CustomerId); e.HasOne<AmlAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<AmlAlertRuleMatch>(e =>
        {
            e.ToTable("AmlAlertRuleMatches", t => t.HasTrigger("TR_AlertRuleMatches_Immutable"));
            S(e.Property(x => x.MatchKey), 200); e.HasIndex(x => new { x.AlertId, x.MatchKey }).IsUnique(); e.HasOne<AmlAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<AlertStatusHistory>(e => e.HasIndex(x => x.AlertId));

        // ---------- cases ----------
        m.Entity<AmlCase>(e =>
        {
            S(e.Property(x => x.CaseNumber), 100); S(e.Property(x => x.Status), 40); S(e.Property(x => x.Confidentiality), 30);
            e.HasIndex(x => x.CaseNumber).IsUnique(); e.HasIndex(x => x.PrimaryCustomerId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.PrimaryCustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<AmlCaseAlert>(e => { e.HasIndex(x => new { x.CaseId, x.AlertId }).IsUnique(); e.HasOne<AmlCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict); e.HasOne<AmlAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<AmlCaseCustomer>(e => { e.HasIndex(x => new { x.CaseId, x.CustomerId }).IsUnique(); e.HasIndex(x => x.CustomerId); e.HasOne<AmlCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<AmlCaseTransaction>(e => { e.HasIndex(x => new { x.CaseId, x.TransactionId }).IsUnique(); e.HasIndex(x => x.TransactionId); e.HasOne<AmlCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<AmlCaseTeamMember>(e => { S(e.Property(x => x.Username), 100); e.HasIndex(x => new { x.CaseId, x.Username }).IsUnique(); });
        m.Entity<AmlCaseNote>(e => { e.ToTable("AmlCaseNotes", t => t.HasTrigger("TR_CaseNotes_Immutable")); e.HasIndex(x => x.CaseId); e.HasOne<AmlCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<AmlCaseDocument>(e => { e.HasIndex(x => x.CaseId); e.HasOne<AmlCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<AmlCaseHistory>(e => e.HasIndex(x => x.CaseId));
        m.Entity<AmlCaseTask>(e => e.HasIndex(x => x.CaseId));
        m.Entity<CaseStatusTransition>(e => e.HasIndex(x => new { x.TenantId, x.FromStatus, x.ToStatus }).IsUnique());

        // ---------- screening ----------
        m.Entity<WatchlistSource>(e => { S(e.Property(x => x.Code), 50); e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); });
        m.Entity<WatchlistVersion>(e => { S(e.Property(x => x.VersionLabel), 100); e.HasIndex(x => new { x.SourceId, x.VersionLabel }).IsUnique(); });
        m.Entity<WatchlistEntry>(e => { S(e.Property(x => x.EntryId), 100); e.HasIndex(x => new { x.SourceId, x.EntryId }); e.HasOne<WatchlistSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict); });
        m.Entity<ScreeningRequest>(e => { S(e.Property(x => x.IdempotencyKey), 300); S(e.Property(x => x.SubjectId), 300); e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique(); e.HasIndex(x => new { x.Status, x.NextAttemptAt }); e.HasIndex(x => x.CustomerId); });
        m.Entity<ScreeningResult>(e =>
        {
            S(e.Property(x => x.SubjectId), 300); S(e.Property(x => x.ListEntryId), 100); S(e.Property(x => x.SourceCode), 50);
            e.Property(x => x.MatchScore).HasPrecision(9, 4); e.HasIndex(x => x.CustomerId); e.HasIndex(x => new { x.SubjectType, x.SubjectId, x.SourceCode, x.ListEntryId });
            e.HasOne<ScreeningRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<ScreeningReviewHistory>(e => e.HasIndex(x => x.ResultId));
        m.Entity<AllowListEntry>(e => { S(e.Property(x => x.SubjectId), 300); S(e.Property(x => x.ListEntryId), 100); e.HasIndex(x => new { x.SubjectType, x.SubjectId, x.SourceCode, x.ListEntryId }); });

        // ---------- actions / realtime ----------
        m.Entity<ComplianceAction>(e => { S(e.Property(x => x.IdempotencyKey), 300); e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique(); e.HasIndex(x => new { x.Status, x.NextAttemptAt }); e.Property(x => x.FrozenAmount).HasPrecision(19, 4); });
        m.Entity<MockCoreCommand>(e => { S(e.Property(x => x.IdempotencyKey), 300); e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique(); });
        m.Entity<MockCoreTargetState>(e => { S(e.Property(x => x.Target), 300); e.HasIndex(x => new { x.TenantId, x.Target }).IsUnique(); });
        m.Entity<RealtimeDecision>(e => { S(e.Property(x => x.SourceSystem), 50); S(e.Property(x => x.SourceTransactionId), 100); e.HasIndex(x => new { x.TenantId, x.SourceSystem, x.SourceTransactionId }).IsUnique(); e.HasIndex(x => x.DecisionId).IsUnique(); });
        m.Entity<HoldQueueItem>(e => e.HasIndex(x => new { x.Status, x.SlaDueAt }));
        m.Entity<PaymentTransparencyRule>(e => { e.Property(x => x.ThresholdAmount).HasPrecision(19, 4); });

        // ---------- reporting / governance / operations ----------
        m.Entity<RegulatoryReport>(e =>
        {
            e.ToTable("RegulatoryReports", t => t.HasTrigger("TR_RegulatoryReports_SubmittedImmutable"));
            S(e.Property(x => x.ReportNumber), 100); e.HasIndex(x => x.ReportNumber).IsUnique(); e.HasIndex(x => x.CaseId);
            e.HasOne<AmlCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<CurrencyTransactionReport>(e =>
        {
            S(e.Property(x => x.ReportNumber), 100); S(e.Property(x => x.Status), 30); S(e.Property(x => x.BaseCurrency), 3);
            e.HasIndex(x => x.ReportNumber).IsUnique(); e.HasIndex(x => new { x.TenantId, x.CustomerId, x.BusinessDate }).IsUnique(); e.HasIndex(x => x.Status);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<IndustryBenchmark>(e =>
        {
            S(e.Property(x => x.Metric), 50); S(e.Property(x => x.SegmentField), 100); S(e.Property(x => x.SegmentValue), 200);
            e.HasIndex(x => new { x.TenantId, x.Metric, x.SegmentField, x.SegmentValue, x.EffectiveFrom });
        });
        m.Entity<AuthorityRequest>(e => e.HasIndex(x => x.CustomerId));
        m.Entity<RetentionRun>(e => { });
        m.Entity<IngestionRun>(e => e.HasIndex(x => new { x.SourceSystem, x.ReceivedAt }));
        m.Entity<IngestionRecord>(e => { S(e.Property(x => x.Status), 30); e.HasIndex(x => x.RunId); e.HasIndex(x => x.Status); e.HasIndex(x => x.TransactionId); });
        m.Entity<DataQualityIssue>(e => { S(e.Property(x => x.IssueType), 50); S(e.Property(x => x.EntityId), 200); S(e.Property(x => x.Status), 30); e.HasIndex(x => new { x.IssueType, x.Status }); e.HasIndex(x => x.EntityId); });
        m.Entity<SourceControlTotal>(e => e.HasIndex(x => new { x.TenantId, x.SourceSystem, x.BusinessDate, x.Currency }).IsUnique());
        m.Entity<OutboxMessage>(e => { e.HasIndex(x => x.MessageId).IsUnique(); e.HasIndex(x => x.ProcessedAt); });
        m.Entity<PublishedEvent>(e => e.HasIndex(x => x.MessageId).IsUnique());
        m.Entity<RetryQueueItem>(e => { S(e.Property(x => x.Kind), 30); S(e.Property(x => x.ReferenceId), 100); S(e.Property(x => x.Status), 30); e.HasIndex(x => new { x.Kind, x.Status, x.NextAttemptAt }); });
        m.Entity<ExportJob>(e => e.HasIndex(x => x.JobId).IsUnique());
        m.Entity<JobHeartbeat>(e => { S(e.Property(x => x.JobName), 100); e.HasIndex(x => x.JobName).IsUnique(); });
        m.Entity<QaSample>(e => e.HasIndex(x => x.AlertId).IsUnique());
        m.Entity<QaSamplingConfig>(e => e.Property(x => x.Percentage).HasPrecision(9, 4));
    }

    private void ApplyTenantFilter<T>(ModelBuilder m) where T : class, ITenantScoped =>
        m.Entity<T>().HasQueryFilter(e => BypassTenantFilter || e.TenantId == CurrentTenantId);

    private static void S<TP>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<TP> p, int len) => p.HasMaxLength(len);

    private static void Child<T>(ModelBuilder m, System.Linq.Expressions.Expression<Func<T, long>> fk, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T>>? more = null) where T : class
    {
        m.Entity<T>(e =>
        {
            var name = ((System.Linq.Expressions.MemberExpression)fk.Body).Member.Name;
            e.HasOne<Customer>().WithMany().HasForeignKey(name).OnDelete(DeleteBehavior.Restrict);
            more?.Invoke(e);
        });
    }

    private static string Plural(string n) => n.EndsWith("y") && !n.EndsWith("ay") ? n[..^1] + "ies" : n.EndsWith("s") || n.EndsWith("x") || n.EndsWith("ch") ? n + "es" : n + "s";
}
