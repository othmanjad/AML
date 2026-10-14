using AML.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AML.Infrastructure.Persistence.Migrations
{
    /// <summary>Database-level protection: append-only audit log and immutable assessment/evidence records.</summary>
    [DbContext(typeof(AmlDbContext))]
    [Migration("20261014230000_ProtectionTriggers")]
    public partial class ProtectionTriggers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TRIGGER dbo.TR_AuditLogs_Protect ON dbo.AuditLogs INSTEAD OF UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    THROW 51001, 'AuditLogs are append-only: UPDATE and DELETE are not permitted.', 1;
END");
            migrationBuilder.Sql(@"
CREATE TRIGGER dbo.TR_RiskAssessments_Immutable ON dbo.CustomerRiskAssessments AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(CalculatedScore) OR UPDATE(CalculatedLevel) OR UPDATE(FinalLevel) OR UPDATE(MethodologyId) OR UPDATE(MethodologyVersion)
       OR UPDATE(Explanation) OR UPDATE(ConfigSnapshotJson) OR UPDATE(CustomerId) OR UPDATE(AppliedOverrides) OR UPDATE(AppliedFloors)
       OR UPDATE(MandatoryOverrideLevel) OR UPDATE(AsOf) OR UPDATE(CalculatedAt) OR UPDATE([Trigger]) OR UPDATE(TriggerEventId)
        THROW 51002, 'Risk assessments are immutable snapshots; create a new assessment instead.', 1;
END");
            migrationBuilder.Sql(@"
CREATE TRIGGER dbo.TR_RiskAssessments_NoDelete ON dbo.CustomerRiskAssessments INSTEAD OF DELETE AS
BEGIN
    SET NOCOUNT ON;
    THROW 51003, 'Risk assessments cannot be deleted.', 1;
END");
            migrationBuilder.Sql(@"
CREATE TRIGGER dbo.TR_RiskAssessmentFactors_Immutable ON dbo.CustomerRiskAssessmentFactors INSTEAD OF UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    THROW 51004, 'Assessment factor explanations are immutable.', 1;
END");
            migrationBuilder.Sql(@"
CREATE TRIGGER dbo.TR_AlertRuleMatches_Immutable ON dbo.AmlAlertRuleMatches INSTEAD OF UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    THROW 51005, 'Alert evidence snapshots are immutable.', 1;
END");
            migrationBuilder.Sql(@"
CREATE TRIGGER dbo.TR_CaseNotes_Immutable ON dbo.AmlCaseNotes INSTEAD OF UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    THROW 51006, 'Case notes are immutable; add a correcting note instead.', 1;
END");
            migrationBuilder.Sql(@"
CREATE TRIGGER dbo.TR_RegulatoryReports_SubmittedImmutable ON dbo.RegulatoryReports AFTER UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.Id = d.Id
               WHERE d.SubmittedAt IS NOT NULL AND (i.Id IS NULL
                  OR ISNULL(i.PayloadXml, N'') <> ISNULL(d.PayloadXml, N'') OR ISNULL(i.PayloadHash, N'') <> ISNULL(d.PayloadHash, N'')
                  OR ISNULL(i.Narrative, N'') <> ISNULL(d.Narrative, N'') OR i.SubjectsJson <> d.SubjectsJson OR i.TransactionsJson <> d.TransactionsJson
                  OR ISNULL(i.SubmittedAt, '19000101') <> d.SubmittedAt))
        THROW 51007, 'Submitted regulatory report evidence is immutable; create an amendment.', 1;
END");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var t in new[] { "TR_AuditLogs_Protect", "TR_RiskAssessments_Immutable", "TR_RiskAssessments_NoDelete", "TR_RiskAssessmentFactors_Immutable",
                         "TR_AlertRuleMatches_Immutable", "TR_CaseNotes_Immutable", "TR_RegulatoryReports_SubmittedImmutable" })
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS dbo.{t}");
        }
    }
}
