using System.Collections.Concurrent;
using Foodplanner.Api.Features.ShoppingLists;
using Microsoft.AspNetCore.SignalR;

namespace Foodplanner.Api.Realtime;

/// <summary>
/// Ends live SignalR connections when what authorised them is gone (Q-015, T-044). The hub checks the cookie only when a
/// connection opens, and membership on every join, so a connection that already joined a household would otherwise keep
/// receiving that household's (content-free) change signals after its member was removed. Callers invoke these AFTER the
/// change is committed. They never throw: a failure is logged and the connection simply lives until it disconnects, which
/// is the old, accepted behaviour (the content stays closed, because every read re-checks membership).
/// <para>
/// In-process only: the registry knows the connections of this API replica. This is the same single-replica limit as the
/// hub itself (T-034, ADR 0007); behind Azure SignalR or a backplane it has to move to the service.
/// </para>
/// </summary>
public interface ILiveConnections
{
    /// <summary>The user is no longer a member of the household: leave its group and close the connections that joined it.</summary>
    Task EvictUserFromHouseholdAsync(Guid userId, Guid householdId);

    /// <summary>The household is gone: leave its group and close every connection that joined it.</summary>
    Task CloseHouseholdAsync(Guid householdId);

    /// <summary>
    /// The user's security stamp changed (account confirmed, and later password change or "log out everywhere"): close all
    /// of the user's connections. Whoever rotates a stamp must call this.
    /// </summary>
    Task CloseUserAsync(Guid userId);
}

/// <summary>Tracks the live hub connections per user and per joined household. Registered as a singleton.</summary>
internal sealed class LiveConnectionRegistry(IHubContext<ShoppingListHub> hub, ILogger<LiveConnectionRegistry> logger) : ILiveConnections
{
    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    private sealed class Entry(Guid userId, HubCallerContext context)
    {
        private readonly HashSet<Guid> _joined = [];

        public Guid UserId { get; } = userId;

        public HubCallerContext Context { get; } = context;

        public void Join(Guid household)
        {
            lock (_joined)
            {
                _joined.Add(household);
            }
        }

        public void Leave(Guid household)
        {
            lock (_joined)
            {
                _joined.Remove(household);
            }
        }

        public bool HasJoined(Guid household)
        {
            lock (_joined)
            {
                return _joined.Contains(household);
            }
        }
    }

    /// <summary>Number of connections currently known (for tests and diagnostics).</summary>
    public int Count => _connections.Count;

    public void Connected(HubCallerContext context, Guid userId) => _connections[context.ConnectionId] = new Entry(userId, context);

    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);

    public void Joined(string connectionId, Guid household)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
        {
            entry.Join(household);
        }
    }

    public void Left(string connectionId, Guid household)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
        {
            entry.Leave(household);
        }
    }

    public Task EvictUserFromHouseholdAsync(Guid userId, Guid householdId) =>
        CloseAsync(_connections.Values.Where(e => e.UserId == userId && e.HasJoined(householdId)), householdId);

    public Task CloseHouseholdAsync(Guid householdId) =>
        CloseAsync(_connections.Values.Where(e => e.HasJoined(householdId)), householdId);

    public Task CloseUserAsync(Guid userId) =>
        CloseAsync(_connections.Values.Where(e => e.UserId == userId), householdId: null);

    private async Task CloseAsync(IEnumerable<Entry> targets, Guid? householdId)
    {
        foreach (var entry in targets.ToList())
        {
            var connectionId = entry.Context.ConnectionId;
            try
            {
                if (householdId is { } household)
                {
                    await hub.Groups.RemoveFromGroupAsync(connectionId, ShoppingListHub.GroupName(household));
                }

                // Aborting ends the connection on the server; the group memberships die with it.
                entry.Context.Abort();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not close hub connection {ConnectionId}; it stays until it disconnects.", connectionId);
            }
        }
    }
}
