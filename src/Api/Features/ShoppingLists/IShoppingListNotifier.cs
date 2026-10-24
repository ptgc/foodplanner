namespace Foodplanner.Api.Features.ShoppingLists;

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
/// The hook every shopping list mutation calls after it has been saved (T-033). The default does nothing; T-034 registers
/// an implementation that broadcasts over SignalR. A notifier must not throw for a missing listener: a failure here
/// never undoes the saved change, and the endpoint ignores it.
/// </summary>
public interface IShoppingListNotifier
{
    Task NotifyAsync(ShoppingListChange change, CancellationToken cancellationToken);
}

internal sealed class NoOpShoppingListNotifier : IShoppingListNotifier
{
    public Task NotifyAsync(ShoppingListChange change, CancellationToken cancellationToken) => Task.CompletedTask;
}
