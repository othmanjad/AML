namespace AML.Application.Common;

/// <summary>Granular permission catalogue (module.action). Roles are configurable bundles of these.</summary>
public static class Perm
{
    public const string DashboardRead = "dashboard.read";
    public const string CustomersRead = "customers.read";
    public const string CustomersWrite = "customers.write";
    public const string CustomersBulk = "customers.bulk";
    public const string CustomersUnmask = "customers.unmask";
    public const string CustomersExit = "customers.exit";
    public const string CustomersExitApprove = "customers.exit.approve";
    public const string CddDecide = "cdd.decide";
    public const string CddSeniorApprove = "cdd.senior-approve";
    public const string FieldsConfig = "config.fields";
    public const string FieldsApprove = "config.fields.approve";
    public const string LayoutsConfig = "config.layouts";
    public const string RiskConfig = "risk.config";
    public const string RiskApprove = "risk.approve";
    public const string RiskAssess = "risk.assess";
    public const string RiskRead = "risk.read";
    public const string RiskOverrideRequest = "risk.override.request";
    public const string RiskOverrideApprove = "risk.override.approve";
    public const string RiskReviewWork = "risk.review.work";
    public const string TransactionsRead = "transactions.read";
    public const string TransactionsRaw = "transactions.raw";
    public const string IntegrationIngest = "integrations.ingest";
    public const string RulesRead = "rules.read";
    public const string RulesConfig = "rules.config";
    public const string RulesApprove = "rules.approve";
    public const string RulesRun = "rules.run";
    public const string RulesBacktestAlerts = "rules.backtest.alerts";
    public const string AlertsRead = "alerts.read";
    public const string AlertsWork = "alerts.work";
    public const string AlertsApprove = "alerts.approve";
    public const string CasesRead = "cases.read";
    public const string CasesWork = "cases.work";
    public const string CasesApprove = "cases.approve";
    public const string CasesRestricted = "cases.restricted";
    public const string ScreeningRead = "screening.read";
    public const string ScreeningRun = "screening.run";
    public const string ScreeningReview = "screening.review";
    public const string ScreeningRaw = "screening.raw";
    public const string WatchlistsManage = "watchlists.manage";
    public const string ActionsRead = "actions.read";
    public const string ActionsRequest = "actions.request";
    public const string ActionsApprove = "actions.approve";
    public const string RealtimeDecide = "realtime.decide";
    public const string HoldsWork = "holds.work";
    public const string StrPrepare = "str.prepare";
    public const string StrApprove = "str.approve";
    public const string StrSubmit = "str.submit";
    public const string AuthorityManage = "authority.manage";
    public const string ReportsRead = "reports.read";
    public const string ReportsExport = "reports.export";
    public const string ReportsExportSensitive = "reports.export.sensitive";
    public const string ReportsExportRestricted = "reports.export.restricted";
    public const string OperationsRead = "operations.read";
    public const string OperationsReplay = "operations.replay";
    public const string EntityRead = "entity.read";
    public const string DevicesUnmask = "devices.unmask";
    public const string EffectivenessRead = "effectiveness.read";
    public const string TuningRun = "tuning.run";
    public const string QaReview = "qa.review";
    public const string RetentionManage = "retention.manage";
    public const string RetentionApprove = "retention.approve";
    public const string JurisdictionManage = "jurisdiction.manage";
    public const string JurisdictionApprove = "jurisdiction.approve";
    public const string AdminUsers = "admin.users";
    public const string AdminLookups = "admin.lookups";
    public const string AdminWalletMappings = "admin.wallet-mappings";
    public const string AdminFx = "admin.fx";
    public const string AdminSettings = "admin.settings";
    public const string AdminJobs = "admin.jobs";
    public const string AuditRead = "audit.read";

    public static IEnumerable<string> All() =>
        typeof(Perm).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);
}

public static class RoleCodes
{
    public const string SystemAdministrator = "SystemAdministrator";
    public const string AmlManager = "AMLManager";
    public const string ComplianceOfficer = "ComplianceOfficer";
    public const string SeniorReviewer = "SeniorReviewer";
    public const string Mlro = "MLRO";
    public const string Investigator = "Investigator";
    public const string Auditor = "Auditor";
    public const string OperationsUser = "OperationsUser";
    public const string IntegrationClient = "IntegrationClient";

    /// <summary>Default seed role → permission bundles (configurable after installation).</summary>
    public static readonly Dictionary<string, string[]> Defaults = new()
    {
        [SystemAdministrator] = new[]
        {
            Perm.DashboardRead, Perm.CustomersRead, Perm.FieldsConfig, Perm.FieldsApprove, Perm.LayoutsConfig, Perm.RiskRead, Perm.RulesRead,
            Perm.AdminUsers, Perm.AdminLookups, Perm.AdminWalletMappings, Perm.AdminFx, Perm.AdminSettings, Perm.AdminJobs, Perm.AuditRead,
            Perm.OperationsRead, Perm.OperationsReplay, Perm.JurisdictionManage, Perm.TransactionsRead, Perm.ReportsRead, Perm.ReportsExport,
            Perm.CasesRead, Perm.CasesRestricted, Perm.AlertsRead, Perm.ScreeningRead, Perm.WatchlistsManage, Perm.RetentionManage, Perm.EffectivenessRead
        },
        [AmlManager] = new[]
        {
            Perm.DashboardRead, Perm.CustomersRead, Perm.CustomersWrite, Perm.CustomersBulk, Perm.CustomersUnmask, Perm.CustomersExit, Perm.CustomersExitApprove,
            Perm.CddDecide, Perm.CddSeniorApprove, Perm.FieldsConfig, Perm.FieldsApprove, Perm.LayoutsConfig, Perm.RiskConfig, Perm.RiskApprove, Perm.RiskAssess, Perm.RiskRead,
            Perm.RiskOverrideRequest, Perm.RiskOverrideApprove, Perm.RiskReviewWork, Perm.TransactionsRead, Perm.TransactionsRaw, Perm.RulesRead, Perm.RulesConfig,
            Perm.RulesApprove, Perm.RulesRun, Perm.RulesBacktestAlerts, Perm.AlertsRead, Perm.AlertsWork, Perm.AlertsApprove, Perm.CasesRead, Perm.CasesWork,
            Perm.CasesApprove, Perm.CasesRestricted, Perm.ScreeningRead, Perm.ScreeningRun, Perm.ScreeningReview, Perm.ScreeningRaw, Perm.WatchlistsManage,
            Perm.ActionsRead, Perm.ActionsRequest, Perm.ActionsApprove, Perm.HoldsWork, Perm.StrPrepare, Perm.AuthorityManage, Perm.ReportsRead, Perm.ReportsExport,
            Perm.ReportsExportSensitive, Perm.ReportsExportRestricted, Perm.OperationsRead, Perm.OperationsReplay, Perm.EntityRead, Perm.DevicesUnmask,
            Perm.EffectivenessRead, Perm.TuningRun, Perm.QaReview, Perm.RetentionManage, Perm.RetentionApprove, Perm.JurisdictionManage, Perm.JurisdictionApprove,
            Perm.AuditRead, Perm.AdminFx, Perm.AdminWalletMappings
        },
        [ComplianceOfficer] = new[]
        {
            Perm.DashboardRead, Perm.CustomersRead, Perm.CustomersWrite, Perm.CustomersUnmask, Perm.CustomersExit, Perm.CddDecide, Perm.RiskAssess, Perm.RiskRead,
            Perm.RiskOverrideRequest, Perm.RiskReviewWork, Perm.TransactionsRead, Perm.RulesRead, Perm.RulesConfig, Perm.RulesRun, Perm.AlertsRead, Perm.AlertsWork,
            Perm.CasesRead, Perm.CasesWork, Perm.ScreeningRead, Perm.ScreeningRun, Perm.ScreeningReview, Perm.ActionsRead, Perm.ActionsRequest, Perm.HoldsWork,
            Perm.StrPrepare, Perm.AuthorityManage, Perm.ReportsRead, Perm.ReportsExport, Perm.EntityRead, Perm.EffectivenessRead, Perm.FieldsConfig, Perm.RiskConfig,
            Perm.OperationsRead
        },
        [SeniorReviewer] = new[]
        {
            Perm.DashboardRead, Perm.CustomersRead, Perm.CustomersUnmask, Perm.CustomersExitApprove, Perm.CddSeniorApprove, Perm.CddDecide, Perm.RiskRead, Perm.RiskAssess,
            Perm.RiskApprove, Perm.RiskOverrideApprove, Perm.RiskReviewWork, Perm.TransactionsRead, Perm.RulesRead, Perm.RulesApprove, Perm.AlertsRead, Perm.AlertsWork,
            Perm.AlertsApprove, Perm.CasesRead, Perm.CasesWork, Perm.ScreeningRead, Perm.ScreeningReview, Perm.ActionsRead, Perm.ActionsApprove, Perm.HoldsWork,
            Perm.ReportsRead, Perm.ReportsExport, Perm.EntityRead, Perm.EffectivenessRead, Perm.QaReview, Perm.FieldsApprove, Perm.TuningRun, Perm.JurisdictionApprove
        },
        [Mlro] = new[]
        {
            Perm.DashboardRead, Perm.CustomersRead, Perm.CustomersUnmask, Perm.CustomersExitApprove, Perm.CddSeniorApprove, Perm.RiskRead, Perm.RiskApprove,
            Perm.RiskOverrideApprove, Perm.TransactionsRead, Perm.RulesRead, Perm.RulesApprove, Perm.AlertsRead, Perm.AlertsWork, Perm.AlertsApprove, Perm.CasesRead,
            Perm.CasesWork, Perm.CasesApprove, Perm.CasesRestricted, Perm.ScreeningRead, Perm.ScreeningReview, Perm.ActionsRead, Perm.ActionsApprove, Perm.HoldsWork,
            Perm.StrPrepare, Perm.StrApprove, Perm.StrSubmit, Perm.AuthorityManage, Perm.ReportsRead, Perm.ReportsExport, Perm.ReportsExportSensitive,
            Perm.ReportsExportRestricted, Perm.EntityRead, Perm.DevicesUnmask, Perm.EffectivenessRead, Perm.QaReview, Perm.AuditRead, Perm.RetentionApprove
        },
        [Investigator] = new[]
        {
            Perm.DashboardRead, Perm.CustomersRead, Perm.TransactionsRead, Perm.AlertsRead, Perm.AlertsWork, Perm.CasesRead, Perm.CasesWork, Perm.ScreeningRead,
            Perm.ScreeningReview, Perm.StrPrepare, Perm.EntityRead, Perm.ReportsRead, Perm.RiskRead
        },
        [Auditor] = new[]
        {
            Perm.DashboardRead, Perm.CustomersRead, Perm.RiskRead, Perm.TransactionsRead, Perm.RulesRead, Perm.AlertsRead, Perm.CasesRead, Perm.ScreeningRead,
            Perm.ActionsRead, Perm.ReportsRead, Perm.OperationsRead, Perm.EntityRead, Perm.EffectivenessRead, Perm.AuditRead
        },
        [OperationsUser] = new[]
        {
            Perm.DashboardRead, Perm.CustomersRead, Perm.TransactionsRead, Perm.OperationsRead, Perm.ScreeningRead, Perm.EntityRead
        },
        [IntegrationClient] = new[] { Perm.IntegrationIngest, Perm.RealtimeDecide },
    };
}
