using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '30s';");
            migrationBuilder.CreateTable(
                name: "SubscriptionPriceChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PreviousCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PreviousCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PreviousBillingPeriod = table.Column<string>(type: "text", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BillingPeriod = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPriceChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionPriceChanges_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPriceChanges_SubscriptionId_ChangedAt_Id",
                table: "SubscriptionPriceChanges",
                columns: new[] { "SubscriptionId", "ChangedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriptionPriceChanges");
        }
    }
}
