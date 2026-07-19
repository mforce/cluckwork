using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VoidedEntryVacatesNaturalKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DailyEntries_NaturalKey",
                table: "DailyEntries");

            migrationBuilder.CreateIndex(
                name: "IX_DailyEntries_NaturalKey",
                table: "DailyEntries",
                columns: new[] { "AccountId", "FarmId", "HouseId", "FlockId", "Date" },
                unique: true,
                filter: "\"Status\" <> 'Voided'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DailyEntries_NaturalKey",
                table: "DailyEntries");

            migrationBuilder.CreateIndex(
                name: "IX_DailyEntries_NaturalKey",
                table: "DailyEntries",
                columns: new[] { "AccountId", "FarmId", "HouseId", "FlockId", "Date" },
                unique: true);
        }
    }
}
