using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AML.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EconomicLegOf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EconomicLegOfId",
                table: "Transactions",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EconomicLegOfId",
                table: "Transactions");
        }
    }
}
