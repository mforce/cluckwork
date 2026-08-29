using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerSaleAllocationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #612 — every existing farm keeps its current (unrestricted) sale
            // behavior by defaulting to AssignedFlocksOnly, same default a new
            // farm gets from Account.Create. The DEFAULT clause both backfills
            // every existing row and covers any writer that still inserts with
            // no explicit value.
            migrationBuilder.AddColumn<string>(
                name: "WorkerSaleAllocationPolicy",
                table: "Accounts",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "AssignedFlocksOnly");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkerSaleAllocationPolicy",
                table: "Accounts");
        }
    }
}
