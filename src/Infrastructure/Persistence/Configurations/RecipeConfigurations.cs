using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foodplanner.Infrastructure.Persistence.Configurations;

// Relationships are configured from the dependent only, without navigations: every principal here is a filtered entity
// (a required navigation to one raises EF warning 10622). Cascade paths: Household -> Recipe -> children is the only
// cascade. The children's own Household FK and their Ingredient/Unit FKs are NO ACTION, because SQL Server rejects
// two cascade paths to one table; NO ACTION is checked at the end of the statement, so deleting a household still
// removes recipes, their children and the household's ingredients in one go.

internal sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> recipe)
    {
        recipe.ToTable("Recipes");
        recipe.Property(r => r.Title).HasMaxLength(Recipe.MaxTitleLength).IsRequired();
        recipe.Property(r => r.Description).HasMaxLength(Recipe.MaxDescriptionLength);
        recipe.Ignore(r => r.TotalMinutes);
        recipe.HasIndex(r => new { r.HouseholdId, r.Title });
        recipe.HasOne<Household>().WithMany().HasForeignKey(r => r.HouseholdId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RecipeStepConfiguration : IEntityTypeConfiguration<RecipeStep>
{
    public void Configure(EntityTypeBuilder<RecipeStep> step)
    {
        step.ToTable("RecipeSteps");
        step.Property(s => s.Text).HasMaxLength(RecipeStep.MaxTextLength).IsRequired();
        step.HasIndex(s => new { s.RecipeId, s.Position });
        step.HasOne<Recipe>().WithMany().HasForeignKey(s => s.RecipeId).OnDelete(DeleteBehavior.Cascade);
        step.HasOne<Household>().WithMany().HasForeignKey(s => s.HouseholdId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RecipeIngredientConfiguration : IEntityTypeConfiguration<RecipeIngredient>
{
    public void Configure(EntityTypeBuilder<RecipeIngredient> line)
    {
        line.ToTable("RecipeIngredients");
        line.Property(i => i.Quantity).HasPrecision(18, 4);
        line.Property(i => i.Note).HasMaxLength(RecipeIngredient.MaxNoteLength);
        line.HasIndex(i => new { i.RecipeId, i.Position });
        line.HasIndex(i => i.IngredientId);
        line.HasIndex(i => i.UnitId);
        line.HasOne<Recipe>().WithMany().HasForeignKey(i => i.RecipeId).OnDelete(DeleteBehavior.Cascade);
        line.HasOne<Household>().WithMany().HasForeignKey(i => i.HouseholdId).OnDelete(DeleteBehavior.Restrict);

        // Ingredients go with the household; a line must not be orphaned by deleting a (global or own) ingredient.
        line.HasOne<Ingredient>().WithMany().HasForeignKey(i => i.IngredientId).OnDelete(DeleteBehavior.Restrict);
        line.HasOne<Unit>().WithMany().HasForeignKey(i => i.UnitId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}
