using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionDetailsAndTrials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '30s';");
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Subscriptions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderUrl",
                table: "Subscriptions",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateOnly>(
                name: "TrialEndsOn",
                table: "Subscriptions",
                type: "date",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Subscriptions_TrialBillingDate",
                table: "Subscriptions",
                sql: "\"TrialEndsOn\" IS NULL OR \"NextBillingDate\" >= \"TrialEndsOn\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Subscriptions_TrialBillingDate",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ProviderUrl",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialEndsOn",
                table: "Subscriptions");
        }
    }
}
