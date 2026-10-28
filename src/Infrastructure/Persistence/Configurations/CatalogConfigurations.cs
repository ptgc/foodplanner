using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foodplanner.Infrastructure.Persistence.Configurations;

internal sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> unit)
    {
        unit.ToTable("Units");
        unit.Property(u => u.Code).HasMaxLength(Unit.MaxCodeLength).IsRequired();
        unit.Property(u => u.Name).HasMaxLength(Unit.MaxNameLength).IsRequired();
        unit.Property(u => u.Dimension).HasConversion<string>().HasMaxLength(20).IsRequired();
        unit.Property(u => u.FactorToBase).HasPrecision(18, 6);
        unit.HasIndex(u => u.Code).IsUnique();
    }
}

/// <summary>Global translations (T-039): keyed by kind, row id and locale. No foreign key (the kind selects the table); the seed keeps them whole.</summary>
internal sealed class CatalogTranslationConfiguration : IEntityTypeConfiguration<CatalogTranslation>
{
    public void Configure(EntityTypeBuilder<CatalogTranslation> translation)
    {
        translation.ToTable("CatalogTranslations");
        translation.HasKey(t => new { t.Kind, t.EntityId, t.Locale });
        translation.Property(t => t.Kind).HasMaxLength(CatalogTranslation.MaxKindLength).IsRequired();
        translation.Property(t => t.Locale).HasMaxLength(CatalogTranslation.MaxLocaleLength).IsRequired();
        translation.Property(t => t.Name).HasMaxLength(CatalogTranslation.MaxNameLength).IsRequired();

        // Duplicate-name checks look a typed name up across locales.
        translation.HasIndex(t => new { t.Kind, t.Name });
    }
}

internal sealed class IngredientConfiguration : IEntityTypeConfiguration<Ingredient>
{
    public void Configure(EntityTypeBuilder<Ingredient> ingredient)
    {
        ingredient.ToTable("Ingredients");
        ingredient.Property(i => i.Name).HasMaxLength(Ingredient.MaxNameLength).IsRequired();
        ingredient.Property(i => i.Dimension).HasConversion<string>().HasMaxLength(20).IsRequired();
        ingredient.Property(i => i.Section).HasConversion<string>().HasMaxLength(30).IsRequired();

        // Unique per household, and among the global rows (HouseholdId null). EF would add "WHERE HouseholdId IS NOT
        // NULL" to a unique index over a nullable column, which would leave global names unconstrained; SQL Server treats
        // the nulls as equal in a unique index, so remove the filter. Global-versus-household duplicates are refused by
        // the endpoint (a household cannot shadow a global name), not by the database.
        ingredient.HasIndex(i => new { i.HouseholdId, i.Name }).IsUnique().HasFilter(null);

        // Nullable: null is a global row. A household's ingredients go with the household.
        ingredient.HasOne<Household>().WithMany().HasForeignKey(i => i.HouseholdId).IsRequired(false).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// The global catalog rows, as migration data (<c>HasData</c>): applied by the migration in every environment, keyed by
/// fixed ids (<see cref="SeedIds"/>), so applying twice is a no-op (EF records the migration as applied and never
/// re-inserts; the ids make a hand-run of the same data conflict instead of duplicating). Applied after the tenancy
/// conventions because the audit columns are added by them. Global rows never pass through SaveChanges.
/// Changing the data file later needs a new migration (EF emits the update).
/// </summary>
internal static class CatalogSeedData
{
    /// <summary>The fixed audit timestamp of every seed row.</summary>
    public static readonly DateTimeOffset SeededAt = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Unit>().HasData(UnitSeed.All.Select(u => new
        {
            u.Id,
            u.Code,
            u.Name,
            u.Dimension,
            u.FactorToBase,
            CreatedAt = SeededAt,
            UpdatedAt = SeededAt,
        }));

        modelBuilder.Entity<Ingredient>().HasData(IngredientSeed.All.Select(i => new
        {
            i.Id,
            HouseholdId = (Guid?)null,
            i.Name,
            i.IsStaple,
            i.Dimension,
            i.Section,
            CreatedAt = SeededAt,
            UpdatedAt = SeededAt,
        }));

        modelBuilder.Entity<CatalogTranslation>().HasData(CatalogTranslation.SeedRows().Select(t => new
        {
            t.Kind,
            t.EntityId,
            t.Locale,
            t.Name,
        }));

        modelBuilder.Entity<Tag>().HasData(TagSeed.All.Select(t => new
        {
            t.Id,
            HouseholdId = (Guid?)null,
            t.Name,
            t.Kind,
            CreatedAt = SeededAt,
            UpdatedAt = SeededAt,
        }));
    }
}
