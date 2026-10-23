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
}
