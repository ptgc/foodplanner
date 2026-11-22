using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupportAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdminEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetHouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetHouseholdName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ReasonCategory = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ReasonNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupportSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetHouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ReasonCategory = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReasonNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedReason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupportAuditEntries_At",
                table: "SupportAuditEntries",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_SupportAuditEntries_TargetHouseholdId_At",
                table: "SupportAuditEntries",
                columns: new[] { "TargetHouseholdId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportSessions_TargetHouseholdId",
                table: "SupportSessions",
                column: "TargetHouseholdId");

            migrationBuilder.CreateIndex(
                name: "UX_SupportSessions_AdminUserId_Active",
                table: "SupportSessions",
                column: "AdminUserId",
                unique: true,
                filter: "[EndedAt] IS NULL");

            // T-096: the audit log is append-only for EVERY login, the application's included (the save interceptor refuses it too).
            // THROW rolls the statement and its transaction back. Dropping the table drops the trigger (see Down).
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [TR_SupportAuditEntries_AppendOnly] ON [SupportAuditEntries]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    THROW 51096, 'SupportAuditEntries is append-only: rows cannot be updated or deleted.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupportAuditEntries");

            migrationBuilder.DropTable(
                name: "SupportSessions");
        }
    }
}
