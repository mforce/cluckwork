using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesItemsSellProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaseUnitFactor",
                table: "SalesOrderItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                table: "SalesOrderItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "ProductTypeSnapshot",
                table: "SalesOrderItems",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "QuantityBase",
                table: "SalesOrderItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "SalesOrderItems",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");


            // ---- Backfill (frozen history, no money changes) ----
            // Historical raw-grade lines are attributed to a DEDICATED
            // backfill product per grade — never to a user-created product
            // that happens to map to the same grade (it may never have sold
            // those eggs). The grade's own Guid is reused as the product id:
            // deterministic linkage, no RETURNING round-trip, idempotent on
            // re-run. Name collisions (an existing product with the grade's
            // name, or two same-named grades from different farms in this
            // very insert) fall back to a full-grade-Guid suffix, which is
            // unique among siblings and collides with an existing product
            // only if someone literally named one "<grade>-<that guid>".
            // Grade names cap at 50, so 50 + 1 + 36 fits the 100-char column.
            migrationBuilder.Sql(
                """
                INSERT INTO "Products"
                    ("Id","FarmId","Name","ProductType","DefaultUnit","DefaultPriceMinorUnits",
                     "CurrencyCode","CurrencyMinorUnit","Notes","Active","Version","AccountId")
                SELECT x."Id", x."FarmId",
                       CASE WHEN x."NameTaken" OR x."SiblingDup"
                            THEN x."Name" || '-' || x."Id"::text
                            ELSE x."Name" END,
                       'Egg', 'Egg', NULL,
                       x."DefaultCurrencyCode", x."DefaultCurrencyMinorUnit",
                       NULL, TRUE, 0, x."AccountId"
                FROM (
                    SELECT g."Id", g."FarmId", g."Name", g."AccountId",
                           a."DefaultCurrencyCode", a."DefaultCurrencyMinorUnit",
                           EXISTS (SELECT 1 FROM "Products" p
                                   WHERE p."AccountId" = g."AccountId"
                                     AND lower(p."Name") = lower(g."Name")) AS "NameTaken",
                           count(*) OVER (PARTITION BY g."AccountId", lower(g."Name")) > 1 AS "SiblingDup"
                    FROM (SELECT DISTINCT i."AccountId", i."EggGradeId"
                          FROM "SalesOrderItems" i) refs
                    JOIN "EggGrades" g ON g."Id" = refs."EggGradeId"
                    JOIN "Accounts" a ON a."Id" = g."AccountId"
                    WHERE NOT EXISTS (SELECT 1 FROM "Products" p WHERE p."Id" = g."Id")
                ) x;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "ProductEggGradeMappings" ("Id","ProductId","EggGradeId","AccountId")
                SELECT gen_random_uuid(), p."Id", p."Id", p."AccountId"
                FROM "Products" p
                WHERE EXISTS (SELECT 1 FROM "EggGrades" g WHERE g."Id" = p."Id")
                  AND NOT EXISTS (SELECT 1 FROM "ProductEggGradeMappings" m
                                  WHERE m."ProductId" = p."Id");
                """);

            // Existing lines were priced and counted per individual egg. The
            // product id IS the grade id (step 1), so the join is exact — no
            // arbitrary pick among user mappings. The zero-Guid filter makes
            // a re-run a no-op.
            migrationBuilder.Sql(
                """
                UPDATE "SalesOrderItems" i
                SET "ProductId" = i."EggGradeId",
                    "ProductTypeSnapshot" = 'Egg',
                    "Unit" = 'Egg',
                    "BaseUnitFactor" = 1,
                    "QuantityBase" = i."Quantity"
                WHERE i."ProductId" = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderItems_ProductId",
                table: "SalesOrderItems",
                column: "ProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesOrderItems_Products_ProductId",
                table: "SalesOrderItems",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesOrderItems_Products_ProductId",
                table: "SalesOrderItems");

            migrationBuilder.DropIndex(
                name: "IX_SalesOrderItems_ProductId",
                table: "SalesOrderItems");

            migrationBuilder.DropColumn(
                name: "BaseUnitFactor",
                table: "SalesOrderItems");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "SalesOrderItems");

            migrationBuilder.DropColumn(
                name: "ProductTypeSnapshot",
                table: "SalesOrderItems");

            migrationBuilder.DropColumn(
                name: "QuantityBase",
                table: "SalesOrderItems");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "SalesOrderItems");
        }
    }
}
