using System.Globalization;
using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Auth;

/// <summary>
/// Stateless, time-limited confirmation tokens. The token is a protected payload of user id, expiry, security stamp
/// and email, so it dies when the address or stamp changes, and <see cref="Validate"/> also refuses once the email is
/// confirmed (single use). Expiry is checked against <see cref="TimeProvider"/> so tests can move the clock.
/// </summary>
internal sealed class EmailConfirmationTokens(IDataProtectionProvider provider, TimeProvider time, IOptions<AuthOptions> options)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Foodplanner.EmailConfirmation.v1");

    public string Create(AppUser user)
    {
        var expires = time.GetUtcNow().AddHours(options.Value.ConfirmationTokenLifetimeHours).ToUnixTimeSeconds();
        return _protector.Protect(
            string.Create(CultureInfo.InvariantCulture, $"{user.Id:N}|{expires}|{user.SecurityStamp}|{user.Email}"));
    }

    public bool Validate(AppUser user, string token)
    {
        if (user.EmailConfirmed)
        {
            return false;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return false;
        }

        var parts = payload.Split('|', 4);
        return parts.Length == 4
            && parts[0] == user.Id.ToString("N")
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            && time.GetUtcNow().ToUnixTimeSeconds() <= expires
            && parts[2] == user.SecurityStamp
            && string.Equals(parts[3], user.Email, StringComparison.Ordinal);
    }
}
