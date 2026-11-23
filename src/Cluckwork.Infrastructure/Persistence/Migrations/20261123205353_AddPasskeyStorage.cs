using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPasskeyStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PasskeyCount",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AspNetUserPasskeys",
                columns: table => new
                {
                    CredentialId = table.Column<byte[]>(type: "bytea", maxLength: 1024, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserPasskeys", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_AspNetUserPasskeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserPasskeys_AspNetUsers_UserId_AccountId",
                        columns: x => new { x.UserId, x.AccountId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "Id", "AccountId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "passkey_ceremonies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<short>(type: "smallint", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_passkey_ceremonies", x => x.Id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_PasskeyCount",
                table: "AspNetUsers",
                sql: "\"PasskeyCount\" >= 0 AND \"PasskeyCount\" <= 5");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserPasskeys_UserId_AccountId",
                table: "AspNetUserPasskeys",
                columns: new[] { "UserId", "AccountId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetUserPasskeys");

            migrationBuilder.DropTable(
                name: "passkey_ceremonies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_PasskeyCount",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PasskeyCount",
                table: "AspNetUsers");
        }
    }
}
