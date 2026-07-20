using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRoleAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserRoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: true),
                    HouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoleAssignments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignments_AccountId_UserId",
                table: "UserRoleAssignments",
                columns: new[] { "AccountId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignments_UserId_FlockId",
                table: "UserRoleAssignments",
                columns: new[] { "UserId", "FlockId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserRoleAssignments");
        }
    }
}
