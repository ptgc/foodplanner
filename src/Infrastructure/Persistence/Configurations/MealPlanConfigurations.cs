using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foodplanner.Infrastructure.Persistence.Configurations;

// Same shape as recipes (see RecipeConfigurations): no navigations, Household -> MealPlan -> entries is the only
// cascade; every other foreign key is NO ACTION (SQL Server rejects two cascade paths, and a planned recipe or a
// user must not vanish from under an entry).

internal sealed class MealPlanConfiguration : IEntityTypeConfiguration<MealPlan>
{
    public void Configure(EntityTypeBuilder<MealPlan> plan)
    {
        plan.ToTable("MealPlans");
        plan.HasIndex(p => new { p.HouseholdId, p.WeekStart }).IsUnique();
        plan.HasOne<Household>().WithMany().HasForeignKey(p => p.HouseholdId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MealPlanEntryConfiguration : IEntityTypeConfiguration<MealPlanEntry>
{
    public void Configure(EntityTypeBuilder<MealPlanEntry> entry)
    {
        entry.ToTable("MealPlanEntries");
        entry.HasIndex(e => new { e.MealPlanId, e.Date });
        entry.HasIndex(e => e.RecipeId);
        entry.HasIndex(e => e.PickedByUserId);
        entry.HasOne<MealPlan>().WithMany().HasForeignKey(e => e.MealPlanId).OnDelete(DeleteBehavior.Cascade);
        entry.HasOne<Household>().WithMany().HasForeignKey(e => e.HouseholdId).OnDelete(DeleteBehavior.Restrict);

        // A planned recipe cannot be deleted (the recipe endpoint answers 409 first); the user is global identity data.
        entry.HasOne<Recipe>().WithMany().HasForeignKey(e => e.RecipeId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<AppUser>().WithMany().HasForeignKey(e => e.PickedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
