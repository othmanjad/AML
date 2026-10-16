using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AML.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MonitoringCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GoodsCategory",
                table: "Transactions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InvoiceAmount",
                table: "Transactions",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceCurrency",
                table: "Transactions",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceNumber",
                table: "Transactions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HouseholdId",
                table: "Customers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CurrencyTransactionReports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TransactionCount = table.Column<int>(type: "int", nullable: false),
                    TotalBaseAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CashInBaseAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CashOutBaseAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    BaseCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Threshold = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TransactionIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExportedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmissionReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyTransactionReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurrencyTransactionReports_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndustryBenchmarks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Metric = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SegmentField = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SegmentValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndustryBenchmarks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WalletBalanceSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceSystem = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExternalWalletId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WalletId = table.Column<long>(type: "bigint", nullable: true),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    BaseBalance = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletBalanceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_HouseholdId",
                table: "Customers",
                columns: new[] { "TenantId", "HouseholdId" });

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyTransactionReports_CustomerId",
                table: "CurrencyTransactionReports",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyTransactionReports_ReportNumber",
                table: "CurrencyTransactionReports",
                column: "ReportNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyTransactionReports_Status",
                table: "CurrencyTransactionReports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyTransactionReports_TenantId_CustomerId_BusinessDate",
                table: "CurrencyTransactionReports",
                columns: new[] { "TenantId", "CustomerId", "BusinessDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndustryBenchmarks_TenantId_Metric_SegmentField_SegmentValue_EffectiveFrom",
                table: "IndustryBenchmarks",
                columns: new[] { "TenantId", "Metric", "SegmentField", "SegmentValue", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_WalletBalanceSnapshots_CustomerId_BusinessDate",
                table: "WalletBalanceSnapshots",
                columns: new[] { "CustomerId", "BusinessDate" });

            migrationBuilder.CreateIndex(
                name: "IX_WalletBalanceSnapshots_TenantId_SourceSystem_ExternalWalletId_BusinessDate",
                table: "WalletBalanceSnapshots",
                columns: new[] { "TenantId", "SourceSystem", "ExternalWalletId", "BusinessDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CurrencyTransactionReports");

            migrationBuilder.DropTable(
                name: "IndustryBenchmarks");

            migrationBuilder.DropTable(
                name: "WalletBalanceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Customers_TenantId_HouseholdId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "GoodsCategory",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "InvoiceAmount",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "InvoiceCurrency",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "InvoiceNumber",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "HouseholdId",
                table: "Customers");
        }
    }
}
