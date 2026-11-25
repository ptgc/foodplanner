using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>Which action a flow cookie belongs to (T-130): the cart fill of T-053 or "Sync with Oda". A cookie of one kind never completes the other.</summary>
public enum OdaFlowKind
{
    Cart = 0,
    Sync = 1,
}

/// <summary>
/// What the browser that started an Oda sign-in carries to the call that completes it (Q-033, option 1). <c>Kind</c> defaults to the cart.
/// For <c>Sync</c> the week is unused and the options of the run are fixed here, so the completing call cannot change them (T-130).
/// </summary>
public sealed record OdaFlowState(
    string Verifier,
    string State,
    Guid HouseholdId,
    DateOnly WeekStart,
    Guid UserId,
    DateTimeOffset ExpiresAt,
    OdaFlowKind Kind = OdaFlowKind.Cart,
    bool IncludeStaples = false,
    bool IncludeExisting = false,
    int Offset = 0);

/// <summary>
/// The OAuth flow state of the session-only Oda connection (T-053, Q-033 option 1): the PKCE verifier, the <c>state</c>, the
/// household, the week and the user, in a short-lived (10 minutes) encrypted <c>__Host-</c> cookie (ASP.NET Data Protection;
/// <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite=Lax</c>, path <c>/</c>). There is no table and no server-side copy, so any replica that
/// shares the Data Protection key ring (T-016, needed before more than one replica) can serve both calls. The cookie holds no Oda
/// credential; the verifier is useless without the single-use code and is never logged.
/// <para>
/// Single use: the cookie is deleted when it is read, and a replica remembers the SHA-256 of a used <c>state</c> for the cookie's
/// lifetime, so a replayed cookie is refused on that replica. Across replicas Oda's single-use authorization code is what stops a replay.
/// </para>
/// </summary>
public sealed class OdaFlowCookie(IDataProtectionProvider protection, TimeProvider time)
{
    public const string CookieName = "__Host-foodplanner-oda-flow";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector = protection.CreateProtector("Foodplanner.Oda.Flow.v1");
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    public void Issue(HttpResponse response, OdaFlowState state) =>
        response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(state)), Options(state.ExpiresAt - time.GetUtcNow()));

    /// <summary>
    /// Reads and consumes the flow state for this caller, week and household. Null for every kind of refusal (no cookie, tampered,
    /// expired, wrong <c>state</c>, another user, household or week, already used), so the answers cannot be told apart. The cookie is
    /// always deleted.
    /// </summary>
    public OdaFlowState? Take(HttpContext http, string? presentedState, Guid householdId, DateOnly weekStart, Guid userId, OdaFlowKind kind = OdaFlowKind.Cart)
    {
        var raw = http.Request.Cookies[CookieName];
        http.Response.Cookies.Delete(CookieName, Options(TimeSpan.Zero));
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(presentedState))
        {
            return null;
        }

        OdaFlowState? state;
        try
        {
            state = JsonSerializer.Deserialize<OdaFlowState>(_protector.Unprotect(raw));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (state is null || state.ExpiresAt <= now || state.UserId != userId || state.HouseholdId != householdId || state.WeekStart != weekStart
            || state.Kind != kind || state.Offset < 0
            || string.IsNullOrEmpty(state.State) || string.IsNullOrEmpty(state.Verifier)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state.State), Encoding.UTF8.GetBytes(presentedState)))
        {
            return null;
        }

        foreach (var (key, expires) in _used)
        {
            if (expires <= now)
            {
                _used.TryRemove(key, out _);
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.State)));
        return _used.TryAdd(hash, state.ExpiresAt) ? state : null;
    }

    private static CookieOptions Options(TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
        MaxAge = maxAge > TimeSpan.Zero ? maxAge : null,
    };
}
