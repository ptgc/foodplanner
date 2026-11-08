using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.MealPlans;

/// <summary>
/// One planned meal: a recipe on a date, for a number of servings, picked by a user. A day can have several entries
/// (ordered by creation). The recipe is not changed after the entry is made; to swap it, delete and add. HouseholdId is
/// assigned by Infrastructure when the entry is added.
/// </summary>
public sealed class MealPlanEntry : Entity, ITenantEntity
{
    public const int MinServings = 1;
    public const int MaxServings = 50;

    private MealPlanEntry()
    {
    }

    public MealPlanEntry(
        Guid mealPlanId, DateOnly weekStart, DateOnly date, Guid recipeId, int servings, Guid pickedByUserId, Guid? pickedByProfileId = null)
    {
        if (pickedByProfileId == Guid.Empty)
        {
            throw new ArgumentException("A profile id cannot be empty; use null for no profile.", nameof(pickedByProfileId));
        }

        if (mealPlanId == Guid.Empty)
        {
            throw new ArgumentException("An entry needs a plan.", nameof(mealPlanId));
        }

        if (recipeId == Guid.Empty)
        {
            throw new ArgumentException("An entry needs a recipe.", nameof(recipeId));
        }

        if (pickedByUserId == Guid.Empty)
        {
            throw new ArgumentException("An entry needs the user who picked it.", nameof(pickedByUserId));
        }

        MealPlanId = mealPlanId;
        RecipeId = recipeId;
        PickedByUserId = pickedByUserId;
        PickedByProfileId = pickedByProfileId;
        Change(weekStart, date, servings);
    }

    public Guid HouseholdId { get; private set; }

    public Guid MealPlanId { get; private set; }

    public DateOnly Date { get; private set; }

    public Guid RecipeId { get; private set; }

    public int Servings { get; private set; }

    /// <summary>The account that picked the entry; null once that account was deleted (T-095): the entry stays as history.</summary>
    public Guid? PickedByUserId { get; private set; }

    /// <summary>
    /// The kid profile that picked this entry (T-026), or null: picked by the account holder, or the profile was deleted.
    /// <see cref="PickedByUserId"/> is always the signed-in account.
    /// </summary>
    public Guid? PickedByProfileId { get; private set; }

    /// <summary>The profile was deleted: the history keeps only the account holder.</summary>
    public void ClearPickedByProfile() => PickedByProfileId = null;

    /// <summary>Moves the entry to another day of the same week and/or changes the servings.</summary>
    public void Change(DateOnly weekStart, DateOnly date, int servings)
    {
        if (!MealPlan.IsInWeek(weekStart, date))
        {
            throw new ArgumentOutOfRangeException(nameof(date), "The date is outside the plan's week.");
        }

        if (servings is < MinServings or > MaxServings)
        {
            throw new ArgumentOutOfRangeException(nameof(servings));
        }

        Date = date;
        Servings = servings;
    }
}
