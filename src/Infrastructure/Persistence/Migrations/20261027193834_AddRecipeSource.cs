using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceName",
                table: "Recipes",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "Recipes",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            // Backfill (T-075): recipes saved from an import keep the import's URL (the import keeps SourceUrl after save).
            // SourceName stays NULL on purpose: the API derives the display name from the host when none is stored, so the
            // host parsing lives in one place (Recipe.DeriveSourceName) instead of being repeated in SQL. Only http(s) URLs.
            migrationBuilder.Sql("""
                UPDATE r SET r.SourceUrl = i.SourceUrl
                FROM Recipes r
                INNER JOIN RecipeImports i ON i.SavedRecipeId = r.Id AND i.HouseholdId = r.HouseholdId
                WHERE i.SourceUrl IS NOT NULL AND (i.SourceUrl LIKE 'http://%' OR i.SourceUrl LIKE 'https://%')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceName",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "Recipes");
        }
    }
}
