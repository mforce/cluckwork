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

            migrationBuilder.AddCheckConstraint(
                name: "ck_farm_logos_content_length",
                table: "FarmLogos",
                sql: "octet_length(\"Content\") > 0 AND octet_length(\"Content\") <= 1048576");
        }
    }
}
