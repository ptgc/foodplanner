using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HouseholdPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    PhotoImportsPerDay = table.Column<int>(type: "int", nullable: true),
                    LineHelpPerDay = table.Column<int>(type: "int", nullable: true),
                    LineHelpPerMonth = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdPlans", x => x.Id);
                    table.CheckConstraint("CK_HouseholdPlans_LineHelpPerDay", "[LineHelpPerDay] IS NULL OR ([LineHelpPerDay] >= 0 AND [LineHelpPerDay] <= 10000)");
                    table.CheckConstraint("CK_HouseholdPlans_LineHelpPerMonth", "[LineHelpPerMonth] IS NULL OR ([LineHelpPerMonth] >= 0 AND [LineHelpPerMonth] <= 10000)");
                    table.CheckConstraint("CK_HouseholdPlans_PhotoImportsPerDay", "[PhotoImportsPerDay] IS NULL OR ([PhotoImportsPerDay] >= 0 AND [PhotoImportsPerDay] <= 10000)");
                    table.ForeignKey(
                        name: "FK_HouseholdPlans_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdPlans_HouseholdId",
                table: "HouseholdPlans",
                column: "HouseholdId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HouseholdPlans");
        }
    }
}
