using System.Security.Claims;

namespace Foodplanner.Api.Features.Auth;

/// <summary>
/// Kid mode (T-026) is two claims in the auth cookie's principal: the active profile and the household it belongs to. Only the
/// server writes them (<see cref="Add"/>, from the activate endpoint), the cookie is protected by Data Protection, and
/// <see cref="CopyTo"/> carries them over when Identity rebuilds the principal on its security stamp refresh, so a refresh
/// can never unlock a device. "Kid mode" means either claim is present: a malformed value still locks.
/// </summary>
internal static class KidModeClaims
{
    public const string Profile = "fp:profile";
    public const string ProfileHousehold = "fp:profile-household";

    public static bool IsKidMode(ClaimsPrincipal principal) =>
        principal.HasClaim(c => c.Type is Profile or ProfileHousehold);

    public static Guid? ProfileId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(Profile), out var id) ? id : null;

    public static Guid? ProfileHouseholdId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ProfileHousehold), out var id) ? id : null;

    /// <summary>The claims that lock a session to a profile.</summary>
    public static IEnumerable<Claim> For(Guid profileId, Guid householdId) =>
    [
        new Claim(Profile, profileId.ToString()),
        new Claim(ProfileHousehold, householdId.ToString()),
    ];

    public static void Remove(ClaimsIdentity identity)
    {
        foreach (var claim in identity.Claims.Where(c => c.Type is Profile or ProfileHousehold).ToList())
        {
            identity.RemoveClaim(claim);
        }
    }

    /// <summary>Copies the kid claims of <paramref name="from"/> onto <paramref name="to"/> (replacing any there).</summary>
    public static void CopyTo(ClaimsPrincipal from, ClaimsPrincipal to)
    {
        if (!IsKidMode(from) || to.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        Remove(identity);
        foreach (var claim in from.Claims.Where(c => c.Type is Profile or ProfileHousehold))
        {
            identity.AddClaim(new Claim(claim.Type, claim.Value));
        }
    }
}
