using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FarmLocalizationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TimeZoneId",
                table: "Accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Accounts",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "DefaultCurrencyCode",
                table: "Accounts",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "DateFormatOverride",
                table: "Accounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultCurrencySymbol",
                table: "Accounts",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstDayOfWeek",
                table: "Accounts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Scaffolded as "" — backfilled here instead. An empty locale or
            // unit system would be a farm the settings screen cannot render and
            // the domain's own required-field guard would reject on first save.
            migrationBuilder.AddColumn<string>(
                name: "Locale",
                table: "Accounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "en-US");

            migrationBuilder.AddColumn<string>(
                name: "TimeFormatOverride",
                table: "Accounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitSystem",
                table: "Accounts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Metric");

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // DefaultCurrencySymbol is deliberately left NULL on existing rows:
            // it is derived, not authored, and Account.CurrencySymbol resolves
            // it through the §4.6 catalog until the next currency change stores
            // one. The currency CODE and minor unit are never touched here —
            // §4.6 forbids reinterpreting money already stored.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DateFormatOverride",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "DefaultCurrencySymbol",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FirstDayOfWeek",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Locale",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "TimeFormatOverride",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "UnitSystem",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Accounts");

            migrationBuilder.AlterColumn<string>(
                name: "TimeZoneId",
                table: "Accounts",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Accounts",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120);

            migrationBuilder.AlterColumn<string>(
                name: "DefaultCurrencyCode",
                table: "Accounts",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3);
        }
    }
}
