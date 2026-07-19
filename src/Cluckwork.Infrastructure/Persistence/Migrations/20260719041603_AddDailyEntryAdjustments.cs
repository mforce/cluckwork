using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyEntryAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DailyEntryId",
                table: "EggLots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdjustReason",
                table: "DailyEntries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdjustedFromJson",
                table: "DailyEntries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedAtUtc",
                table: "DailyEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidReason",
                table: "DailyEntries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EggLots_DailyEntryId",
                table: "EggLots",
                column: "DailyEntryId");

            // Backfill: link each existing lot to the entry whose submit
            // generated it — but only where that entry is UNAMBIGUOUS (exactly
            // one non-draft entry for the lot's flock and date). Lots whose
            // provenance can't be proven stay null, and their entries refuse
            // adjust/void (DailyEntry.PredatesLotTracking).
            migrationBuilder.Sql("""
                UPDATE "EggLots" l
                SET "DailyEntryId" = e."Id"
                FROM "DailyEntries" e
                WHERE l."AccountId" = e."AccountId"
                  AND l."FlockId" = e."FlockId"
                  AND l."ProductionDate" = e."Date"
                  AND e."Status" <> 'Draft'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "DailyEntries" other
                      WHERE other."AccountId" = l."AccountId"
                        AND other."FlockId" = l."FlockId"
                        AND other."Date" = l."ProductionDate"
                        AND other."Status" <> 'Draft'
                        AND other."Id" <> e."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EggLots_DailyEntryId",
                table: "EggLots");

            migrationBuilder.DropColumn(
                name: "DailyEntryId",
                table: "EggLots");

            migrationBuilder.DropColumn(
                name: "AdjustReason",
                table: "DailyEntries");

            migrationBuilder.DropColumn(
                name: "AdjustedFromJson",
                table: "DailyEntries");

            migrationBuilder.DropColumn(
                name: "LockedAtUtc",
                table: "DailyEntries");

            migrationBuilder.DropColumn(
                name: "VoidReason",
                table: "DailyEntries");
        }
    }
}
