using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountScopedIdentityIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                columns: new[] { "AccountId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                columns: new[] { "AccountId", "NormalizedUserName" },
                unique: true);

            // #532 review — an orphan check BEFORE the foreign key, because
            // there was no FK on AspNetUsers.AccountId until now: a row may
            // reference an Accounts row that never existed or was removed.
            // Without this the deploy fails on a bare Postgres 23503 naming no
            // row, during `dotnet Cluckwork.Api.dll migrate` in a pre-deploy job
            // (#263), which is the worst place to be handed an opaque error.
            migrationBuilder.Sql("""
                DO $$
                DECLARE orphans bigint;
                BEGIN
                    SELECT count(*) INTO orphans
                    FROM "AspNetUsers" u
                    LEFT JOIN "Accounts" a ON a."Id" = u."AccountId"
                    WHERE a."Id" IS NULL;

                    IF orphans > 0 THEN
                        RAISE EXCEPTION
                            'Cannot add FK_AspNetUsers_Accounts_AccountId: % user row(s) reference an account that does not exist. Resolve them first; this migration will not delete user rows.',
                            orphans;
                    END IF;
                END $$;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Accounts_AccountId",
                table: "AspNetUsers",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // #532 review — THIS ROLLBACK IS ONE-WAY IN PRACTICE. It recreates
            // the GLOBALLY unique UserNameIndex, which cannot exist once two
            // farms share a username — i.e. from the moment the feature this
            // migration enables is actually used. Rolling back after that point
            // fails on a duplicate-key error, and the fix is to remove the
            // second farm's users first. Recorded here rather than discovered
            // during an incident.
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Accounts_AccountId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);
        }
    }
}
