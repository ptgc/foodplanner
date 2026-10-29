using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyGroceries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsWeekly",
                table: "ShoppingListItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "WeeklyAmount",
                table: "ShoppingListItems",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WeeklyUnitId",
                table: "ShoppingListItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WeeklyGroceries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngredientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyGroceries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyGroceries_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WeeklyGroceries_Ingredients_IngredientId",
                        column: x => x.IngredientId,
                        principalTable: "Ingredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WeeklyGroceries_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItems_WeeklyUnitId",
                table: "ShoppingListItems",
                column: "WeeklyUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyGroceries_HouseholdId_IngredientId",
                table: "WeeklyGroceries",
                columns: new[] { "HouseholdId", "IngredientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyGroceries_IngredientId",
                table: "WeeklyGroceries",
                column: "IngredientId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyGroceries_UnitId",
                table: "WeeklyGroceries",
                column: "UnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_ShoppingListItems_Units_WeeklyUnitId",
                table: "ShoppingListItems",
                column: "WeeklyUnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ShoppingListItems_Units_WeeklyUnitId",
                table: "ShoppingListItems");

            migrationBuilder.DropTable(
                name: "WeeklyGroceries");

            migrationBuilder.DropIndex(
                name: "IX_ShoppingListItems_WeeklyUnitId",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "IsWeekly",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "WeeklyAmount",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "WeeklyUnitId",
                table: "ShoppingListItems");
        }
    }
}
