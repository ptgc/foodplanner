using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Auth;

public enum PasswordProofOutcome
{
    /// <summary>The password is right; the failure counter is cleared.</summary>
    Ok,

    /// <summary>Wrong; the attempt was counted (and may have locked the account).</summary>
    Wrong,

    /// <summary>The account is locked out; the answer is "locked" even for a right password.</summary>
    Locked,

    /// <summary>The account has no password to check (it cannot sign in with one).</summary>
    NoPassword,
}

public readonly record struct PasswordProofResult(PasswordProofOutcome Outcome, TimeSpan RetryAfter = default);

/// <summary>
/// Re-entering the password for a dangerous action (T-095: delete the account). There is no "recent sign-in" notion in the
/// auth cookie, so the action asks for the password again. It uses exactly the sign-in lockout: the same
/// <c>AccessFailedCount</c> / <c>LockoutEnd</c> on the user, the same <see cref="AuthOptions"/> limits, and a locked account
/// refuses even the right password. So guessing the password through this endpoint costs the same as through login, and the
/// two share their budget. Mirrors the checks in the login handler.
/// </summary>
public sealed class PasswordProof(UserManager<AppUser> users, IOptions<AuthOptions> options, TimeProvider time)
{
    public async Task<PasswordProofResult> CheckAsync(AppUser user, string password)
    {
        if (user.PasswordHash is null)
        {
            return new PasswordProofResult(PasswordProofOutcome.NoPassword);
        }

        var now = time.GetUtcNow();
        var settings = options.Value;
        var locked = user.LockoutEnd is { } lockoutEnd && lockoutEnd > now;
        var passwordOk = await users.CheckPasswordAsync(user, password);
        if (locked)
        {
            return new PasswordProofResult(PasswordProofOutcome.Locked, user.LockoutEnd!.Value - now);
        }

        if (!passwordOk)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= settings.LockoutMaxFailedAttempts)
            {
                user.LockoutEnd = now.AddMinutes(settings.LockoutMinutes);
                user.AccessFailedCount = 0;
            }

            await users.UpdateAsync(user);
            return new PasswordProofResult(PasswordProofOutcome.Wrong);
        }

        if (user.AccessFailedCount != 0 || user.LockoutEnd is not null)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            await users.UpdateAsync(user);
        }

        return new PasswordProofResult(PasswordProofOutcome.Ok);
    }
}
