using System.Security.Claims;
using Foodplanner.Domain.Users;

namespace Foodplanner.Api.Features.Auth;

/// <summary>The only place outside the auth endpoints that reads the authenticated principal.</summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? Email => IsAuthenticated ? Principal!.FindFirstValue(ClaimTypes.Email) : null;
}
