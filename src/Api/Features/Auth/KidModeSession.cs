using System.Security.Claims;
using Foodplanner.Api.Realtime;
using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Foodplanner.Api.Features.Auth;

/// <summary>
/// Moves this device's session in and out of kid mode (T-026) by re-issuing the auth cookie. It lives in the Auth feature
/// because only code there may use <c>SignInManager</c>. The security stamp is untouched, so the account's other devices and
/// sessions are unaffected.
/// </summary>
public interface IKidModeSession
{
    /// <summary>Re-issues the cookie locked to the profile (also kid to kid), then closes the account's live hub connections.</summary>
    Task<bool> EnterAsync(Guid userId, Guid profileId, Guid householdId);

    /// <summary>Re-issues the cookie without the kid claims (the PIN was right), then closes the account's live hub connections.</summary>
    Task<bool> ExitAsync(Guid userId);
}

internal sealed class KidModeSession(UserManager<AppUser> users, SignInManager<AppUser> signIn, ILiveConnections live) : IKidModeSession
{
    public async Task<bool> EnterAsync(Guid userId, Guid profileId, Guid householdId)
    {
        if (await users.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return false;
        }

        await signIn.SignInWithClaimsAsync(user, isPersistent: true, KidModeClaims.For(profileId, householdId));
        await live.CloseUserAsync(userId);
        return true;
    }

    public async Task<bool> ExitAsync(Guid userId)
    {
        if (await users.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return false;
        }

        await signIn.SignInAsync(user, isPersistent: true);
        await live.CloseUserAsync(userId);
        return true;
    }
}
