using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '30s';");
            migrationBuilder.CreateTable(
                name: "NotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PreferenceVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutbox", x => x.Id);
                    table.CheckConstraint("CK_NotificationOutbox_Attempts", "\"Attempts\" BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_NotificationOutbox_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SendHour = table.Column<int>(type: "integer", nullable: false),
                    LeadDays = table.Column<int>(type: "integer", nullable: false),
                    Renewals = table.Column<bool>(type: "boolean", nullable: false),
                    Trials = table.Column<bool>(type: "boolean", nullable: false),
                    GoalDeadlines = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeNames = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsentRecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextDigestAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.UserId);
                    table.CheckConstraint("CK_NotificationPreferences_Ranges", "\"SendHour\" BETWEEN 0 AND 23 AND \"LeadDays\" BETWEEN 0 AND 14");
                    table.ForeignKey(
                        name: "FK_NotificationPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_CreatedAt",
                table: "NotificationOutbox",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_State_LeaseExpiresAtUtc",
                table: "NotificationOutbox",
                columns: new[] { "State", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_State_NextAttemptAtUtc",
                table: "NotificationOutbox",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_UserId_LocalDate",
                table: "NotificationOutbox",
                columns: new[] { "UserId", "LocalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_Enabled_NextDigestAtUtc",
                table: "NotificationPreferences",
                columns: new[] { "Enabled", "NextDigestAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationOutbox");

            migrationBuilder.DropTable(
                name: "NotificationPreferences");
        }
    }
}
