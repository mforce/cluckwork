using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyEntryStepperUnitPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreferredStepperUnit",
                table: "AspNetUsers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // "Individual", not "": the generated default is an EF/EDM-level
            // fallback, but this column round-trips through Account's own
            // HasConversion<string>() enum mapping — an existing row (the
            // seeded default account) backfilled with "" would fail to parse
            // as an EggUnit on its very next read. "Individual" matches
            // Account.DefaultStepperUnit's own field default (= EggUnit.Individual),
            // so an existing farm keeps today's +1/-1 stepper behavior unchanged.
            migrationBuilder.AddColumn<string>(
                name: "DefaultStepperUnit",
                table: "Accounts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Individual");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreferredStepperUnit",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DefaultStepperUnit",
                table: "Accounts");
        }
    }
}
