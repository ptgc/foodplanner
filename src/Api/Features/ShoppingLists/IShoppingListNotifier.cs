using System.Text.Json.Serialization;

namespace Foodplanner.Api.Features.ShoppingLists;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShoppingListChangeKind
{
    /// <summary>The list was generated or regenerated from the plan.</summary>
    Generated,
    ManualItemAdded,
    ItemChecked,
    ItemUnchecked,
    ItemRemoved,
}

/// <summary>What changed, for listeners. <c>ItemId</c> is null for a (re)generation, which can change many items.</summary>
public sealed record ShoppingListChange(Guid HouseholdId, DateOnly WeekStart, ShoppingListChangeKind Kind, Guid? ItemId);

/// <summary>
/// The hook every shopping list mutation calls after it has been saved (T-033). T-034 registers the implementation
/// that broadcasts over SignalR (<see cref="SignalRShoppingListNotifier"/>). A failure here never undoes the saved
/// change: the endpoint logs and ignores it.
/// </summary>
public interface IShoppingListNotifier
{
    Task NotifyAsync(ShoppingListChange change, CancellationToken cancellationToken);
}
