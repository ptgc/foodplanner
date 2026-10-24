using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foodplanner.Infrastructure.Persistence.Configurations;

// Same shape as recipes and meal plans: no navigations. Household -> ShoppingList -> Item -> (Quantity, Source) is the
// only cascade; every other foreign key is NO ACTION (SQL Server rejects two cascade paths), so a unit, an ingredient, a
// recipe or a user cannot vanish from under a list. The recipe delete endpoint removes the recipe's sources first.

internal sealed class ShoppingListConfiguration : IEntityTypeConfiguration<ShoppingList>
{
    public void Configure(EntityTypeBuilder<ShoppingList> list)
    {
        list.ToTable("ShoppingLists");
        list.HasIndex(l => new { l.HouseholdId, l.WeekStart }).IsUnique();
        list.HasOne<Household>().WithMany().HasForeignKey(l => l.HouseholdId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ShoppingListItemConfiguration : IEntityTypeConfiguration<ShoppingListItem>
{
    public void Configure(EntityTypeBuilder<ShoppingListItem> item)
    {
        item.ToTable("ShoppingListItems");
        item.Property(i => i.Section).HasConversion<string>().HasMaxLength(20).IsRequired();
        item.Property(i => i.Label).HasMaxLength(ShoppingListItem.MaxLabelLength);
        item.Ignore(i => i.IsManual);

        // Optimistic concurrency of the check-off (see docs/domain.md, T-033).
        item.Property(i => i.RowVersion).IsRowVersion();

        // One generated item per ingredient and list: a concurrent generation cannot add it twice.
        item.HasIndex(i => new { i.ShoppingListId, i.IngredientId }).IsUnique().HasFilter("[IngredientId] IS NOT NULL");
        item.HasIndex(i => i.IngredientId);
        item.HasIndex(i => i.CheckedByUserId);
        item.HasOne<ShoppingList>().WithMany().HasForeignKey(i => i.ShoppingListId).OnDelete(DeleteBehavior.Cascade);
        item.HasOne<Household>().WithMany().HasForeignKey(i => i.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        item.HasOne<Ingredient>().WithMany().HasForeignKey(i => i.IngredientId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        item.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CheckedByUserId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShoppingListItemQuantityConfiguration : IEntityTypeConfiguration<ShoppingListItemQuantity>
{
    public void Configure(EntityTypeBuilder<ShoppingListItemQuantity> quantity)
    {
        quantity.ToTable("ShoppingListItemQuantities");
        quantity.Property(q => q.Amount).HasPrecision(18, 4);
        quantity.HasIndex(q => new { q.ShoppingListItemId, q.Position });
        quantity.HasIndex(q => q.UnitId);
        quantity.HasOne<ShoppingListItem>().WithMany().HasForeignKey(q => q.ShoppingListItemId).OnDelete(DeleteBehavior.Cascade);
        quantity.HasOne<Household>().WithMany().HasForeignKey(q => q.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        quantity.HasOne<Unit>().WithMany().HasForeignKey(q => q.UnitId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShoppingListItemSourceConfiguration : IEntityTypeConfiguration<ShoppingListItemSource>
{
    public void Configure(EntityTypeBuilder<ShoppingListItemSource> source)
    {
        source.ToTable("ShoppingListItemSources");
        source.HasIndex(s => new { s.ShoppingListItemId, s.RecipeId }).IsUnique();
        source.HasIndex(s => s.RecipeId);
        source.HasOne<ShoppingListItem>().WithMany().HasForeignKey(s => s.ShoppingListItemId).OnDelete(DeleteBehavior.Cascade);
        source.HasOne<Household>().WithMany().HasForeignKey(s => s.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        source.HasOne<Recipe>().WithMany().HasForeignKey(s => s.RecipeId).OnDelete(DeleteBehavior.Restrict);
    }
}
