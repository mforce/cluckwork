using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CredentialEpoch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADD COLUMN takes ACCESS EXCLUSIVE. Do not let a queued schema lock
            // block old-fleet login/refresh traffic indefinitely during Deploy A.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AddColumn<int>(
                name: "CredentialEpoch",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisabledAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DisabledBy",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IssuedEpoch",
                table: "refresh_tokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The application has not been deployed, so the user-approved cutover
            // may force one re-login. The epoch comparison remains the actual
            // boundary; this is defense in depth for legacy rows.
            migrationBuilder.Sql("""
                UPDATE refresh_tokens
                SET "RevokedAt" = now()
                WHERE "RevokedAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CredentialEpoch", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "DisabledAt", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "DisabledBy", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "IssuedEpoch", table: "refresh_tokens");
        }
    }
}
