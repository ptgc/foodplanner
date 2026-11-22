using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// The Oda product a household wants for an ingredient (T-052, Q-032): only Oda's integer product id, set by an Adult from the
/// Oda result screen. No name, brand, price, URL or search hit is stored. One row per household and ingredient (unique index), a new
/// pick replaces the old. The ingredient is global or the household's own, pointed at by id. HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class PreferredOdaProduct : Entity, ITenantEntity
{
    /// <summary>The most picks one household can have (a guard against abuse, not a product number).</summary>
    public const int MaxPerHousehold = 500;

    private PreferredOdaProduct()
    {
    }

    public PreferredOdaProduct(Guid ingredientId, long odaProductId)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A preferred product needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
        Replace(odaProductId);
    }

    public Guid HouseholdId { get; private set; }

    public Guid IngredientId { get; private set; }

    /// <summary>Oda's integer product id, more than 0.</summary>
    public long OdaProductId { get; private set; }

    public static bool IsValidProductId(long odaProductId) => odaProductId > 0;

    /// <summary>A new pick for the same ingredient.</summary>
    public void Replace(long odaProductId)
    {
        if (!IsValidProductId(odaProductId))
        {
            throw new ArgumentOutOfRangeException(nameof(odaProductId), "An Oda product id is more than 0.");
        }

        OdaProductId = odaProductId;
    }

    /// <summary>Points the row at another ingredient (merging two ingredients, T-079).</summary>
    public void ChangeIngredient(Guid ingredientId)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A preferred product needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
    }
}
