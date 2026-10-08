using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOAuthApplicationCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: "OpenIddictApplications",
                type: "timestamp with time zone",
                nullable: true);

            // No history records when an existing application registered, so it gets
            // #819's unknown sentinel, and an unapproved one expires at the next sweep.
            // Only non-Production databases hold any (#795).
            migrationBuilder.Sql(
                """
                UPDATE "OpenIddictApplications" SET "CreatedAtUtc" = '1970-01-01 00:00:00+00';
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: "OpenIddictApplications",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            // #819's function, attached the way #819 attaches it to every created-only table.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER "TR_OpenIddictApplications_BusinessRecordTimestamps"
                BEFORE INSERT OR UPDATE ON "OpenIddictApplications"
                FOR EACH ROW EXECUTE FUNCTION "StampCreatedBusinessRecord"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER "TR_OpenIddictApplications_BusinessRecordTimestamps" ON "OpenIddictApplications";
                """);

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "OpenIddictApplications");
        }
    }
}
