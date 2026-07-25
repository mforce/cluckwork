using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FarmBrand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing farms predate palettes and are on aubergine by
            // definition — the default carries no data-brand attribute, so this
            // is also exactly what they were already rendering (#149).
            migrationBuilder.AddColumn<string>(
                name: "Brand",
                table: "Accounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "aubergine");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Brand",
                table: "Accounts");
        }
    }
}
