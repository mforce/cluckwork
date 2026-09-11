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
            //
            // The CHECK below is what makes Account.MaxDiscount's throw
            // unreachable: that getter runs on the role-agnostic GET /account,
            // so one out-of-range row would 500 every page load on the farm,
            // Settings included. See AccountConfiguration for the full reasoning.
            migrationBuilder.AddColumn<int>(
                name: "MaxDiscountBasisPoints",
                table: "Accounts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_MaxDiscountBasisPoints",
                table: "Accounts",
                sql: "\"MaxDiscountBasisPoints\" IS NULL OR \"MaxDiscountBasisPoints\" BETWEEN 0 AND 10000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_MaxDiscountBasisPoints",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MaxDiscountBasisPoints",
                table: "Accounts");
        }
    }
}
