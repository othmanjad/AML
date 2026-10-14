using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AML.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AcceptancePolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SeniorApprovalRiskLevels = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConditionalRiskLevels = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequireCompleteKyc = table.Column<bool>(type: "bit", nullable: false),
                    PepRequiresSeniorApproval = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsSample = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcceptancePolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActionReconciliations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActionId = table.Column<long>(type: "bigint", nullable: false),
                    LocalState = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CoreState = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsMismatch = table.Column<bool>(type: "bit", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionReconciliations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertClosurePolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConditionType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConditionValues = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiresSecondApproval = table.Column<bool>(type: "bit", nullable: false),
                    RequiredChecklistItems = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MinNarrativeLength = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertClosurePolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertSlaPolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WorkingDays = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertSlaPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertStatusHistories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ToStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResolutionCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertStatusHistories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AllowListEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubjectType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SubjectHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ListEntryId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntryHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ListVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClearedResultId = table.Column<long>(type: "bigint", nullable: true),
                    ClearedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClearedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    InvalidatedReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    InvalidatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllowListEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AmlCaseHistories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ToStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlCaseHistories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AmlCaseTasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Assignee = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlCaseTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AmlCaseTeamMembers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlCaseTeamMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AmlRuleExecutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    RuleVersionId = table.Column<long>(type: "bigint", nullable: false),
                    RuleVersion = table.Column<int>(type: "int", nullable: false),
                    RuleCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WindowEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordsEvaluated = table.Column<int>(type: "int", nullable: false),
                    Matches = table.Column<int>(type: "int", nullable: false),
                    AlertsCreated = table.Column<int>(type: "int", nullable: false),
                    AlertsDeduplicated = table.Column<int>(type: "int", nullable: false),
                    ErrorCount = table.Column<int>(type: "int", nullable: false),
                    Errors = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlRuleExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AmlRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuleType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsSample = table.Column<bool>(type: "bit", nullable: false),
                    IsRealtimeCapable = table.Column<bool>(type: "bit", nullable: false),
                    TypologyCodes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActiveVersionId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OldValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceContext = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsRestricted = table.Column<bool>(type: "bit", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthorityNotifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActionId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    NotificationType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Authority = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorityNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthorityRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Reference = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Authority = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    SubjectsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Scope = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedOfficer = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Confidentiality = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Response = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseEvidence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsOverdue = table.Column<bool>(type: "bit", nullable: false),
                    TriggeredAssessmentId = table.Column<long>(type: "bigint", nullable: true),
                    CaseId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorityRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CaseStatusTransitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FromStatus = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ToStatus = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseStatusTransitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComplianceActionPolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TriggerType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Categories = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActionType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsAutomatic = table.Column<bool>(type: "bit", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceActionPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComplianceActions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActionType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    WalletId = table.Column<long>(type: "bigint", nullable: true),
                    TransactionId = table.Column<long>(type: "bigint", nullable: true),
                    TargetReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NeutralCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InternalReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsAutomatic = table.Column<bool>(type: "bit", nullable: false),
                    TriggerType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScreeningResultId = table.Column<long>(type: "bigint", nullable: true),
                    CaseId = table.Column<long>(type: "bigint", nullable: true),
                    RelatedActionId = table.Column<long>(type: "bigint", nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CoreReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FrozenAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    FrozenCurrency = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FrozenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReconciliationStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDailyAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    TxnCount = table.Column<int>(type: "int", nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDailyAggregates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerFieldDefinitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LabelEn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LabelAr = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GroupCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomerTypes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiredForTypes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsSearchable = table.Column<bool>(type: "bit", nullable: false),
                    IsFilterable = table.Column<bool>(type: "bit", nullable: false),
                    IsSensitive = table.Column<bool>(type: "bit", nullable: false),
                    IsEncrypted = table.Column<bool>(type: "bit", nullable: false),
                    ValidationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OptionsSource = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DefaultValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsSample = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerFieldDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerFieldGroups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LabelEn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LabelAr = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerFieldGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerFieldOptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FieldCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OptionCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LabelEn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LabelAr = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerFieldOptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerFormLayouts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RoleCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LayoutJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerFormLayouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceSystem = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CustomerType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NameLatin = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NameArabic = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NameSearchKey = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    NationalityCountry = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    ResidenceCountry = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    IncorporationCountry = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    RegistrationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    KycStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ScreeningStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AssignedOfficer = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Region = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    OnboardingAgentId = table.Column<long>(type: "bigint", nullable: true),
                    DueDiligenceLevel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsPep = table.Column<bool>(type: "bit", nullable: false),
                    IsFrozen = table.Column<bool>(type: "bit", nullable: false),
                    ExitedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsAnonymised = table.Column<bool>(type: "bit", nullable: false),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataQualityIssues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IssueType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataQualityIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DoNotOnboardEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdNumberToken = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    NameSearchKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    OriginalCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    LinkedCaseId = table.Column<long>(type: "bigint", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoNotOnboardEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DueDiligenceLevelDefinitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiredFieldCodes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiredDocuments = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VerificationMethods = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovalPermission = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequiresSourceOfFunds = table.Column<bool>(type: "bit", nullable: false),
                    RequiresSourceOfWealth = table.Column<bool>(type: "bit", nullable: false),
                    ReviewFrequencyMonths = table.Column<int>(type: "int", nullable: false),
                    IsSample = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DueDiligenceLevelDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DueDiligenceSelectionRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ConditionType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConditionValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LevelCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DueDiligenceSelectionRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EntityLinks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerAId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerBId = table.Column<long>(type: "bigint", nullable: false),
                    LinkType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    RuleVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExportJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Format = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FiltersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IncludeSensitive = table.Column<bool>(type: "bit", nullable: false),
                    IncludeRestricted = table.Column<bool>(type: "bit", nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequesterPermissions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Sha256 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DownloadCount = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FxRates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FromCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ToCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(19,8)", precision: 19, scale: 8, nullable: false),
                    RateType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    SupersedesId = table.Column<long>(type: "bigint", nullable: true),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HoldQueueItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TransactionId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceTransactionId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SlaDueAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsEscalated = table.Column<bool>(type: "bit", nullable: false),
                    EscalatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CoreActionId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldQueueItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Holidays",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holidays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestionRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceTransactionId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorHistory = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TransactionId = table.Column<long>(type: "bigint", nullable: true),
                    ReplayedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReplayedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestionRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceSystem = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    BatchId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    ProcessedCount = table.Column<int>(type: "int", nullable: false),
                    DuplicateCount = table.Column<int>(type: "int", nullable: false),
                    ConflictCount = table.Column<int>(type: "int", nullable: false),
                    ErrorCount = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstitutionalRiskAssessments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Period = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Dimension = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Item = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InherentRisk = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ControlEffectiveness = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResidualRisk = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstitutionalRiskAssessments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobHeartbeats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    LastDurationMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobHeartbeats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JurisdictionSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Jurisdiction = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LegalSource = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ArticleReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ComplianceOwner = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JurisdictionSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegalHolds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntityType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalHolds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LookupValues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Category = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LabelEn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LabelAr = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Attribute = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupValues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MockCoreCommands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Command = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CoreReference = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TimesReceived = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MockCoreCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MockCoreTargetStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    State = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MockCoreTargetStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelGovernanceRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ModelCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputsSchema = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OutputsSchema = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Explainability = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DriftMonitoring = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelGovernanceRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationalAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentTransparencyRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Corridor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TransactionTypes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ThresholdAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ThresholdCurrency = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiredFields = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AlwaysRequiredFields = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsSample = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTransparencyRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Module = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublishedEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QaSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    ConfigCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Stratum = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Analyst = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SampledAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reviewer = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorCategory = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Findings = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Reopened = table.Column<bool>(type: "bit", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QaSamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QaSamplingConfigs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuleCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Analyst = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RiskLevel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Percentage = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    Seed = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QaSamplingConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RawTransactionPayloads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IngestionRecordId = table.Column<long>(type: "bigint", nullable: false),
                    TransactionId = table.Column<long>(type: "bigint", nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StoredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawTransactionPayloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RealtimeDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceTransactionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReasonCodes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    IsFallback = table.Column<bool>(type: "bit", nullable: false),
                    FallbackMode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EvaluatedRules = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScreeningSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TransactionType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    TransactionId = table.Column<long>(type: "bigint", nullable: true),
                    NeedsPostReview = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RealtimeFallbackPolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    TransactionType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MinAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    MaxAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClosedDecision = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeFallbackPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceSystem = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TransactionType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceCount = table.Column<int>(type: "int", nullable: false),
                    SourceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ImportedCount = table.Column<int>(type: "int", nullable: false),
                    ImportedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CountDifference = table.Column<int>(type: "int", nullable: false),
                    AmountDifference = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RunAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetentionPolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordClass = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RetentionMonths = table.Column<int>(type: "int", nullable: false),
                    ClockEvent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Disposal = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LegalSource = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetentionRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AsOf = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CandidatesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SkippedJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CandidateCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProcessedCount = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetryQueueItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetryQueueItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskFactorCategories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsCustom = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskFactorCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskMethodologies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AggregationMethod = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Cap = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    ScorePrecision = table.Column<int>(type: "int", nullable: false),
                    AppliesToCustomerTypes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovalRequiredLevels = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CategoryMatrixJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Owner = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Rationale = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PreviousVersionId = table.Column<long>(type: "bigint", nullable: true),
                    IsSample = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskMethodologies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScreeningProviderStatuses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                    SimulationMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SimulatedFailuresRemaining = table.Column<int>(type: "int", nullable: false),
                    LastSuccessAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailureAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreeningProviderStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScreeningRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    SubjectName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SubjectHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProviderCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureHistory = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ListVersionId = table.Column<long>(type: "bigint", nullable: true),
                    ResultCount = table.Column<int>(type: "int", nullable: false),
                    SuppressedCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreeningRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScreeningReviewHistories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ResultId = table.Column<long>(type: "bigint", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ToStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreeningReviewHistories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScreeningSuppressionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AllowListEntryId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ListEntryId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreeningSuppressionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceControlTotals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceSystem = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReportedCount = table.Column<int>(type: "int", nullable: false),
                    ReportedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlTotals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceSystems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClientSecretHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpectedFeed = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TransactionTypesInScope = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceSystems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BaseCurrency = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WeekendDays = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReportingEntityId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TransactionStatusMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceSystem = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    NormalizedStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionStatusMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TuningRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    RuleVersionId = table.Column<long>(type: "bigint", nullable: false),
                    RunType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CurrentMatches = table.Column<int>(type: "int", nullable: false),
                    CandidateMatches = table.Column<int>(type: "int", nullable: false),
                    SampleJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TuningRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Typologies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RiskRating = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Typologies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TypologyControls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TypologyCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ControlType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ControlReference = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypologyControls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WalletMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceSystem = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExternalWalletId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MappingCategory = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RuleVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistSources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ListType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsMandatory = table.Column<bool>(type: "bit", nullable: false),
                    CurrentVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VersionLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Additions = table.Column<int>(type: "int", nullable: false),
                    Amendments = table.Column<int>(type: "int", nullable: false),
                    Removals = table.Column<int>(type: "int", nullable: false),
                    RescreenStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RescreenCompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TimeToScreenMs = table.Column<long>(type: "bigint", nullable: true),
                    SubjectsScreened = table.Column<int>(type: "int", nullable: false),
                    MatchesRaised = table.Column<int>(type: "int", nullable: false),
                    ChangeSetJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AmlRuleVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Rationale = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TestEvidence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpectedImpact = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidationResult = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SimulatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SimulationSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublishedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlRuleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlRuleVersions_AmlRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AmlRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlertNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AlertType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuleId = table.Column<long>(type: "bigint", nullable: true),
                    RuleVersionId = table.Column<long>(type: "bigint", nullable: true),
                    RuleVersion = table.Column<int>(type: "int", nullable: true),
                    RuleCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ExecutionId = table.Column<long>(type: "bigint", nullable: true),
                    PrimaryCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    SubjectKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Score = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AssignedTo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssignedTeam = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolutionCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChecklistJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingClosureStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClosureRequestedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClosureApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EscalationReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Confidentiality = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CaseId = table.Column<long>(type: "bigint", nullable: true),
                    IsBacktest = table.Column<bool>(type: "bit", nullable: false),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    ReopenCount = table.Column<int>(type: "int", nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlAlerts_Customers_PrimaryCustomerId",
                        column: x => x.PrimaryCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlCases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CaseType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Confidentiality = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PrimaryCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    AssignedInvestigator = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Reviewer = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Team = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Findings = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecommendedAction = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FinalDecision = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClosureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SuspicionFormedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovalStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovalRequestedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlCases_Customers_PrimaryCustomerId",
                        column: x => x.PrimaryCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CurrentCustomerRiskProfiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    Level = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CalculatedScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    CalculatedLevel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MonitoringLevel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastAssessmentId = table.Column<long>(type: "bigint", nullable: true),
                    LastAssessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextReviewDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OverrideApplied = table.Column<bool>(type: "bit", nullable: false),
                    OverrideType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OverrideReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SanctionsWorkflow = table.Column<bool>(type: "bit", nullable: false),
                    MethodologyCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MethodologyVersion = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrentCustomerRiskProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurrentCustomerRiskProfiles_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerAcceptanceDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PolicyCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PolicyVersion = table.Column<int>(type: "int", nullable: false),
                    DecidedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReasonCodes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Conditions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConditionsDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsAutomatic = table.Column<bool>(type: "bit", nullable: false),
                    SanctionsWorkflow = table.Column<bool>(type: "bit", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAcceptanceDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAcceptanceDecisions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerContacts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    ContactType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ValueEncrypted = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SearchToken = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Masked = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerContacts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDeviceSignals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GeoCountry = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDeviceSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerDeviceSignals_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Sha256 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerDocuments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerExitRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InternalReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LinkedCaseId = table.Column<long>(type: "bigint", nullable: true),
                    Evidence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AddToDoNotOnboard = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerExitRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerExitRequests_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerFieldChangeHistories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    FieldCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FieldVersion = table.Column<int>(type: "int", nullable: false),
                    PreviousValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerFieldChangeHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerFieldChangeHistories_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerFieldValues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    FieldCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FieldDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    FieldVersion = table.Column<int>(type: "int", nullable: false),
                    ValueText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ValueNumber = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    ValueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ValueBool = table.Column<bool>(type: "bit", nullable: true),
                    ValueJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValueEncrypted = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValueToken = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    MaskedDisplay = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VerificationStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerFieldValues_CustomerFieldDefinitions_FieldDefinitionId",
                        column: x => x.FieldDefinitionId,
                        principalTable: "CustomerFieldDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerFieldValues_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerFinancialProfiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    DeclaredMonthlyIncome = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceOfFunds = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceOfWealth = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Occupation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpectedMonthlyVolume = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ExpectedMonthlyCount = table.Column<int>(type: "int", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeclaredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerFinancialProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerFinancialProfiles_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerIdentifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    IdType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IdNumberEncrypted = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IdNumberToken = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Last4 = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IssuingCountry = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    VerificationStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerIdentifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerIdentifications_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EddTasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    LevelCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TaskType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SignedOffBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SignedOffAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EddTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EddTasks_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnershipLinks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntityCustomerId = table.Column<long>(type: "bigint", nullable: false),
                    OwnerCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    OwnerName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OwnerNameArabic = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OwnerType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Percentage = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    ControlType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OwnerDateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    OwnerNationality = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OwnerIsPep = table.Column<bool>(type: "bit", nullable: false),
                    ScreeningStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Evidence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnershipLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnershipLinks_Customers_EntityCustomerId",
                        column: x => x.EntityCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnershipLinks_Customers_OwnerCustomerId",
                        column: x => x.OwnerCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RiskOverrideRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    AssessmentId = table.Column<long>(type: "bigint", nullable: false),
                    CalculatedScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    CalculatedLevel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestedLevel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DecidedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FinalLevel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskOverrideRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskOverrideRequests_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RiskReviewTasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EscalatedTo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EscalatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletionEvidence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CompletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceAssessmentId = table.Column<long>(type: "bigint", nullable: true),
                    ResultAssessmentId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskReviewTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskReviewTasks_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceSystem = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceTransactionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OriginalReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SenderWalletExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReceiverWalletExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SenderWalletId = table.Column<long>(type: "bigint", nullable: true),
                    ReceiverWalletId = table.Column<long>(type: "bigint", nullable: true),
                    SenderCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    ReceiverCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    TransactionType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    BaseAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    BaseCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    FxRate = table.Column<decimal>(type: "decimal(19,8)", precision: 19, scale: 8, nullable: true),
                    FxRateDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FxRateId = table.Column<long>(type: "bigint", nullable: true),
                    FxRateVersion = table.Column<int>(type: "int", nullable: true),
                    FxStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReversedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSourceEventAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OriginalSourceTransactionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OriginalTransactionId = table.Column<long>(type: "bigint", nullable: true),
                    IsReversal = table.Column<bool>(type: "bit", nullable: false),
                    IsRefund = table.Column<bool>(type: "bit", nullable: false),
                    SourceCountry = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    DestinationCountry = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    AgentCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    MerchantCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    OriginatorName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginatorAccount = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginatorAddress = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginatorNationalId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginatorDateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    OriginatorPlaceOfBirth = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BeneficiaryName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BeneficiaryAccount = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IntermediaryInstitution = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OrderingInstitution = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CopResult = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PaymentTransparencyStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PaymentTransparencyIssues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeviceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IngestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: true),
                    DataQualityStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RealtimeDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transactions_Customers_ReceiverCustomerId",
                        column: x => x.ReceiverCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Transactions_Customers_SenderCustomerId",
                        column: x => x.SenderCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Transactions_Transactions_OriginalTransactionId",
                        column: x => x.OriginalTransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Wallets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceSystem = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExternalWalletId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    WalletType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParentWalletId = table.Column<long>(type: "bigint", nullable: true),
                    IsFrozen = table.Column<bool>(type: "bit", nullable: false),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Wallets_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Wallets_Wallets_ParentWalletId",
                        column: x => x.ParentWalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRiskAssessments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    MethodologyId = table.Column<long>(type: "bigint", nullable: false),
                    MethodologyCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MethodologyVersion = table.Column<int>(type: "int", nullable: false),
                    AggregationMethod = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TriggerEventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TriggerReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CalculatedScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    CalculatedLevel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PreviousScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    PreviousLevel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MandatoryOverrideLevel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FinalLevel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AppliedOverrides = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppliedFloors = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SanctionsWorkflow = table.Column<bool>(type: "bit", nullable: false),
                    HasMissingData = table.Column<bool>(type: "bit", nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovalNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AsOf = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CalculatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConfigSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRiskAssessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerRiskAssessments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRiskAssessments_RiskMethodologies_MethodologyId",
                        column: x => x.MethodologyId,
                        principalTable: "RiskMethodologies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RiskCategoryFloors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyId = table.Column<long>(type: "bigint", nullable: false),
                    CategoryCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TriggerLevel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MinimumLevel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskCategoryFloors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskCategoryFloors_RiskMethodologies_MethodologyId",
                        column: x => x.MethodologyId,
                        principalTable: "RiskMethodologies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RiskFactorDefinitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CategoryCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataSource = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    FactorType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Weight = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    ScoringMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApplicableCustomerTypes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MissingDataHandling = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MissingDataScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    FreshnessDays = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskFactorDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskFactorDefinitions_RiskMethodologies_MethodologyId",
                        column: x => x.MethodologyId,
                        principalTable: "RiskMethodologies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RiskOverridePolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConditionDataSource = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConditionOperator = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConditionValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MinimumLevel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    IsSanctionsWorkflow = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskOverridePolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskOverridePolicies_RiskMethodologies_MethodologyId",
                        column: x => x.MethodologyId,
                        principalTable: "RiskMethodologies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RiskThresholds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MethodologyId = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MinScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    MaxScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    ReviewMonths = table.Column<int>(type: "int", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    IsSample = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskThresholds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskThresholds_RiskMethodologies_MethodologyId",
                        column: x => x.MethodologyId,
                        principalTable: "RiskMethodologies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    PermissionId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScreeningResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    SubjectName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProviderCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ListEntryId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ListVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EntryHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SubjectHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MatchScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    MatchMethod = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MatchedAttributesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Explanation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScreenedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reviewer = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Decision = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NeedsReview = table.Column<bool>(type: "bit", nullable: false),
                    NeedsReviewReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProviderResponseEncrypted = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreeningResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScreeningResults_ScreeningRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "ScreeningRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExternalSubject = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Team = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataScopeRegion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PreferredLanguage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TokenVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUsers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EntryId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PrimaryName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AliasesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NameSearchKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    Nationality = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VersionAdded = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastAmendedVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EntryHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchlistEntries_WatchlistSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "WatchlistSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlAlertCustomers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlAlertCustomers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlAlertCustomers_AmlAlerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "AmlAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AmlAlertCustomers_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlAlertRuleMatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionId = table.Column<long>(type: "bigint", nullable: true),
                    RuleVersionId = table.Column<long>(type: "bigint", nullable: true),
                    RuleVersion = table.Column<int>(type: "int", nullable: true),
                    MatchKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MatchedConditionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WindowEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MatchedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlAlertRuleMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlAlertRuleMatches_AmlAlerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "AmlAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlCaseAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LinkedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlCaseAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlCaseAlerts_AmlAlerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "AmlAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AmlCaseAlerts_AmlCases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "AmlCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlCaseCustomers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlCaseCustomers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlCaseCustomers_AmlCases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "AmlCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AmlCaseCustomers_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlCaseDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScanStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlCaseDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlCaseDocuments_AmlCases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "AmlCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlCaseNotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    Author = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SupersedesNoteId = table.Column<long>(type: "bigint", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlCaseNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlCaseNotes_AmlCases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "AmlCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegulatoryReports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReportType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Confidentiality = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Narrative = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IndicatorCodes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SubjectsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TransactionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AccountsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PreparedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReviewedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ValidationErrors = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmissionChannel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PayloadXml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FiuReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgementDetails = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ManualSubmissionEvidence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PreviousReportId = table.Column<long>(type: "bigint", nullable: true),
                    SuspicionFormedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsOverdue = table.Column<bool>(type: "bit", nullable: false),
                    EscalatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegulatoryReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegulatoryReports_AmlCases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "AmlCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlAlertTransactions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    TransactionId = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlAlertTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlAlertTransactions_AmlAlerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "AmlAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AmlAlertTransactions_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AmlCaseTransactions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    TransactionId = table.Column<long>(type: "bigint", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmlCaseTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AmlCaseTransactions_AmlCases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "AmlCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AmlCaseTransactions_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransactionPartyResolutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TransactionId = table.Column<long>(type: "bigint", nullable: false),
                    Side = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OriginalWalletExternalId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResolvedCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    Method = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Confidence = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    RuleVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionPartyResolutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionPartyResolutions_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransactionStatusHistories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TransactionId = table.Column<long>(type: "bigint", nullable: false),
                    SourceStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NormalizedStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceEventAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Applied = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionStatusHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionStatusHistories_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRiskAssessmentFactors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssessmentId = table.Column<long>(type: "bigint", nullable: false),
                    FactorCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FactorName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CategoryCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FactorVersion = table.Column<int>(type: "int", nullable: false),
                    DataSource = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DataReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ObservedValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NormalizedScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    Weight = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    Contribution = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    Included = table.Column<bool>(type: "bit", nullable: false),
                    MissingHandling = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Explanation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRiskAssessmentFactors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerRiskAssessmentFactors_CustomerRiskAssessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "CustomerRiskAssessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RiskFactorScoreMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FactorId = table.Column<long>(type: "bigint", nullable: false),
                    MatchType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RangeMin = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    RangeMax = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    Score = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Label = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskFactorScoreMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskFactorScoreMappings_RiskFactorDefinitions_FactorId",
                        column: x => x.FactorId,
                        principalTable: "RiskFactorDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPreferences_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRoles_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertStatusHistories_AlertId",
                table: "AlertStatusHistories",
                column: "AlertId");

            migrationBuilder.CreateIndex(
                name: "IX_AllowListEntries_SubjectType_SubjectId_SourceCode_ListEntryId",
                table: "AllowListEntries",
                columns: new[] { "SubjectType", "SubjectId", "SourceCode", "ListEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlertCustomers_AlertId_CustomerId",
                table: "AmlAlertCustomers",
                columns: new[] { "AlertId", "CustomerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlertCustomers_CustomerId",
                table: "AmlAlertCustomers",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlertRuleMatches_AlertId_MatchKey",
                table: "AmlAlertRuleMatches",
                columns: new[] { "AlertId", "MatchKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlerts_AlertNumber",
                table: "AmlAlerts",
                column: "AlertNumber");

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlerts_PrimaryCustomerId",
                table: "AmlAlerts",
                column: "PrimaryCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlerts_RuleId",
                table: "AmlAlerts",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlerts_Status_DueAt",
                table: "AmlAlerts",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlerts_TenantId_DeduplicationKey",
                table: "AmlAlerts",
                columns: new[] { "TenantId", "DeduplicationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlertTransactions_AlertId_TransactionId",
                table: "AmlAlertTransactions",
                columns: new[] { "AlertId", "TransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlAlertTransactions_TransactionId",
                table: "AmlAlertTransactions",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseAlerts_AlertId",
                table: "AmlCaseAlerts",
                column: "AlertId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseAlerts_CaseId_AlertId",
                table: "AmlCaseAlerts",
                columns: new[] { "CaseId", "AlertId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseCustomers_CaseId_CustomerId",
                table: "AmlCaseCustomers",
                columns: new[] { "CaseId", "CustomerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseCustomers_CustomerId",
                table: "AmlCaseCustomers",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseDocuments_CaseId",
                table: "AmlCaseDocuments",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseHistories_CaseId",
                table: "AmlCaseHistories",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseNotes_CaseId",
                table: "AmlCaseNotes",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlCases_CaseNumber",
                table: "AmlCases",
                column: "CaseNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlCases_PrimaryCustomerId",
                table: "AmlCases",
                column: "PrimaryCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseTasks_CaseId",
                table: "AmlCaseTasks",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseTeamMembers_CaseId_Username",
                table: "AmlCaseTeamMembers",
                columns: new[] { "CaseId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseTransactions_CaseId_TransactionId",
                table: "AmlCaseTransactions",
                columns: new[] { "CaseId", "TransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlCaseTransactions_TransactionId",
                table: "AmlCaseTransactions",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_AmlRuleExecutions_RuleVersionId_Mode_WindowEnd",
                table: "AmlRuleExecutions",
                columns: new[] { "RuleVersionId", "Mode", "WindowEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_AmlRuleExecutions_StartedAt",
                table: "AmlRuleExecutions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AmlRules_TenantId_Code",
                table: "AmlRules",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlRuleVersions_RuleId_Version",
                table: "AmlRuleVersions",
                columns: new[] { "RuleId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AmlRuleVersions_Status",
                table: "AmlRuleVersions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_TenantId",
                table: "AppUsers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_Username",
                table: "AppUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action",
                table: "AuditLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TimestampUtc",
                table: "AuditLogs",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorityRequests_CustomerId",
                table: "AuthorityRequests",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseStatusTransitions_TenantId_FromStatus_ToStatus",
                table: "CaseStatusTransitions",
                columns: new[] { "TenantId", "FromStatus", "ToStatus" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceActions_Status_NextAttemptAt",
                table: "ComplianceActions",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceActions_TenantId_IdempotencyKey",
                table: "ComplianceActions",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CurrentCustomerRiskProfiles_CustomerId",
                table: "CurrentCustomerRiskProfiles",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAcceptanceDecisions_CustomerId",
                table: "CustomerAcceptanceDecisions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerContacts_CustomerId",
                table: "CustomerContacts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerContacts_SearchToken",
                table: "CustomerContacts",
                column: "SearchToken");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDailyAggregates_CustomerId_BusinessDate_Direction",
                table: "CustomerDailyAggregates",
                columns: new[] { "CustomerId", "BusinessDate", "Direction" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDeviceSignals_CustomerId",
                table: "CustomerDeviceSignals",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDeviceSignals_DeviceId",
                table: "CustomerDeviceSignals",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocuments_CustomerId",
                table: "CustomerDocuments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerExitRequests_CustomerId",
                table: "CustomerExitRequests",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFieldChangeHistories_CustomerId",
                table: "CustomerFieldChangeHistories",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFieldDefinitions_Code_Status",
                table: "CustomerFieldDefinitions",
                columns: new[] { "Code", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFieldDefinitions_TenantId_Code_Version",
                table: "CustomerFieldDefinitions",
                columns: new[] { "TenantId", "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFieldGroups_TenantId_Code",
                table: "CustomerFieldGroups",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFieldOptions_TenantId_FieldCode_OptionCode",
                table: "CustomerFieldOptions",
                columns: new[] { "TenantId", "FieldCode", "OptionCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFieldValues_FieldDefinitionId",
                table: "CustomerFieldValues",
                column: "FieldDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldValues_Code_Date",
                table: "CustomerFieldValues",
                columns: new[] { "FieldCode", "ValueDate" })
                .Annotation("SqlServer:Include", new[] { "CustomerId", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_FieldValues_Code_Number",
                table: "CustomerFieldValues",
                columns: new[] { "FieldCode", "ValueNumber" })
                .Annotation("SqlServer:Include", new[] { "CustomerId", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_FieldValues_Code_Token",
                table: "CustomerFieldValues",
                columns: new[] { "FieldCode", "ValueToken" })
                .Annotation("SqlServer:Include", new[] { "CustomerId", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_FieldValues_Customer_Code_From",
                table: "CustomerFieldValues",
                columns: new[] { "CustomerId", "FieldCode", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFinancialProfiles_CustomerId_EffectiveFrom",
                table: "CustomerFinancialProfiles",
                columns: new[] { "CustomerId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFormLayouts_TenantId_CustomerType_RoleCode_Version",
                table: "CustomerFormLayouts",
                columns: new[] { "TenantId", "CustomerType", "RoleCode", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerIdentifications_CustomerId",
                table: "CustomerIdentifications",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerIdentifications_IdNumberToken",
                table: "CustomerIdentifications",
                column: "IdNumberToken");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRiskAssessmentFactors_AssessmentId",
                table: "CustomerRiskAssessmentFactors",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRiskAssessments_CustomerId_CalculatedAt",
                table: "CustomerRiskAssessments",
                columns: new[] { "CustomerId", "CalculatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRiskAssessments_CustomerId_TriggerEventId",
                table: "CustomerRiskAssessments",
                columns: new[] { "CustomerId", "TriggerEventId" },
                unique: true,
                filter: "[TriggerEventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRiskAssessments_MethodologyId",
                table: "CustomerRiskAssessments",
                column: "MethodologyId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_OnboardingAgentId",
                table: "Customers",
                column: "OnboardingAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Region",
                table: "Customers",
                column: "Region");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ScreeningStatus",
                table: "Customers",
                column: "ScreeningStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_SourceSystem_ExternalId",
                table: "Customers",
                columns: new[] { "TenantId", "SourceSystem", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_Status",
                table: "Customers",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityIssues_EntityId",
                table: "DataQualityIssues",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_DataQualityIssues_IssueType_Status",
                table: "DataQualityIssues",
                columns: new[] { "IssueType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DoNotOnboardEntries_IdNumberToken",
                table: "DoNotOnboardEntries",
                column: "IdNumberToken");

            migrationBuilder.CreateIndex(
                name: "IX_EddTasks_CustomerId",
                table: "EddTasks",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_EntityLinks_CustomerAId",
                table: "EntityLinks",
                column: "CustomerAId");

            migrationBuilder.CreateIndex(
                name: "IX_EntityLinks_CustomerBId",
                table: "EntityLinks",
                column: "CustomerBId");

            migrationBuilder.CreateIndex(
                name: "IX_ExportJobs_JobId",
                table: "ExportJobs",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_TenantId_FromCurrency_ToCurrency_EffectiveFrom_Version",
                table: "FxRates",
                columns: new[] { "TenantId", "FromCurrency", "ToCurrency", "EffectiveFrom", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HoldQueueItems_Status_SlaDueAt",
                table: "HoldQueueItems",
                columns: new[] { "Status", "SlaDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Holidays_TenantId_Date",
                table: "Holidays",
                columns: new[] { "TenantId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRecords_RunId",
                table: "IngestionRecords",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRecords_Status",
                table: "IngestionRecords",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRecords_TransactionId",
                table: "IngestionRecords",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRuns_SourceSystem_ReceivedAt",
                table: "IngestionRuns",
                columns: new[] { "SourceSystem", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_JobHeartbeats_JobName",
                table: "JobHeartbeats",
                column: "JobName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JurisdictionSettings_TenantId_Key_EffectiveFrom",
                table: "JurisdictionSettings",
                columns: new[] { "TenantId", "Key", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_LookupValues_Category_Code",
                table: "LookupValues",
                columns: new[] { "Category", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MockCoreCommands_TenantId_IdempotencyKey",
                table: "MockCoreCommands",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MockCoreTargetStates_TenantId_Target",
                table: "MockCoreTargetStates",
                columns: new[] { "TenantId", "Target" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_MessageId",
                table: "OutboxMessages",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt",
                table: "OutboxMessages",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OwnershipLinks_EntityCustomerId",
                table: "OwnershipLinks",
                column: "EntityCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnershipLinks_OwnerCustomerId",
                table: "OwnershipLinks",
                column: "OwnerCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Code",
                table: "Permissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishedEvents_MessageId",
                table: "PublishedEvents",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QaSamples_AlertId",
                table: "QaSamples",
                column: "AlertId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RawTransactionPayloads_IngestionRecordId",
                table: "RawTransactionPayloads",
                column: "IngestionRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeDecisions_DecisionId",
                table: "RealtimeDecisions",
                column: "DecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeDecisions_TenantId_SourceSystem_SourceTransactionId",
                table: "RealtimeDecisions",
                columns: new[] { "TenantId", "SourceSystem", "SourceTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegulatoryReports_CaseId",
                table: "RegulatoryReports",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_RegulatoryReports_ReportNumber",
                table: "RegulatoryReports",
                column: "ReportNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RetryQueueItems_Kind_Status_NextAttemptAt",
                table: "RetryQueueItems",
                columns: new[] { "Kind", "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RiskCategoryFloors_MethodologyId",
                table: "RiskCategoryFloors",
                column: "MethodologyId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskFactorCategories_TenantId_Code",
                table: "RiskFactorCategories",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskFactorDefinitions_DataSource",
                table: "RiskFactorDefinitions",
                column: "DataSource");

            migrationBuilder.CreateIndex(
                name: "IX_RiskFactorDefinitions_MethodologyId_Code",
                table: "RiskFactorDefinitions",
                columns: new[] { "MethodologyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskFactorScoreMappings_FactorId",
                table: "RiskFactorScoreMappings",
                column: "FactorId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskMethodologies_TenantId_Code_Version",
                table: "RiskMethodologies",
                columns: new[] { "TenantId", "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskOverridePolicies_MethodologyId",
                table: "RiskOverridePolicies",
                column: "MethodologyId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskOverrideRequests_CustomerId",
                table: "RiskOverrideRequests",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskReviewTasks_CustomerId",
                table: "RiskReviewTasks",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskReviewTasks_Status_DueDate",
                table: "RiskReviewTasks",
                columns: new[] { "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RiskThresholds_MethodologyId",
                table: "RiskThresholds",
                column: "MethodologyId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_PermissionId",
                table: "RolePermissions",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleId_PermissionId",
                table: "RolePermissions",
                columns: new[] { "RoleId", "PermissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Code",
                table: "Roles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningRequests_CustomerId",
                table: "ScreeningRequests",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningRequests_Status_NextAttemptAt",
                table: "ScreeningRequests",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningRequests_TenantId_IdempotencyKey",
                table: "ScreeningRequests",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningResults_CustomerId",
                table: "ScreeningResults",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningResults_RequestId",
                table: "ScreeningResults",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningResults_SubjectType_SubjectId_SourceCode_ListEntryId",
                table: "ScreeningResults",
                columns: new[] { "SubjectType", "SubjectId", "SourceCode", "ListEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningReviewHistories_ResultId",
                table: "ScreeningReviewHistories",
                column: "ResultId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlTotals_TenantId_SourceSystem_BusinessDate_Currency",
                table: "SourceControlTotals",
                columns: new[] { "TenantId", "SourceSystem", "BusinessDate", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceSystems_ClientId",
                table: "SourceSystems",
                column: "ClientId",
                unique: true,
                filter: "[ClientId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SourceSystems_TenantId_Code",
                table: "SourceSystems",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_TenantId_Key",
                table: "SystemSettings",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Code",
                table: "Tenants",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransactionPartyResolutions_TransactionId",
                table: "TransactionPartyResolutions",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AgentCustomerId_TimestampUtc",
                table: "Transactions",
                columns: new[] { "AgentCustomerId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_DeviceId",
                table: "Transactions",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_OriginalReference",
                table: "Transactions",
                column: "OriginalReference");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_OriginalSourceTransactionId",
                table: "Transactions",
                column: "OriginalSourceTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_OriginalTransactionId",
                table: "Transactions",
                column: "OriginalTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ReceiverCustomerId_TimestampUtc",
                table: "Transactions",
                columns: new[] { "ReceiverCustomerId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_SenderCustomerId_TimestampUtc",
                table: "Transactions",
                columns: new[] { "SenderCustomerId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_SourceSystem_BusinessDate",
                table: "Transactions",
                columns: new[] { "SourceSystem", "BusinessDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TenantId_SourceSystem_SourceTransactionId",
                table: "Transactions",
                columns: new[] { "TenantId", "SourceSystem", "SourceTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TimestampUtc",
                table: "Transactions",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionStatusHistories_TransactionId",
                table: "TransactionStatusHistories",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionStatusMappings_TenantId_SourceSystem_SourceStatus",
                table: "TransactionStatusMappings",
                columns: new[] { "TenantId", "SourceSystem", "SourceStatus" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserId_Key",
                table: "UserPreferences",
                columns: new[] { "UserId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId_RoleId",
                table: "UserRoles",
                columns: new[] { "UserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletMappings_TenantId_SourceSystem_ExternalWalletId",
                table: "WalletMappings",
                columns: new[] { "TenantId", "SourceSystem", "ExternalWalletId" });

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_CustomerId",
                table: "Wallets",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_ParentWalletId",
                table: "Wallets",
                column: "ParentWalletId");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_TenantId_SourceSystem_ExternalWalletId",
                table: "Wallets",
                columns: new[] { "TenantId", "SourceSystem", "ExternalWalletId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistEntries_SourceId_EntryId",
                table: "WatchlistEntries",
                columns: new[] { "SourceId", "EntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistSources_TenantId_Code",
                table: "WatchlistSources",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistVersions_SourceId_VersionLabel",
                table: "WatchlistVersions",
                columns: new[] { "SourceId", "VersionLabel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcceptancePolicies");

            migrationBuilder.DropTable(
                name: "ActionReconciliations");

            migrationBuilder.DropTable(
                name: "AlertClosurePolicies");

            migrationBuilder.DropTable(
                name: "AlertSlaPolicies");

            migrationBuilder.DropTable(
                name: "AlertStatusHistories");

            migrationBuilder.DropTable(
                name: "AllowListEntries");

            migrationBuilder.DropTable(
                name: "AmlAlertCustomers");

            migrationBuilder.DropTable(
                name: "AmlAlertRuleMatches");

            migrationBuilder.DropTable(
                name: "AmlAlertTransactions");

            migrationBuilder.DropTable(
                name: "AmlCaseAlerts");

            migrationBuilder.DropTable(
                name: "AmlCaseCustomers");

            migrationBuilder.DropTable(
                name: "AmlCaseDocuments");

            migrationBuilder.DropTable(
                name: "AmlCaseHistories");

            migrationBuilder.DropTable(
                name: "AmlCaseNotes");

            migrationBuilder.DropTable(
                name: "AmlCaseTasks");

            migrationBuilder.DropTable(
                name: "AmlCaseTeamMembers");

            migrationBuilder.DropTable(
                name: "AmlCaseTransactions");

            migrationBuilder.DropTable(
                name: "AmlRuleExecutions");

            migrationBuilder.DropTable(
                name: "AmlRuleVersions");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "AuthorityNotifications");

            migrationBuilder.DropTable(
                name: "AuthorityRequests");

            migrationBuilder.DropTable(
                name: "CaseStatusTransitions");

            migrationBuilder.DropTable(
                name: "ComplianceActionPolicies");

            migrationBuilder.DropTable(
                name: "ComplianceActions");

            migrationBuilder.DropTable(
                name: "CurrentCustomerRiskProfiles");

            migrationBuilder.DropTable(
                name: "CustomerAcceptanceDecisions");

            migrationBuilder.DropTable(
                name: "CustomerContacts");

            migrationBuilder.DropTable(
                name: "CustomerDailyAggregates");

            migrationBuilder.DropTable(
                name: "CustomerDeviceSignals");

            migrationBuilder.DropTable(
                name: "CustomerDocuments");

            migrationBuilder.DropTable(
                name: "CustomerExitRequests");

            migrationBuilder.DropTable(
                name: "CustomerFieldChangeHistories");

            migrationBuilder.DropTable(
                name: "CustomerFieldGroups");

            migrationBuilder.DropTable(
                name: "CustomerFieldOptions");

            migrationBuilder.DropTable(
                name: "CustomerFieldValues");

            migrationBuilder.DropTable(
                name: "CustomerFinancialProfiles");

            migrationBuilder.DropTable(
                name: "CustomerFormLayouts");

            migrationBuilder.DropTable(
                name: "CustomerIdentifications");

            migrationBuilder.DropTable(
                name: "CustomerRiskAssessmentFactors");

            migrationBuilder.DropTable(
                name: "DataQualityIssues");

            migrationBuilder.DropTable(
                name: "DoNotOnboardEntries");

            migrationBuilder.DropTable(
                name: "DueDiligenceLevelDefinitions");

            migrationBuilder.DropTable(
                name: "DueDiligenceSelectionRules");

            migrationBuilder.DropTable(
                name: "EddTasks");

            migrationBuilder.DropTable(
                name: "EntityLinks");

            migrationBuilder.DropTable(
                name: "ExportJobs");

            migrationBuilder.DropTable(
                name: "FxRates");

            migrationBuilder.DropTable(
                name: "HoldQueueItems");

            migrationBuilder.DropTable(
                name: "Holidays");

            migrationBuilder.DropTable(
                name: "IngestionRecords");

            migrationBuilder.DropTable(
                name: "IngestionRuns");

            migrationBuilder.DropTable(
                name: "InstitutionalRiskAssessments");

            migrationBuilder.DropTable(
                name: "JobHeartbeats");

            migrationBuilder.DropTable(
                name: "JurisdictionSettings");

            migrationBuilder.DropTable(
                name: "LegalHolds");

            migrationBuilder.DropTable(
                name: "LookupValues");

            migrationBuilder.DropTable(
                name: "MockCoreCommands");

            migrationBuilder.DropTable(
                name: "MockCoreTargetStates");

            migrationBuilder.DropTable(
                name: "ModelGovernanceRecords");

            migrationBuilder.DropTable(
                name: "OperationalAlerts");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "OwnershipLinks");

            migrationBuilder.DropTable(
                name: "PaymentTransparencyRules");

            migrationBuilder.DropTable(
                name: "PublishedEvents");

            migrationBuilder.DropTable(
                name: "QaSamples");

            migrationBuilder.DropTable(
                name: "QaSamplingConfigs");

            migrationBuilder.DropTable(
                name: "RawTransactionPayloads");

            migrationBuilder.DropTable(
                name: "RealtimeDecisions");

            migrationBuilder.DropTable(
                name: "RealtimeFallbackPolicies");

            migrationBuilder.DropTable(
                name: "ReconciliationResults");

            migrationBuilder.DropTable(
                name: "RegulatoryReports");

            migrationBuilder.DropTable(
                name: "RetentionPolicies");

            migrationBuilder.DropTable(
                name: "RetentionRuns");

            migrationBuilder.DropTable(
                name: "RetryQueueItems");

            migrationBuilder.DropTable(
                name: "RiskCategoryFloors");

            migrationBuilder.DropTable(
                name: "RiskFactorCategories");

            migrationBuilder.DropTable(
                name: "RiskFactorScoreMappings");

            migrationBuilder.DropTable(
                name: "RiskOverridePolicies");

            migrationBuilder.DropTable(
                name: "RiskOverrideRequests");

            migrationBuilder.DropTable(
                name: "RiskReviewTasks");

            migrationBuilder.DropTable(
                name: "RiskThresholds");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "ScreeningProviderStatuses");

            migrationBuilder.DropTable(
                name: "ScreeningResults");

            migrationBuilder.DropTable(
                name: "ScreeningReviewHistories");

            migrationBuilder.DropTable(
                name: "ScreeningSuppressionLogs");

            migrationBuilder.DropTable(
                name: "SourceControlTotals");

            migrationBuilder.DropTable(
                name: "SourceSystems");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "TransactionPartyResolutions");

            migrationBuilder.DropTable(
                name: "TransactionStatusHistories");

            migrationBuilder.DropTable(
                name: "TransactionStatusMappings");

            migrationBuilder.DropTable(
                name: "TuningRuns");

            migrationBuilder.DropTable(
                name: "Typologies");

            migrationBuilder.DropTable(
                name: "TypologyControls");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "WalletMappings");

            migrationBuilder.DropTable(
                name: "Wallets");

            migrationBuilder.DropTable(
                name: "WatchlistEntries");

            migrationBuilder.DropTable(
                name: "WatchlistVersions");

            migrationBuilder.DropTable(
                name: "AmlAlerts");

            migrationBuilder.DropTable(
                name: "AmlRules");

            migrationBuilder.DropTable(
                name: "CustomerFieldDefinitions");

            migrationBuilder.DropTable(
                name: "CustomerRiskAssessments");

            migrationBuilder.DropTable(
                name: "AmlCases");

            migrationBuilder.DropTable(
                name: "RiskFactorDefinitions");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "ScreeningRequests");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "WatchlistSources");

            migrationBuilder.DropTable(
                name: "RiskMethodologies");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
