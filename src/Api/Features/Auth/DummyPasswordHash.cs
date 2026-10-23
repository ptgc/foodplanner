using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Foodplanner.Api.Features.Auth;

/// <summary>
/// A fixed hash (random password nobody knows, created once per process from the configured hasher) that an
/// unknown-email login verifies against, so it costs about as much as a real verification.
/// </summary>
internal static class DummyPasswordHash
{
    private static string? _value;

    public static string Get(IPasswordHasher<AppUser> hasher) =>
        _value ??= hasher.HashPassword(new AppUser(), Convert.ToHexString(Guid.NewGuid().ToByteArray()));
}
