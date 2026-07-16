using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBirdMovementEntryRefAndBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DailyEntryId",
                table: "BirdMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BirdMovements_DailyEntryId",
                table: "BirdMovements",
                column: "DailyEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_BirdMovements_DailyEntries_DailyEntryId",
                table: "BirdMovements",
                column: "DailyEntryId",
                principalTable: "DailyEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Backfill: entries submitted before this feature already recorded
            // mortality, but the submit-time side effect didn't exist — without
            // these rows every pre-existing flock's CurrentBirds silently
            // forgets its historical deaths. Idempotent via the NOT EXISTS on
            // the back-reference.
            migrationBuilder.Sql(
                """
                INSERT INTO "BirdMovements"
                    ("Id", "AccountId", "FlockId", "Date", "Type", "Quantity", "Note", "DailyEntryId")
                SELECT gen_random_uuid(), e."AccountId", e."FlockId", e."Date",
                       'Mortality', e."MortalityCount", 'Daily entry mortality (backfilled)', e."Id"
                FROM "DailyEntries" e
                WHERE e."Status" <> 'Draft'
                  AND e."MortalityCount" > 0
                  AND NOT EXISTS (
                      SELECT 1 FROM "BirdMovements" m
                      WHERE m."DailyEntryId" = e."Id"
                         OR (m."FlockId" = e."FlockId" AND m."Date" = e."Date"
                             AND m."Type" = 'Mortality'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BirdMovements_DailyEntries_DailyEntryId",
                table: "BirdMovements");

            migrationBuilder.DropIndex(
                name: "IX_BirdMovements_DailyEntryId",
                table: "BirdMovements");

            migrationBuilder.DropColumn(
                name: "DailyEntryId",
                table: "BirdMovements");
        }
    }
}
