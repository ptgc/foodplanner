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

        // The weekly groceries' part of the item (T-092): amount and unit (NO ACTION, global catalog data), both null = no amount.
        item.Property(i => i.WeeklyAmount).HasPrecision(18, 4);
        item.HasIndex(i => i.WeeklyUnitId);
        item.HasOne<Unit>().WithMany().HasForeignKey(i => i.WeeklyUnitId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);

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

// Pantry (T-037): one row per household and ingredient. Ingredient and user are NO ACTION (global identity / catalog data).
internal sealed class PantryItemConfiguration : IEntityTypeConfiguration<PantryItem>
{
    public void Configure(EntityTypeBuilder<PantryItem> pantry)
    {
        pantry.ToTable("PantryItems");
        pantry.HasIndex(p => new { p.HouseholdId, p.IngredientId }).IsUnique();
        pantry.HasIndex(p => p.IngredientId);
        pantry.HasIndex(p => p.ConfirmedByUserId);
        pantry.HasOne<Household>().WithMany().HasForeignKey(p => p.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        pantry.HasOne<Ingredient>().WithMany().HasForeignKey(p => p.IngredientId).OnDelete(DeleteBehavior.Restrict);
        pantry.HasOne<AppUser>().WithMany().HasForeignKey(p => p.ConfirmedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

// Weekly groceries (T-092): one row per household and ingredient. Unit and ingredient are NO ACTION (global catalog data).
internal sealed class WeeklyGroceryConfiguration : IEntityTypeConfiguration<WeeklyGrocery>
{
    public void Configure(EntityTypeBuilder<WeeklyGrocery> weekly)
    {
        weekly.ToTable("WeeklyGroceries");
        weekly.Property(w => w.Amount).HasPrecision(18, 4);
        weekly.HasIndex(w => new { w.HouseholdId, w.IngredientId }).IsUnique();
        weekly.HasIndex(w => w.IngredientId);
        weekly.HasIndex(w => w.UnitId);
        weekly.HasOne<Household>().WithMany().HasForeignKey(w => w.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        weekly.HasOne<Ingredient>().WithMany().HasForeignKey(w => w.IngredientId).OnDelete(DeleteBehavior.Restrict);
        weekly.HasOne<Unit>().WithMany().HasForeignKey(w => w.UnitId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
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
