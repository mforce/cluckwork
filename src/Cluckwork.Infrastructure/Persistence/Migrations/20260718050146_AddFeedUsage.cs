using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EstimatedCostMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedCostCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EstimatedCostCurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    DailyEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedUsages_Flocks_FlockId",
                        column: x => x.FlockId,
                        principalTable: "Flocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeedUsages_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedUsages_FlockId_Date",
                table: "FeedUsages",
                columns: new[] { "FlockId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedUsages_InventoryItemId_Date",
                table: "FeedUsages",
                columns: new[] { "InventoryItemId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedUsages");
        }
    }
}
