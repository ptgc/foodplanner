namespace Foodplanner.Domain.Users;

/// <summary>
/// Who is calling. The only way feature code learns the caller's identity (ADR 0004); handlers outside
/// <c>Features/Auth</c> never touch <c>ClaimsPrincipal</c>, <c>UserManager</c> or <c>SignInManager</c>.
/// A user is a global identity, not a tenant entity.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string? Email { get; }

    /// <summary>
    /// Kid mode (T-026): the profile this device's session is locked to, or null. Set by the server only, in the auth cookie
    /// (claims). A stale id (the profile was deleted) still means kid mode.
    /// </summary>
    Guid? ActiveProfileId { get; }

    /// <summary>The household the active profile belongs to (the one it was activated in).</summary>
    Guid? ActiveProfileHouseholdId { get; }

    /// <summary>True while the session is locked to a profile. Read by policy code and the household resolution only.</summary>
    bool IsKidMode { get; }
}
