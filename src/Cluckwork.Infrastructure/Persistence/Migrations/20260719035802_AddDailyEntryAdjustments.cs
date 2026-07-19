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
            migrationBuilder.AddColumn<string>(
                name: "AdjustReason",
                table: "DailyEntries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdjustedFromJson",
                table: "DailyEntries",
                type: "jsonb",
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
