using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConvertLotsAndSalesToEggGradeIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EggLots_Allocation",
                table: "EggLots");

            migrationBuilder.DropColumn(
                name: "GradeCode",
                table: "SalesOrderItems");

            migrationBuilder.DropColumn(
                name: "GradeCode",
                table: "EggLots");

            migrationBuilder.AddColumn<Guid>(
                name: "EggGradeId",
                table: "SalesOrderItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EggGradeId",
                table: "EggLots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_EggLots_Allocation",
                table: "EggLots",
                columns: new[] { "AccountId", "EggGradeId", "ProductionDate", "QuantityAvailable" });

            migrationBuilder.CreateIndex(
                name: "IX_EggLots_EggGradeId",
                table: "EggLots",
                column: "EggGradeId");

            migrationBuilder.AddForeignKey(
                name: "FK_EggLots_EggGrades_EggGradeId",
                table: "EggLots",
                column: "EggGradeId",
                principalTable: "EggGrades",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EggLots_EggGrades_EggGradeId",
                table: "EggLots");

            migrationBuilder.DropIndex(
                name: "IX_EggLots_Allocation",
                table: "EggLots");

            migrationBuilder.DropIndex(
                name: "IX_EggLots_EggGradeId",
                table: "EggLots");

            migrationBuilder.DropColumn(
                name: "EggGradeId",
                table: "SalesOrderItems");

            migrationBuilder.DropColumn(
                name: "EggGradeId",
                table: "EggLots");

            migrationBuilder.AddColumn<string>(
                name: "GradeCode",
                table: "SalesOrderItems",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "GradeCode",
                table: "EggLots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_EggLots_Allocation",
                table: "EggLots",
                columns: new[] { "AccountId", "GradeCode", "ProductionDate", "QuantityAvailable" });
        }
    }
}
