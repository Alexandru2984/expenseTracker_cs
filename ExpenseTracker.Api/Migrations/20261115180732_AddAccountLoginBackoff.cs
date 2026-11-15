using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountLoginBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '30s';");
            // Same expression as authentication lookups. Existing case collisions
            // must abort the restore drill, never be merged or renamed automatically.
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_Users_Username_CaseInsensitive\" ON \"Users\" (lower(\"Username\"));");
            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginFailureAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LoginBlockedUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LoginFailureCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX \"IX_Users_Username_CaseInsensitive\";");
            migrationBuilder.DropColumn(
                name: "LastLoginFailureAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LoginBlockedUntil",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LoginFailureCount",
                table: "Users");
        }
    }
}
