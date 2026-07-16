using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PRE-RELEASE: existing orders reference customer ids that predate the
            // Customers table; the new FK would fail validation against them. No
            // production data — purge legacy orders (items cascade) instead of
            // backfilling placeholder customers.
            migrationBuilder.Sql("""DELETE FROM "SalesOrderItems";""");
            migrationBuilder.Sql("""DELETE FROM "SalesOrders";""");

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_AccountId_ReferenceNumber",
                table: "SalesOrders",
                columns: new[] { "AccountId", "ReferenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CustomerId",
                table: "SalesOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AccountId_Name",
                table: "Customers",
                columns: new[] { "AccountId", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_SalesOrders_Customers_CustomerId",
                table: "SalesOrders",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesOrders_Customers_CustomerId",
                table: "SalesOrders");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_SalesOrders_AccountId_ReferenceNumber",
                table: "SalesOrders");

            migrationBuilder.DropIndex(
                name: "IX_SalesOrders_CustomerId",
                table: "SalesOrders");
        }
    }
}
