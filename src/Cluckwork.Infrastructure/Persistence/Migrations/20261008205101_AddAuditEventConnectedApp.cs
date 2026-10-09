using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEventConnectedApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConnectedAppClientId",
                table: "AuditEvents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectedAppName",
                table: "AuditEvents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ConnectedApp",
                table: "AuditEvents",
                columns: new[] { "AccountId", "OccurredAtUtc" },
                filter: "\"ConnectedAppClientId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_ConnectedApp",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "ConnectedAppClientId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "ConnectedAppName",
                table: "AuditEvents");
        }
    }
}
