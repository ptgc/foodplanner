using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// A household's memory that it has a staple ("I have salt"). One row per household and ingredient (unique index).
/// While it is active the staple is no longer prompted in the "check you have" section of a generated list
/// (<see cref="PantryFilter"/>). With <see cref="ExpiresAt"/> set it is active until then and prompts again after;
/// without, it stays until the household un-confirms it. HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class PantryItem : Entity, ITenantEntity
{
    private PantryItem()
    {
    }

    public PantryItem(Guid ingredientId, Guid confirmedByUserId, DateTimeOffset confirmedAt, DateTimeOffset? expiresAt)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A pantry item needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
        Confirm(confirmedByUserId, confirmedAt, expiresAt);
    }

    public Guid HouseholdId { get; private set; }

    public Guid IngredientId { get; private set; }

    public DateTimeOffset ConfirmedAt { get; private set; }

    public Guid ConfirmedByUserId { get; private set; }

    /// <summary>Null = no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Confirms (again): the latest confirmation wins, including its expiry.</summary>
    public void Confirm(Guid confirmedByUserId, DateTimeOffset confirmedAt, DateTimeOffset? expiresAt)
    {
        if (confirmedByUserId == Guid.Empty)
        {
            throw new ArgumentException("A confirmation needs a user.", nameof(confirmedByUserId));
        }

        if (expiresAt is { } expiry && expiry <= confirmedAt)
        {
            throw new ArgumentException("The expiry must be after the confirmation.", nameof(expiresAt));
        }

        ConfirmedByUserId = confirmedByUserId;
        ConfirmedAt = confirmedAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>Points the row at another ingredient (merging two ingredients, T-079).</summary>
    public void ChangeIngredient(Guid ingredientId)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A pantry item needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
    }

    /// <summary>
    /// Combines another row of the same household (merging two ingredients, T-079) into this one: the more recent
    /// confirmation wins (user and time), and the expiry is the later of the two, no expiry counting as the latest.
    /// </summary>
    public void Absorb(PantryItem other)
    {
        ArgumentNullException.ThrowIfNull(other);
        DateTimeOffset? expiry = ExpiresAt is null || other.ExpiresAt is null
            ? null
            : (ExpiresAt > other.ExpiresAt ? ExpiresAt : other.ExpiresAt);
        var newer = other.ConfirmedAt > ConfirmedAt ? other : this;
        Confirm(newer.ConfirmedByUserId, newer.ConfirmedAt, expiry);
    }

    public bool IsActive(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}
