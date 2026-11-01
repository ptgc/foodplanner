using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMealMixRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MealMixRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FoodType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MinPerWeek = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MealMixRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MealMixRules_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MealMixRules_HouseholdId_FoodType",
                table: "MealMixRules",
                columns: new[] { "HouseholdId", "FoodType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MealMixRules");
        }
    }
}
