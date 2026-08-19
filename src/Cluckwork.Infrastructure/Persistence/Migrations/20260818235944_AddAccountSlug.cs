using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountSlug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #531/#407 — adding a NOT NULL, uniquely-indexed column to a table
            // that already has rows. EF's default single AddColumn(nullable:
            // false, defaultValue: "") would stamp EVERY existing account with
            // the same empty slug and then fail the unique index. Instead, do it
            // in the classic four steps: add nullable, backfill a deterministic
            // per-row slug, tighten to NOT NULL, then add the unique index.
            //
            // The default account (InitialCreate's raw-SQL literal, #283) gets
            // the documented 'default-farm' — the code an operator types on
            // upgrade day (#537 ADR). Every OTHER pre-existing row (a
            // non-production database may already carry
            // SimulationDataSeeder.SecondAccountId) gets a deterministic
            // 'farm-<first 12 hex of md5(id)>'. md5 of distinct ids differs, and
            // the UNIQUE index below is the fail-loud assertion that the
            // backfilled set has no collision. Deliberately NOT a single literal:
            // the second account shares the default's first 8 id hex, so an
            // id-prefix slug would collide.
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Accounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Accounts"
                SET "Slug" = CASE
                    WHEN "Id" = '0000000a-0000-0000-0000-000000000001' THEN 'default-farm'
                    ELSE 'farm-' || substr(md5("Id"::text), 1, 12)
                END
                WHERE "Slug" IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "Accounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Slug",
                table: "Accounts",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_Slug",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Accounts");
        }
    }
}
