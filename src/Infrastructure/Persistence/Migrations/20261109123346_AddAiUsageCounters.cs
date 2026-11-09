using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiGlobalUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MonthStart = table.Column<DateOnly>(type: "date", nullable: false),
                    Calls = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiGlobalUsages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiHouseholdUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    DayCalls = table.Column<int>(type: "int", nullable: false),
                    MonthStart = table.Column<DateOnly>(type: "date", nullable: false),
                    MonthCalls = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiHouseholdUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiHouseholdUsages_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiGlobalUsages_MonthStart",
                table: "AiGlobalUsages",
                column: "MonthStart",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiHouseholdUsages_HouseholdId",
                table: "AiHouseholdUsages",
                column: "HouseholdId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiGlobalUsages");

            migrationBuilder.DropTable(
                name: "AiHouseholdUsages");
        }
    }
}
