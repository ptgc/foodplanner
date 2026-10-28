using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIngredientKindPurposeAlternatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AmountKind",
                table: "RecipeIngredients",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Measured");

            migrationBuilder.AddColumn<bool>(
                name: "IsOptional",
                table: "RecipeIngredients",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "RecipeIngredients",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Main");

            // Backfill (T-076): "to taste" used to be a missing quantity (and unit). It is now an explicit kind; every other
            // existing line keeps the column defaults (Measured, Main, not optional).
            migrationBuilder.Sql("UPDATE RecipeIngredients SET AmountKind = 'ToTaste' WHERE Quantity IS NULL");

            migrationBuilder.CreateTable(
                name: "RecipeIngredientAlternatives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeIngredientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    IngredientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeIngredientAlternatives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeIngredientAlternatives_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeIngredientAlternatives_Ingredients_IngredientId",
                        column: x => x.IngredientId,
                        principalTable: "Ingredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeIngredientAlternatives_RecipeIngredients_RecipeIngredientId",
                        column: x => x.RecipeIngredientId,
                        principalTable: "RecipeIngredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredientAlternatives_HouseholdId",
                table: "RecipeIngredientAlternatives",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredientAlternatives_IngredientId",
                table: "RecipeIngredientAlternatives",
                column: "IngredientId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredientAlternatives_RecipeIngredientId_Position",
                table: "RecipeIngredientAlternatives",
                columns: new[] { "RecipeIngredientId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeIngredientAlternatives");

            migrationBuilder.DropColumn(
                name: "AmountKind",
                table: "RecipeIngredients");

            migrationBuilder.DropColumn(
                name: "IsOptional",
                table: "RecipeIngredients");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "RecipeIngredients");
        }
    }
}
