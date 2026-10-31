namespace Foodplanner.Domain.MealPlans;

/// <summary>A recipe as the kid pick rules see it: its id and the ids of its global categories (Middag, Dessert, ...).</summary>
public sealed record KidPickRecipe(Guid Id, IReadOnlySet<Guid> CategoryTagIds);

/// <summary>What is already planned that week, for rules that look at the mix (T-090). The T-026 rule ignores it.</summary>
public sealed record KidPickPlan(DateOnly WeekStart, IReadOnlyList<(DateOnly Date, Guid RecipeId)> Entries);

/// <summary>
/// The seam for what a kid may add to the week plan (T-026). T-026 implements it as "dinner only"
/// (<see cref="DinnerOnlyKidPickRules"/>); T-090 replaces or extends it with the household's meal-mix rules. It applies in kid
/// mode only; an adult may add anything.
/// </summary>
public interface IKidPickRules
{
    bool IsAllowed(KidPickRecipe recipe, DateOnly day, KidPickPlan plan);
}

/// <summary>A recipe is a dinner when it is in the global category Middag (the plan has no slot column; T-090 may refine this).</summary>
public sealed class DinnerOnlyKidPickRules(Guid dinnerCategoryId) : IKidPickRules
{
    public bool IsAllowed(KidPickRecipe recipe, DateOnly day, KidPickPlan plan) =>
        recipe.CategoryTagIds.Contains(dinnerCategoryId);
}
