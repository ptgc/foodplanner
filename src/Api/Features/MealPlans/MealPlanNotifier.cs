using System.Text.Json.Serialization;
using Foodplanner.Api.Features.ShoppingLists;
using Microsoft.AspNetCore.SignalR;

namespace Foodplanner.Api.Features.MealPlans;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MealPlanChangeKind
{
    EntryAdded,
    EntryChanged,
    EntryRemoved,
    Locked,
    Unlocked,
}

/// <summary>What changed, for listeners. <c>EntryId</c> is null for <see cref="MealPlanChangeKind.Locked"/> and <see cref="MealPlanChangeKind.Unlocked"/>.</summary>
public sealed record MealPlanChange(Guid HouseholdId, DateOnly WeekStart, MealPlanChangeKind Kind, Guid? EntryId);

/// <summary>What the server pushes to a household group on every saved plan change. Clients refetch the week; no plan content is sent.</summary>
public sealed record MealPlanChangedMessage(Guid HouseholdId, DateOnly WeekStart, MealPlanChangeKind Kind, Guid? EntryId);

/// <summary>
/// The hook every meal plan write calls after it has been saved (T-026), like <see cref="IShoppingListNotifier"/>. A failure
/// here never undoes the saved change: the endpoint logs and ignores it.
/// </summary>
public interface IMealPlanNotifier
{
    Task NotifyAsync(MealPlanChange change, CancellationToken cancellationToken);
}

/// <summary>Broadcasts a saved plan change to the household's group on the existing hub. Only the metadata goes out.</summary>
internal sealed class SignalRMealPlanNotifier(IHubContext<ShoppingListHub> hub) : IMealPlanNotifier
{
    public const string ChangedEvent = "MealPlanChanged";

    public Task NotifyAsync(MealPlanChange change, CancellationToken cancellationToken) =>
        hub.Clients.Group(ShoppingListHub.GroupName(change.HouseholdId)).SendAsync(
            ChangedEvent,
            new MealPlanChangedMessage(change.HouseholdId, change.WeekStart, change.Kind, change.EntryId),
            cancellationToken);
}

public static class MealPlanServicesSetup
{
    public static IServiceCollection AddMealPlanServices(this IServiceCollection services)
    {
        services.AddSingleton<IMealPlanNotifier, SignalRMealPlanNotifier>();
        services.AddSingleton(_ =>
            new Foodplanner.Domain.MealPlans.MealMixKidPickRules(
                Foodplanner.Domain.Recipes.TagSeed.All.Single(t => t.Key == "middag").Id));
        services.AddSingleton<Foodplanner.Domain.MealPlans.IKidPickRules>(sp =>
            sp.GetRequiredService<Foodplanner.Domain.MealPlans.MealMixKidPickRules>());
        return services;
    }
}
