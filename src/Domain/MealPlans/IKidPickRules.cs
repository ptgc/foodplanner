using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.MealPlans;

/// <summary>A recipe as the kid pick rules see it: its id, the ids of its global categories (Middag, Dessert, ...) and its food type (T-089).</summary>
public sealed record KidPickRecipe(Guid Id, IReadOnlySet<Guid> CategoryTagIds, FoodType FoodType = FoodType.Unknown);

/// <summary>One planned entry as the pick rules see it: <c>IsDinner</c> is whether its recipe is in the category Middag.</summary>
public sealed record KidPickEntry(DateOnly Date, Guid RecipeId, bool IsDinner = false, FoodType FoodType = FoodType.Unknown);

/// <summary>What is already planned that week and the household's weekly meal mix rules (T-090). Without rules, only "dinner only" applies.</summary>
public sealed record KidPickPlan(DateOnly WeekStart, IReadOnlyList<KidPickEntry> Entries, IReadOnlyList<MealMixRequirement>? Rules = null);

/// <summary>Why a recipe cannot be added to a day. The codes are part of the API.</summary>
public static class PickReasons
{
    /// <summary>Kids can pick dinners only.</summary>
    public const string NotDinner = "not-dinner";

    /// <summary>The week's meal mix rules could no longer be met.</summary>
    public const string BreaksMealMix = "breaks-meal-mix";
}

/// <summary>
/// The seam for what a kid may add to the week plan (T-026, extended by T-090 with the household's meal mix rules). It applies in
/// kid mode only; an adult may add anything (the UI greys out what breaks the rules).
/// </summary>
public interface IKidPickRules
{
    bool IsAllowed(KidPickRecipe recipe, DateOnly day, KidPickPlan plan);
}

/// <summary>A recipe is a dinner when it is in the global category Middag (the plan has no slot column).</summary>
public class DinnerOnlyKidPickRules(Guid dinnerCategoryId) : IKidPickRules
{
    public bool IsDinner(KidPickRecipe recipe) => recipe.CategoryTagIds.Contains(dinnerCategoryId);

    public virtual bool IsAllowed(KidPickRecipe recipe, DateOnly day, KidPickPlan plan) => IsDinner(recipe);
}

/// <summary>
/// Dinner only (T-026) plus the household's meal mix (T-090): a dinner is refused when adding it on that day would make the
/// weekly minimums impossible to meet (see <see cref="MealMix"/>). Without rules the answer is the same as dinner only.
/// </summary>
public sealed class MealMixKidPickRules(Guid dinnerCategoryId) : DinnerOnlyKidPickRules(dinnerCategoryId)
{
    public override bool IsAllowed(KidPickRecipe recipe, DateOnly day, KidPickPlan plan) =>
        IsDinner(recipe) && FitsMealMix(recipe, day, plan);

    /// <summary>Only the mix part: always true for a non-dinner or when the household has no rules.</summary>
    public bool FitsMealMix(KidPickRecipe recipe, DateOnly day, KidPickPlan plan)
    {
        if (!IsDinner(recipe) || plan.Rules is not { Count: > 0 } rules)
        {
            return true;
        }

        var dinners = plan.Entries.Where(e => e.IsDinner).ToList();
        var dinnerDays = dinners.Select(e => e.Date).Distinct().ToList();
        var openDays = Math.Max(0, MealMix.DinnerDaysPerWeek - dinnerDays.Count);
        return MealMix.CanAdd(rules, openDays, [.. dinners.Select(e => e.FoodType)], recipe.FoodType, fillsOpenDay: !dinnerDays.Contains(day));
    }
}
