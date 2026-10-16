using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AML.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TransactionDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Commission",
                table: "Transactions",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mcc",
                table: "Transactions",
                type: "nvarchar(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReceiverBalanceAfter",
                table: "Transactions",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReceiverBalanceBefore",
                table: "Transactions",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SenderBalanceAfter",
                table: "Transactions",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SenderBalanceBefore",
                table: "Transactions",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubType",
                table: "Transactions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Commission",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "Mcc",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ReceiverBalanceAfter",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ReceiverBalanceBefore",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "SenderBalanceAfter",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "SenderBalanceBefore",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "SubType",
                table: "Transactions");
        }
    }
}
