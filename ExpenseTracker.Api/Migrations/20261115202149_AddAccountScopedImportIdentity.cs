using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountScopedImportIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '30s';");
            migrationBuilder.AddColumn<Guid>(
                name: "ImportSourceId",
                table: "Subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_UserId_ImportSourceId",
                table: "Subscriptions",
                columns: new[] { "UserId", "ImportSourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_UserId_ImportSourceId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ImportSourceId",
                table: "Subscriptions");
        }
    }
}
