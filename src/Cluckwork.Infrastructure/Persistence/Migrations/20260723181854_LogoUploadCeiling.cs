using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogoUploadCeiling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_farm_logos_content_length",
                table: "FarmLogos");

            migrationBuilder.AddCheckConstraint(
                name: "ck_farm_logos_content_length",
                table: "FarmLogos",
                sql: "octet_length(\"Content\") > 0 AND octet_length(\"Content\") <= 5242880");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_farm_logos_content_length",
                table: "FarmLogos");

            // A logo stored under the wider cap can exceed the 1 MB the old
            // constraint allows, and Postgres validates a new CHECK against
            // existing rows — so re-adding it while such a row exists aborts the
            // whole rollback (codex review of #123). A downgrade to a 1 MB world
            // genuinely cannot keep a 2 MB logo, so the row is removed first and
            // the farm falls back to app branding, which is the same graceful
            // fallback a missing logo already has. Destructive, but a rollback
            // that throws is the worse failure.
            migrationBuilder.Sql(
                "DELETE FROM \"FarmLogos\" WHERE octet_length(\"Content\") > 1048576;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_farm_logos_content_length",
                table: "FarmLogos",
                sql: "octet_length(\"Content\") > 0 AND octet_length(\"Content\") <= 1048576");
        }
    }
}
