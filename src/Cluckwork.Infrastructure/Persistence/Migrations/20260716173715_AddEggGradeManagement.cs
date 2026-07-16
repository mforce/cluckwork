using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEggGradeManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EggGrades_AccountId_FarmId_Name",
                table: "EggGrades");

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "EggGrades",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The old index was case-sensitive, so "Large"/"large" may coexist.
            // Rename later duplicates (keeping the earliest row's name) so the
            // stricter index below can't brick startup on existing data. The
            // suffix stays within MaxNameLength (42 + '-' + 7 = 50).
            migrationBuilder.Sql(
                """
                UPDATE "EggGrades" g
                SET "Name" = left(g."Name", 42) || '-' || left(g."Id"::text, 7)
                WHERE EXISTS (
                    SELECT 1 FROM "EggGrades" d
                    WHERE d."AccountId" = g."AccountId"
                      AND d."FarmId" = g."FarmId"
                      AND lower(d."Name") = lower(g."Name")
                      AND d."Id" < g."Id");
                """);

            // Case-insensitive per-farm name uniqueness. Expression indexes
            // aren't representable in the EF model, so this lives as raw SQL;
            // it replaces the case-sensitive IX dropped above.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_EggGrades_AccountId_FarmId_LowerName"
                    ON "EggGrades" ("AccountId", "FarmId", lower("Name"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_EggGrades_AccountId_FarmId_LowerName";
                """);

            migrationBuilder.DropColumn(
                name: "Version",
                table: "EggGrades");

            migrationBuilder.CreateIndex(
                name: "IX_EggGrades_AccountId_FarmId_Name",
                table: "EggGrades",
                columns: new[] { "AccountId", "FarmId", "Name" },
                unique: true);
        }
    }
}
