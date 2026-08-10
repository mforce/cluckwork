using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmBannerColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_farm_logos_content_length",
                table: "FarmLogos");

            migrationBuilder.AlterColumn<int>(
                name: "Width",
                table: "FarmLogos",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "FarmLogos",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<int>(
                name: "Height",
                table: "FarmLogos",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "ContentType",
                table: "FarmLogos",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "ContentHash",
                table: "FarmLogos",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<byte[]>(
                name: "Content",
                table: "FarmLogos",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.AlterColumn<int>(
                name: "ByteLength",
                table: "FarmLogos",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "BannerByteLength",
                table: "FarmLogos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "BannerContent",
                table: "FarmLogos",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BannerContentHash",
                table: "FarmLogos",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BannerContentType",
                table: "FarmLogos",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BannerHeight",
                table: "FarmLogos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BannerUpdatedAt",
                table: "FarmLogos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BannerWidth",
                table: "FarmLogos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_farm_logos_banner_content_length",
                table: "FarmLogos",
                sql: "\"BannerContent\" IS NULL OR (octet_length(\"BannerContent\") > 0 AND octet_length(\"BannerContent\") <= 15728640)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_farm_logos_content_length",
                table: "FarmLogos",
                sql: "\"Content\" IS NULL OR (octet_length(\"Content\") > 0 AND octet_length(\"Content\") <= 5242880)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Codex review of #496: the pre-banner schema required every FarmLogos
            // row to carry a real (non-null, non-empty) logo. A row this migration
            // allowed — banner-only, Content NULL — has no valid pre-migration
            // state to roll back to. Left in place, the NOT NULL backfill below
            // would set Content to an empty bytea, which immediately violates the
            // restored ck_farm_logos_content_length ("> 0") check. Delete such rows
            // before restoring the old constraints rather than fail the rollback.
            migrationBuilder.Sql("DELETE FROM \"FarmLogos\" WHERE \"Content\" IS NULL;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_farm_logos_banner_content_length",
                table: "FarmLogos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_farm_logos_content_length",
                table: "FarmLogos");

            migrationBuilder.DropColumn(
                name: "BannerByteLength",
                table: "FarmLogos");

            migrationBuilder.DropColumn(
                name: "BannerContent",
                table: "FarmLogos");

            migrationBuilder.DropColumn(
                name: "BannerContentHash",
                table: "FarmLogos");

            migrationBuilder.DropColumn(
                name: "BannerContentType",
                table: "FarmLogos");

            migrationBuilder.DropColumn(
                name: "BannerHeight",
                table: "FarmLogos");

            migrationBuilder.DropColumn(
                name: "BannerUpdatedAt",
                table: "FarmLogos");

            migrationBuilder.DropColumn(
                name: "BannerWidth",
                table: "FarmLogos");

            migrationBuilder.AlterColumn<int>(
                name: "Width",
                table: "FarmLogos",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "FarmLogos",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Height",
                table: "FarmLogos",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ContentType",
                table: "FarmLogos",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ContentHash",
                table: "FarmLogos",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "Content",
                table: "FarmLogos",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ByteLength",
                table: "FarmLogos",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_farm_logos_content_length",
                table: "FarmLogos",
                sql: "octet_length(\"Content\") > 0 AND octet_length(\"Content\") <= 5242880");
        }
    }
}
