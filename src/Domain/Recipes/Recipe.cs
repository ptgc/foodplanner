using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// A household's recipe. Steps (<see cref="RecipeStep"/>) and ingredients (<see cref="RecipeIngredient"/>) are separate
/// tenant entities that point to it by id; there are no navigations (see docs/domain.md, T-021). HouseholdId is
/// assigned by Infrastructure when the recipe is added.
/// </summary>
public sealed class Recipe : Entity, ITenantEntity
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MinServings = 1;
    public const int MaxServings = 100;
    public const int MaxMinutes = 1440;
    public const int MaxSteps = 50;
    public const int MaxIngredients = 100;

    private Recipe()
    {
    }

    public Recipe(string title, string? description, int servings, int? prepMinutes, int? cookMinutes) =>
        Set(title, description, servings, prepMinutes, cookMinutes);

    public Guid HouseholdId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int Servings { get; private set; }

    public int? PrepMinutes { get; private set; }

    public int? CookMinutes { get; private set; }

    /// <summary>Prep plus cook time; null when neither is known.</summary>
    public int? TotalMinutes => PrepMinutes is null && CookMinutes is null ? null : (PrepMinutes ?? 0) + (CookMinutes ?? 0);

    public void Update(string title, string? description, int servings, int? prepMinutes, int? cookMinutes) =>
        Set(title, description, servings, prepMinutes, cookMinutes);

    private void Set(string title, string? description, int servings, int? prepMinutes, int? cookMinutes)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            throw new ArgumentException("Invalid recipe title.", nameof(title));
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            throw new ArgumentException("The description is too long.", nameof(description));
        }

        if (servings is < MinServings or > MaxServings)
        {
            throw new ArgumentOutOfRangeException(nameof(servings));
        }

        if (prepMinutes is < 0 or > MaxMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(prepMinutes));
        }

        if (cookMinutes is < 0 or > MaxMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(cookMinutes));
        }

        Title = title;
        Description = description;
        Servings = servings;
        PrepMinutes = prepMinutes;
        CookMinutes = cookMinutes;
    }
}
