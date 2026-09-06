using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEventSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #508 — a durable monotonic ordering key for AuditEvents.
            //
            // This is deliberately NOT what `dotnet ef migrations add`
            // scaffolded. EF emits a single statement:
            //
            //     ALTER TABLE "AuditEvents" ADD "Sequence" bigint GENERATED ALWAYS AS IDENTITY;
            //
            // which does backfill existing rows — but in PHYSICAL order. For
            // this append-only heap that happens to be insert order today, and
            // it is not a contract: a VACUUM FULL, CLUSTER or pg_repack rewrite
            // may reorder the heap and leave "Sequence" disagreeing with
            // "OccurredAtUtc" on rows nobody ever touched. Verified by probe —
            // rows stamped 00:00:02, 00:00:01, 00:00:03 came back 1, 2, 3.
            //
            // So: add nullable, backfill in TIMESTAMP order, make NOT NULL, and
            // only then attach the identity — which is why the sequence must be
            // advanced past the backfilled values by hand.
            //
            // COALESCE(..., 0) + 1 with is_called = false is load-bearing and is
            // the EMPTY-table case, which is what every Testcontainers run hits:
            // it makes the first insert into a fresh database get 1. The obvious
            // COALESCE(max, 1) would start a fresh database at 2.
            //
            // Both arguments to pg_get_serial_sequence are quoted because the
            // table and column are both mixed-case; unquoted they fold to
            // lowercase and resolve to nothing.
            //
            // This rewrites the table under ACCESS EXCLUSIVE. So does the
            // scaffolded one-liner — the identity column assigns a per-row value
            // either way — so the sort is the only added cost, and `migrate`
            // runs as a pre-deploy job with the serving process not running DDL
            // (#263).
            // ONE STATEMENT PER Sql() CALL, deliberately. Every existing
            // migrationBuilder.Sql in this repo is a single statement — there is
            // no multi-statement precedent here, and this is not the migration to
            // establish one. EF wraps the whole Up() in a single transaction, so
            // five calls are exactly as atomic as one string would have been.
            migrationBuilder.Sql("""
                ALTER TABLE "AuditEvents" ADD COLUMN "Sequence" bigint;
                """);

            migrationBuilder.Sql("""
                UPDATE "AuditEvents" AS e
                SET "Sequence" = s.rn
                FROM (
                    SELECT "Id",
                           row_number() OVER (ORDER BY "OccurredAtUtc" ASC, "Id" ASC) AS rn
                    FROM "AuditEvents"
                ) AS s
                WHERE e."Id" = s."Id";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AuditEvents" ALTER COLUMN "Sequence" SET NOT NULL;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AuditEvents" ALTER COLUMN "Sequence" ADD GENERATED ALWAYS AS IDENTITY;
                """);

            migrationBuilder.Sql("""
                SELECT setval(
                    pg_get_serial_sequence('"AuditEvents"', 'Sequence'),
                    COALESCE((SELECT max("Sequence") FROM "AuditEvents"), 0) + 1,
                    false);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "AuditEvents");
        }
    }
}
