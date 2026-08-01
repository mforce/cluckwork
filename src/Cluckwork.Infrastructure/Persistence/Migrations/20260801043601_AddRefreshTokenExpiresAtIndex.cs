using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenExpiresAtIndex : Migration
    {
        // #270 review — built CONCURRENTLY, and therefore outside a transaction.
        // A plain CREATE INDEX takes a lock that blocks every INSERT into
        // refresh_tokens for the build's duration, and this migration exists
        // precisely because that table may already hold months of accumulated
        // rows. The migrate job (#263) runs while the PREVIOUS app version is
        // still serving, so a blocking build would stall live logins and
        // refreshes mid-deploy. CONCURRENTLY trades a slower build for not
        // taking that lock.
        //
        // Cost of leaving the transaction: a failure partway leaves an INVALID
        // index behind instead of rolling back. IF NOT EXISTS plus the explicit
        // DROP in Down keep a re-run safe, and an invalid index is inert for
        // queries — never wrong answers, just unused until rebuilt.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_refresh_tokens_ExpiresAt"
                    ON refresh_tokens ("ExpiresAt");
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_refresh_tokens_ExpiresAt";""",
                suppressTransaction: true);
        }
    }
}
