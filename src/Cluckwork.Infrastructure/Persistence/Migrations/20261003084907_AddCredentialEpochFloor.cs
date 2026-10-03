using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialEpochFloor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #1031 — validates existing rows and fails on any below 1 without
            // repairing it. The preflight and why 1 is never a safe repair are
            // in docs/decisions/364-credential-epoch-revocation.md.
            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_CredentialEpoch",
                table: "AspNetUsers",
                sql: "\"CredentialEpoch\" >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_CredentialEpoch",
                table: "AspNetUsers");
        }
    }
}
