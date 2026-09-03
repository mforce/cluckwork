using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountIdToUserRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "AspNetUserRoles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // #670 — hand-inserted, and load-bearing on every database that
            // already holds role rows (every real one; the test suite's is
            // empty, which is why UserRoleAccountIdMigrationTests exists).
            // (1) Backfill from the user: the FK below would otherwise refuse
            // every pre-existing row, whose AccountId is still the add-column
            // default. (2) Drop that default: EF leaves it on the column for
            // good, and a tenant column whose default means "no tenant" is
            // wrong on the record (docs/schema would carry it) even though the
            // FK rejects the value at runtime.
            migrationBuilder.Sql("""
                UPDATE "AspNetUserRoles" ur
                SET "AccountId" = u."AccountId"
                FROM "AspNetUsers" u
                WHERE u."Id" = ur."UserId";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AspNetUserRoles" ALTER COLUMN "AccountId" DROP DEFAULT;
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AspNetUsers_Id_AccountId",
                table: "AspNetUsers",
                columns: new[] { "Id", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_UserId_AccountId",
                table: "AspNetUserRoles",
                columns: new[] { "UserId", "AccountId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserRoles_AspNetUsers_UserId_AccountId",
                table: "AspNetUserRoles",
                columns: new[] { "UserId", "AccountId" },
                principalTable: "AspNetUsers",
                principalColumns: new[] { "Id", "AccountId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserRoles_AspNetUsers_UserId_AccountId",
                table: "AspNetUserRoles");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AspNetUsers_Id_AccountId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUserRoles_UserId_AccountId",
                table: "AspNetUserRoles");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "AspNetUserRoles");
        }
    }
}
