using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesOrderItemListPriceBasis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #720 — the default backfills every EXISTING row to PreDating (the
            // one value the application never writes), then is dropped so no
            // FUTURE insert can silently inherit a basis: a row the code writes
            // always states its own.
            migrationBuilder.AddColumn<string>(
                name: "ListPriceBasis",
                table: "SalesOrderItems",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "PreDating");

            migrationBuilder.Sql(
                "ALTER TABLE \"SalesOrderItems\" ALTER COLUMN \"ListPriceBasis\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ListPriceBasis",
                table: "SalesOrderItems");
        }
    }
}
