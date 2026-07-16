using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEggGradesAndDailyEntryGrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EggGrades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GradeType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsSaleable = table.Column<bool>(type: "boolean", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EggGrades", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyEntryGrades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DailyEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EggGradeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyEntryGrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyEntryGrades_DailyEntries_DailyEntryId",
                        column: x => x.DailyEntryId,
                        principalTable: "DailyEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DailyEntryGrades_EggGrades_EggGradeId",
                        column: x => x.EggGradeId,
                        principalTable: "EggGrades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyEntryGrades_DailyEntryId_EggGradeId",
                table: "DailyEntryGrades",
                columns: new[] { "DailyEntryId", "EggGradeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyEntryGrades_EggGradeId",
                table: "DailyEntryGrades",
                column: "EggGradeId");

            migrationBuilder.CreateIndex(
                name: "IX_EggGrades_AccountId_FarmId_Name",
                table: "EggGrades",
                columns: new[] { "AccountId", "FarmId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyEntryGrades");

            migrationBuilder.DropTable(
                name: "EggGrades");
        }
    }
}
