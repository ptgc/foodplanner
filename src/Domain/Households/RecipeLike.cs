using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Households;

/// <summary>
/// A kid profile likes a recipe (T-026). Unique on (profile, recipe). It only ever links a profile and a recipe of its own
/// household (the save-time reference check enforces it). HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class RecipeLike : Entity, ITenantEntity
{
    private RecipeLike()
    {
    }

    public RecipeLike(Guid memberProfileId, Guid recipeId)
    {
        if (memberProfileId == Guid.Empty)
        {
            throw new ArgumentException("A like needs a profile.", nameof(memberProfileId));
        }

        if (recipeId == Guid.Empty)
        {
            throw new ArgumentException("A like needs a recipe.", nameof(recipeId));
        }

        MemberProfileId = memberProfileId;
        RecipeId = recipeId;
    }

    public Guid HouseholdId { get; private set; }

    public Guid MemberProfileId { get; private set; }

    public Guid RecipeId { get; private set; }
}
