using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EggUnitConversions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EggsPerUnit = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EggUnitConversions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProductType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DefaultUnit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DefaultPriceMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductEggGradeMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    EggGradeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductEggGradeMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductEggGradeMappings_EggGrades_EggGradeId",
                        column: x => x.EggGradeId,
                        principalTable: "EggGrades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductEggGradeMappings_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EggUnitConversions_AccountId_UnitCode",
                table: "EggUnitConversions",
                columns: new[] { "AccountId", "UnitCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductEggGradeMappings_EggGradeId",
                table: "ProductEggGradeMappings",
                column: "EggGradeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductEggGradeMappings_ProductId",
                table: "ProductEggGradeMappings",
                column: "ProductId",
                unique: true);

            // Case-insensitive per-account name uniqueness. Expression indexes
            // aren't representable in the EF model, so this lives as raw SQL
            // (same pattern as IX_EggGrades_AccountId_FarmId_LowerName).
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_Products_AccountId_LowerName"
                    ON "Products" ("AccountId", lower("Name"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EggUnitConversions");

            migrationBuilder.DropTable(
                name: "ProductEggGradeMappings");

            migrationBuilder.DropTable(
                name: "Products");
        }
    }
}
