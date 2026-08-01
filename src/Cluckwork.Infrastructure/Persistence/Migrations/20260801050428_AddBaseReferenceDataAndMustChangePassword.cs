using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBaseReferenceDataAndMustChangePassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.InsertData(
                table: "Accounts",
                columns: new[] { "Id", "AccountId", "Brand", "DateFormatOverride", "DefaultCurrencyCode", "DefaultCurrencyMinorUnit", "DefaultCurrencySymbol", "FirstDayOfWeek", "IsActive", "Locale", "Name", "TimeFormatOverride", "TimeZoneId", "UnitSystem", "Version" },
                values: new object[] { new Guid("0000000a-0000-0000-0000-000000000001"), new Guid("0000000a-0000-0000-0000-000000000001"), "aubergine", null, "USD", 2, "$", null, true, "en-US", "Default Farm", null, "UTC", "Metric", 0 });

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("0000000c-0000-0000-0000-000000000001"), "0000000c-0000-0000-0000-000000000001", "Admin", "ADMIN" },
                    { new Guid("0000000c-0000-0000-0000-000000000002"), "0000000c-0000-0000-0000-000000000002", "Manager", "MANAGER" },
                    { new Guid("0000000c-0000-0000-0000-000000000003"), "0000000c-0000-0000-0000-000000000003", "Sales", "SALES" },
                    { new Guid("0000000c-0000-0000-0000-000000000004"), "0000000c-0000-0000-0000-000000000004", "ReadOnly", "READONLY" }
                });

            migrationBuilder.InsertData(
                table: "EggGrades",
                columns: new[] { "Id", "AccountId", "Active", "FarmId", "GradeType", "IsSaleable", "Name", "SortOrder", "Version" },
                values: new object[,]
                {
                    { new Guid("0000000e-0000-0000-0000-000000000001"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Size", true, "Small", 0, 0 },
                    { new Guid("0000000e-0000-0000-0000-000000000002"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Size", true, "Medium", 1, 0 },
                    { new Guid("0000000e-0000-0000-0000-000000000003"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Size", true, "Large", 2, 0 },
                    { new Guid("0000000e-0000-0000-0000-000000000004"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Size", true, "Jumbo", 3, 0 },
                    { new Guid("0000000e-0000-0000-0000-000000000005"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Quality", true, "Seconds", 4, 0 },
                    { new Guid("0000000e-0000-0000-0000-000000000006"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Quality", false, "Cracked", 5, 0 },
                    { new Guid("0000000e-0000-0000-0000-000000000007"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Quality", false, "Dirty", 6, 0 },
                    { new Guid("0000000e-0000-0000-0000-000000000008"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Quality", false, "Soft Shell", 7, 0 },
                    { new Guid("0000000e-0000-0000-0000-000000000009"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Custom", false, "Discarded", 8, 0 },
                    { new Guid("0000000e-0000-0000-0000-000000000010"), new Guid("0000000a-0000-0000-0000-000000000001"), true, new Guid("0000000f-0000-0000-0000-000000000001"), "Custom", false, "Internal Use", 9, 0 }
                });

            migrationBuilder.InsertData(
                table: "EggUnitConversions",
                columns: new[] { "Id", "AccountId", "Active", "EggsPerUnit", "UnitCode", "Version" },
                values: new object[,]
                {
                    { new Guid("00000010-0000-0000-0000-000000000001"), new Guid("0000000a-0000-0000-0000-000000000001"), true, 1, "Individual", 0 },
                    { new Guid("00000010-0000-0000-0000-000000000002"), new Guid("0000000a-0000-0000-0000-000000000001"), true, 12, "Dozen", 0 },
                    { new Guid("00000010-0000-0000-0000-000000000003"), new Guid("0000000a-0000-0000-0000-000000000001"), true, 30, "Flat", 0 },
                    { new Guid("00000010-0000-0000-0000-000000000004"), new Guid("0000000a-0000-0000-0000-000000000001"), true, 30, "Tray", 0 },
                    { new Guid("00000010-0000-0000-0000-000000000005"), new Guid("0000000a-0000-0000-0000-000000000001"), true, 12, "Carton", 0 },
                    { new Guid("00000010-0000-0000-0000-000000000006"), new Guid("0000000a-0000-0000-0000-000000000001"), true, 360, "Case", 0 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Accounts",
                keyColumn: "Id",
                keyValue: new Guid("0000000a-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("0000000c-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("0000000c-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("0000000c-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("0000000c-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "EggGrades",
                keyColumn: "Id",
                keyValue: new Guid("0000000e-0000-0000-0000-000000000010"));

            migrationBuilder.DeleteData(
                table: "EggUnitConversions",
                keyColumn: "Id",
                keyValue: new Guid("00000010-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "EggUnitConversions",
                keyColumn: "Id",
                keyValue: new Guid("00000010-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "EggUnitConversions",
                keyColumn: "Id",
                keyValue: new Guid("00000010-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "EggUnitConversions",
                keyColumn: "Id",
                keyValue: new Guid("00000010-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "EggUnitConversions",
                keyColumn: "Id",
                keyValue: new Guid("00000010-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "EggUnitConversions",
                keyColumn: "Id",
                keyValue: new Guid("00000010-0000-0000-0000-000000000006"));

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "AspNetUsers");
        }
    }
}
