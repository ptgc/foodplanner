using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Foodplanner.Api.Features.Auth;

/// <summary>
/// A fixed hash (random password nobody knows, created once per process) that an unknown-email login verifies against,
/// so it costs about as much as a real verification.
/// </summary>
internal static class DummyPasswordHash
{
    private static readonly Lazy<string> Value = new(() =>
        new PasswordHasher<AppUser>().HashPassword(new AppUser(), Convert.ToHexString(Guid.NewGuid().ToByteArray())));

    public static string Get() => Value.Value;
}
