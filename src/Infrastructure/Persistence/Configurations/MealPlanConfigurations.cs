using Foodplanner.Domain.Households;
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

        // The week lock (T-026): the user who locked it is global identity data, NO ACTION like the other user references.
        plan.HasOne<AppUser>().WithMany().HasForeignKey(p => p.LockedByUserId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
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

        // The kid profile that picked it (T-026). NO ACTION: Household -> MealPlan -> Entry already cascades, and a second path
        // from Household via MemberProfile is rejected. The profile delete endpoint clears this column first.
        entry.HasIndex(e => e.PickedByProfileId);
        entry.HasOne<MemberProfile>().WithMany().HasForeignKey(e => e.PickedByProfileId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

// The household's weekly meal mix rules (T-090): one row per food type, no references besides the household.
internal sealed class MealMixRuleConfiguration : IEntityTypeConfiguration<MealMixRule>
{
    public void Configure(EntityTypeBuilder<MealMixRule> rule)
    {
        rule.ToTable("MealMixRules");
        rule.Property(r => r.FoodType).HasConversion<string>().HasMaxLength(20).IsRequired();
        rule.HasIndex(r => new { r.HouseholdId, r.FoodType }).IsUnique();
        rule.HasOne<Household>().WithMany().HasForeignKey(r => r.HouseholdId).OnDelete(DeleteBehavior.Cascade);
    }
}
