using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactorStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SignedInAt",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "AspNetUserTokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // #1276 — hand-inserted, as AddAccountIdToUserRoles (#670) did:
            // backfill from the user, so the FK below accepts any existing row,
            // then drop the add-column default, whose value means "no tenant".
            // UserTokenAccountIdMigrationTests pins both.
            migrationBuilder.Sql("""
                UPDATE "AspNetUserTokens" ut
                SET "AccountId" = u."AccountId"
                FROM "AspNetUsers" u
                WHERE u."Id" = ut."UserId";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AspNetUserTokens" ALTER COLUMN "AccountId" DROP DEFAULT;
                """);

            migrationBuilder.AddColumn<int>(
                name: "SecondFactorFailures",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SecondFactorFailuresSince",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SecondFactorRequiredSince",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SecondFactorSignIns",
                table: "AspNetUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SecondFactorWatermark",
                table: "AspNetUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserTokens_UserId_AccountId",
                table: "AspNetUserTokens",
                columns: new[] { "UserId", "AccountId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserTokens_AspNetUsers_UserId_AccountId",
                table: "AspNetUserTokens",
                columns: new[] { "UserId", "AccountId" },
                principalTable: "AspNetUsers",
                principalColumns: new[] { "Id", "AccountId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserTokens_AspNetUsers_UserId_AccountId",
                table: "AspNetUserTokens");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUserTokens_UserId_AccountId",
                table: "AspNetUserTokens");

            migrationBuilder.DropColumn(
                name: "SignedInAt",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "AspNetUserTokens");

            migrationBuilder.DropColumn(
                name: "SecondFactorFailures",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SecondFactorFailuresSince",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SecondFactorRequiredSince",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SecondFactorSignIns",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SecondFactorWatermark",
                table: "AspNetUsers");
        }
    }
}
