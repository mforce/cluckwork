using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddListChronologySequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #819 — one database-generated identity per table. Values are
            // global within that table (not per account) and are used only as
            // an insertion-order tiebreak after the list's date column.
            //
            // Existing same-day write order is unrecoverable: those rows carry
            // only a date and a random v4 Guid. PostgreSQL assigns identities
            // while adding each column, leaving legacy ties deterministic but
            // not claiming chronology that the stored data cannot establish.
            // Each ALTER rewrites and locks its table; migrations run in the
            // pre-deploy job rather than in the serving process (#263).
            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "SalesOrders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "Expenses",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "EggLots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "DailyEntries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "BirdMovements",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "EggLots");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "DailyEntries");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "BirdMovements");
        }
    }
}
