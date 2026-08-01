using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AtomicIdempotencyClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "StatusCode",
                table: "idempotency_records",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "ResponseBody",
                table: "idempotency_records",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "idempotency_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "idempotency_records",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseOwner",
                table: "idempotency_records",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "idempotency_records",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "idempotency_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // #307 review — every row that already existed before this migration was
            // written under the OLD schema, where StatusCode/ResponseBody were NOT
            // NULL: by definition each one is a completed cached response, never an
            // in-progress claim. Left alone, the column defaults above would leave
            // every pre-existing row Status=InProgress (0) with an empty RequestHash
            // and a MinValue lease — a retry against that key could neither steal
            // (RequestHash "" never matches a real caller's hash, so the steal WHERE
            // never matches either) nor replay (Status isn't Completed): it 409s
            // forever. Backfill flips every row whose ResponseBody is still non-null
            // (i.e. every row this migration did NOT just insert — nothing has, since
            // migrations run before the serving process starts) to Completed, using
            // its own CreatedAt as a best-effort CompletedAt. RequestHash stays "" on
            // these rows on purpose: IdempotencyMiddleware treats an empty
            // RequestHash as "hash unknown (pre-#307 row)" and skips the mismatch
            // check for it, rather than trying to reconstruct a hash nothing recorded
            // (no real request hash is ever the empty string, so "" is an
            // unambiguous, collision-free sentinel — see IdempotencyMiddleware's
            // TryClaimOrInspectAsync). This whole shim is retired by #245's
            // InitialCreate squash, which removes every pre-#307 row's reason to
            // exist. Never delete the rows here: a caller with an in-flight retry
            // against a deleted claim would treat the key as free and re-execute the
            // original write, which is the exact double-execution #307 exists to
            // prevent.
            migrationBuilder.Sql("""
                UPDATE idempotency_records
                SET "Status" = 1, "CompletedAt" = "CreatedAt"
                WHERE "ResponseBody" IS NOT NULL AND "Status" = 0
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "idempotency_records");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "idempotency_records");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "idempotency_records");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "idempotency_records");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "idempotency_records");

            migrationBuilder.AlterColumn<int>(
                name: "StatusCode",
                table: "idempotency_records",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ResponseBody",
                table: "idempotency_records",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
