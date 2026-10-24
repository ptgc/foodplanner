using System.Security.Cryptography;
using System.Text.Json;
using Foodplanner.Api.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>What the server pushes to a household group on every saved mutation. Clients refetch (see docs/domain.md).</summary>
public sealed record ShoppingListChangedMessage(Guid HouseholdId, DateOnly WeekStart, ShoppingListChangeKind Kind, Guid? ItemId);

/// <summary>
/// The list of a week plus <c>Version</c>, a fingerprint of that content (equal versions mean equal lists). The list has
/// the shape of the REST GET response, so a client replaces its local state with it wholesale.
/// </summary>
public sealed record ShoppingListState(string Version, ShoppingListResponse List);

/// <summary>
/// Live sync of shopping lists (T-034). Clients connect once (cookie auth, same origin), call <see cref="JoinHousehold"/>
/// and receive <see cref="ChangedEvent"/> for their household only. Every method re-checks membership through
/// <see cref="HouseholdMembershipGuard"/> and never trusts the client's household id.
/// </summary>
[Authorize]
internal sealed class ShoppingListHub(HouseholdMembershipGuard guard, AppDbContext db) : Hub
{
    public const string Path = "/api/hubs/shopping-list";
    public const string ChangedEvent = "ShoppingListChanged";
    public const string Forbidden = "forbidden";

    private static readonly JsonSerializerOptions VersionJson = new(JsonSerializerDefaults.Web);

    public static string GroupName(Guid householdId) => $"household:{householdId:N}";

    /// <summary>Starts delivering this household's events to the connection. Call again after every reconnect (groups do not survive one).</summary>
    public async Task JoinHousehold(Guid householdId)
    {
        await RequireMemberAsync(householdId);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(householdId), Context.ConnectionAborted);
    }

    /// <summary>No membership check: leaving can only reduce what the connection receives.</summary>
    public Task LeaveHousehold(Guid householdId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(householdId), Context.ConnectionAborted);

    /// <summary>The current list of the week, for the first load and for every resync after a reconnect.</summary>
    public async Task<ShoppingListState> GetState(Guid householdId, string weekStart)
    {
        await RequireMemberAsync(householdId);
        if (!ShoppingListEndpoints.IsWeekStart(weekStart, out var monday))
        {
            throw new HubException("weekStart must be the Monday of an ISO week, as yyyy-MM-dd.");
        }

        var list = await ShoppingListEndpoints.ReadAsync(db, monday, Context.ConnectionAborted);
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(list, VersionJson));
        return new ShoppingListState(Convert.ToHexStringLower(hash.AsSpan(0, 12)), list);
    }

    private async Task RequireMemberAsync(Guid householdId)
    {
        if (!await guard.TryEnterAsync(Context, householdId, Context.ConnectionAborted))
        {
            // One answer for "no such household" and "not yours": do not reveal which households exist.
            throw new HubException(Forbidden);
        }
    }
}

/// <summary>Broadcasts a saved change to the household's group. Only the metadata goes out, no list content.</summary>
internal sealed class SignalRShoppingListNotifier(IHubContext<ShoppingListHub> hub) : IShoppingListNotifier
{
    public Task NotifyAsync(ShoppingListChange change, CancellationToken cancellationToken) =>
        hub.Clients.Group(ShoppingListHub.GroupName(change.HouseholdId)).SendAsync(
            ShoppingListHub.ChangedEvent,
            new ShoppingListChangedMessage(change.HouseholdId, change.WeekStart, change.Kind, change.ItemId),
            cancellationToken);
}

/// <summary>
/// Cross-site WebSocket hijacking guard for the hub path. The auth cookie is SameSite=Lax, which a browser already
/// withholds from a cross-site handshake; but "same site" includes sibling subdomains, so the Origin is checked too.
/// </summary>
internal sealed class HubOriginMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(ShoppingListHub.Path) && !IsSameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }

    /// <summary>
    /// Browsers always send Origin on a WebSocket handshake and on a cross-origin POST. A request without one is not
    /// from a browser page (and would need the cookie anyway). Hosts are compared without port and scheme because
    /// nginx forwards <c>$host</c>, which has no port, and TLS ends before the API.
    /// </summary>
    private static bool IsSameOrigin(HttpRequest request) =>
        !request.Headers.TryGetValue("Origin", out var origin)
        || (Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase));
}

public static class ShoppingListSyncSetup
{
    public static IServiceCollection AddShoppingListSync(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddScoped<HouseholdMembershipGuard>();
        services.AddSingleton<IShoppingListNotifier, SignalRShoppingListNotifier>();
        return services;
    }

    /// <summary>
    /// Mapped on the app, NOT on <c>MapApiRoot</c>: the hub's negotiate is a POST that is not a state change, and the
    /// root group's antiforgery filter does not apply to a hub endpoint anyway (see docs/domain.md, shopping list sync).
    /// </summary>
    public static IEndpointRouteBuilder MapShoppingListHub(this IEndpointRouteBuilder app)
    {
        app.MapHub<ShoppingListHub>(ShoppingListHub.Path).RequireAuthorization();
        return app;
    }
}
