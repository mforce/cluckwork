using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountMaxDiscountBasisPoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #727 — deliberately NO defaultValue and no backfill, unlike
            // AddWorkerSaleAllocationPolicy, whose default did double duty
            // because "no policy" was never a legal state. Here "no ceiling" IS
            // the legal default, so NULL says it directly and every existing
            // farm is unaffected until someone types a number. A default of 0
            // would mean the opposite — give nothing away.
            migrationBuilder.AddColumn<int>(
                name: "MaxDiscountBasisPoints",
                table: "Accounts",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxDiscountBasisPoints",
                table: "Accounts");
        }
    }
}
