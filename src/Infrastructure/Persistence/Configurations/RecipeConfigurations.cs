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
        recipe.Property(r => r.SourceUrl).HasMaxLength(Recipe.MaxSourceUrlLength);
        recipe.Property(r => r.SourceName).HasMaxLength(Recipe.MaxSourceNameLength);
        recipe.Ignore(r => r.TotalMinutes);
        recipe.Ignore(r => r.SourceDisplayName);
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

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> tag)
    {
        tag.ToTable("Tags");
        tag.Property(t => t.Name).HasMaxLength(Tag.MaxNameLength).IsRequired();
        tag.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Unique per household and among the global rows (see the Ingredient index); a household cannot shadow a global
        // name, which the endpoint refuses (409).
        tag.HasIndex(t => new { t.HouseholdId, t.Name }).IsUnique().HasFilter(null);
        tag.HasOne<Household>().WithMany().HasForeignKey(t => t.HouseholdId).IsRequired(false).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RecipeTagConfiguration : IEntityTypeConfiguration<RecipeTag>
{
    public void Configure(EntityTypeBuilder<RecipeTag> link)
    {
        link.ToTable("RecipeTags");
        link.HasIndex(l => new { l.RecipeId, l.TagId }).IsUnique();
        link.HasIndex(l => l.TagId);
        link.HasOne<Recipe>().WithMany().HasForeignKey(l => l.RecipeId).OnDelete(DeleteBehavior.Cascade);
        link.HasOne<Household>().WithMany().HasForeignKey(l => l.HouseholdId).OnDelete(DeleteBehavior.Restrict);

        // A tag in use cannot be deleted from under a recipe (and the household's tags go with the household).
        link.HasOne<Tag>().WithMany().HasForeignKey(l => l.TagId).OnDelete(DeleteBehavior.Restrict);
    }
}
