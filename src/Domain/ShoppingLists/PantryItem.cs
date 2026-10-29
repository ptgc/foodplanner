using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>
/// A household's memory that it has an ingredient at home ("we have salt", "300 g cheese"), T-037 and T-093. One row per
/// household and ingredient (unique index). Any ingredient, optionally with an amount and unit (both or neither; no amount =
/// "we have some"). While it is active (<see cref="IsActive"/>) <see cref="PantryFilter"/> subtracts it from a generated
/// list: a staple is no longer prompted, a covered item moves to "check you have", a partly covered one buys the rest.
/// With <see cref="ExpiresAt"/> set it is active until then; without, it stays until the household removes it.
/// HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class PantryItem : Entity, ITenantEntity
{
    private PantryItem()
    {
    }

    public PantryItem(
        Guid ingredientId, Guid confirmedByUserId, DateTimeOffset confirmedAt, DateTimeOffset? expiresAt, decimal? amount = null, Guid? unitId = null)
    {
        if (ingredientId == Guid.Empty)
        {
            throw new ArgumentException("A pantry item needs an ingredient.", nameof(ingredientId));
        }

        IngredientId = ingredientId;
        Confirm(confirmedByUserId, confirmedAt, expiresAt, amount, unitId);
    }

    public Guid HouseholdId { get; private set; }

    public Guid IngredientId { get; private set; }

    public DateTimeOffset ConfirmedAt { get; private set; }

    public Guid ConfirmedByUserId { get; private set; }

    /// <summary>Null = no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>What the household has; null (with a null unit) = "we have some", no amount known.</summary>
    public decimal? Amount { get; private set; }

    public Guid? UnitId { get; private set; }

    /// <summary>Confirms (again): the latest confirmation wins, including its expiry and amount.</summary>
    public void Confirm(Guid confirmedByUserId, DateTimeOffset confirmedAt, DateTimeOffset? expiresAt, decimal? amount = null, Guid? unitId = null)
    {
        if (confirmedByUserId == Guid.Empty)
        {
            throw new ArgumentException("A confirmation needs a user.", nameof(confirmedByUserId));
        }

        if (expiresAt is { } expiry && expiry <= confirmedAt)
        {
            throw new ArgumentException("The expiry must be after the confirmation.", nameof(expiresAt));
        }

        if ((amount is null) != (unitId is null) || unitId == Guid.Empty)
        {
            throw new ArgumentException("Amount and unit go together: both, or neither for no amount.", nameof(unitId));
        }

        if (amount is { } a && !WeeklyGrocery.IsValidAmount(a))
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        ConfirmedByUserId = confirmedByUserId;
        ConfirmedAt = confirmedAt;
        ExpiresAt = expiresAt;
        Amount = amount;
        UnitId = unitId;
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
    /// confirmation wins (user and time), and the expiry is the later of the two, no expiry counting as the latest. The amount
    /// is the sum when both have one in the same unit; otherwise the more recent confirmation's amount, or the other's when
    /// that one has none (an amount beats "some"). Units are not converted here (Domain stays free of the catalog).
    /// </summary>
    public void Absorb(PantryItem other)
    {
        ArgumentNullException.ThrowIfNull(other);
        DateTimeOffset? expiry = ExpiresAt is null || other.ExpiresAt is null
            ? null
            : (ExpiresAt > other.ExpiresAt ? ExpiresAt : other.ExpiresAt);
        var newer = other.ConfirmedAt > ConfirmedAt ? other : this;
        var older = ReferenceEquals(newer, this) ? other : this;
        decimal? amount;
        Guid? unitId;
        if (Amount is { } mine && other.Amount is { } theirs && UnitId == other.UnitId && WeeklyGrocery.IsValidAmount(mine + theirs))
        {
            (amount, unitId) = (mine + theirs, UnitId);
        }
        else if (newer.Amount is not null)
        {
            (amount, unitId) = (newer.Amount, newer.UnitId);
        }
        else
        {
            (amount, unitId) = (older.Amount, older.UnitId);
        }

        Confirm(newer.ConfirmedByUserId, newer.ConfirmedAt, expiry, amount, unitId);
    }

    public bool IsActive(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}
