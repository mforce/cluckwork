using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEggGradeLowStockFloor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LowStockFloor",
                table: "EggGrades",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_EggGrades_LowStockFloor",
                table: "EggGrades",
                sql: "\"LowStockFloor\" IS NULL OR \"LowStockFloor\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EggGrades_LowStockFloor",
                table: "EggGrades");

            migrationBuilder.DropColumn(
                name: "LowStockFloor",
                table: "EggGrades");
        }
    }
}
