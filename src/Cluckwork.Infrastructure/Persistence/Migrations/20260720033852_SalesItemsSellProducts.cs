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
            // 1) Every grade referenced by an existing sales line needs SOME
            //    product mapped to it. Auto-create one per unmapped grade,
            //    deliberately reusing the GRADE's Guid as the product id — a
            //    deterministic link that avoids a RETURNING round-trip; the id
            //    can't collide (Products never contained grade ids).
            migrationBuilder.Sql(
                """
                INSERT INTO "Products"
                    ("Id","FarmId","Name","ProductType","DefaultUnit","DefaultPriceMinorUnits",
                     "CurrencyCode","CurrencyMinorUnit","Notes","Active","Version","AccountId")
                SELECT g."Id", g."FarmId",
                       CASE WHEN EXISTS (SELECT 1 FROM "Products" p
                                         WHERE p."AccountId" = g."AccountId"
                                           AND lower(p."Name") = lower(g."Name"))
                            THEN g."Name" || '-' || left(g."Id"::text, 8)
                            ELSE g."Name" END,
                       'Egg', 'Egg', NULL,
                       a."DefaultCurrencyCode", a."DefaultCurrencyMinorUnit",
                       NULL, TRUE, 0, g."AccountId"
                FROM (SELECT DISTINCT i."AccountId", i."EggGradeId"
                      FROM "SalesOrderItems" i
                      WHERE NOT EXISTS (SELECT 1 FROM "ProductEggGradeMappings" m
                                        WHERE m."AccountId" = i."AccountId"
                                          AND m."EggGradeId" = i."EggGradeId")) x
                JOIN "EggGrades" g ON g."Id" = x."EggGradeId"
                JOIN "Accounts" a ON a."Id" = g."AccountId";
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "ProductEggGradeMappings" ("Id","ProductId","EggGradeId","AccountId")
                SELECT gen_random_uuid(), g."Id", g."Id", g."AccountId"
                FROM (SELECT DISTINCT i."AccountId", i."EggGradeId"
                      FROM "SalesOrderItems" i
                      WHERE NOT EXISTS (SELECT 1 FROM "ProductEggGradeMappings" m
                                        WHERE m."AccountId" = i."AccountId"
                                          AND m."EggGradeId" = i."EggGradeId")
                        AND EXISTS (SELECT 1 FROM "Products" p WHERE p."Id" = i."EggGradeId")) x
                JOIN "EggGrades" g ON g."Id" = x."EggGradeId";
                """);

            // 2) Existing lines were priced and counted per individual egg:
            //    Unit=Egg, factor 1, QuantityBase = Quantity. One product per
            //    grade picked deterministically when several map to it.
            migrationBuilder.Sql(
                """
                UPDATE "SalesOrderItems" i
                SET "ProductId" = m."ProductId",
                    "ProductTypeSnapshot" = 'Egg',
                    "Unit" = 'Egg',
                    "BaseUnitFactor" = 1,
                    "QuantityBase" = i."Quantity"
                FROM (SELECT DISTINCT ON ("AccountId","EggGradeId")
                             "AccountId","EggGradeId","ProductId"
                      FROM "ProductEggGradeMappings"
                      ORDER BY "AccountId","EggGradeId","Id") m
                WHERE m."AccountId" = i."AccountId"
                  AND m."EggGradeId" = i."EggGradeId";
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
