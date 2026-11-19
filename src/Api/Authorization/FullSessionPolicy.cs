using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Foodplanner.Api.Authorization;

/// <summary>
/// Satisfied only when the session is not in kid mode (T-026). It guards the account-level actions that are not under a
/// household role policy (create a household, accept an invite, leave a household, set the PIN), so a device locked to a
/// kid profile cannot do them. T-105 also puts it on the shopping list writes (generate, weekly groceries, manual items, check
/// and uncheck), the chosen-store write and the pantry writes: kid mode may look at the list, never change it.
/// </summary>
public sealed class FullSessionRequirement : IAuthorizationRequirement
{
}

/// <summary>Policy code: the one reader of the kid-mode state for this requirement.</summary>
internal sealed class FullSessionHandler(ICurrentUser currentUser) : AuthorizationHandler<FullSessionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, FullSessionRequirement requirement)
    {
        if (!currentUser.IsKidMode)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Answers a failed <see cref="FullSessionRequirement"/> with 403 ProblemDetails <c>urn:foodplanner:problem:kid-mode</c>
/// (so the client can tell "this device is locked to a profile" from a plain forbidden); everything else is the default.
/// </summary>
internal sealed class KidModeAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<FullSessionRequirement>().Any() == true
            && !IsUnreachableHousehold(context))
        {
            return AppProblems.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                AppProblems.KidMode,
                "Not available in kid mode",
                "This device is locked to a kid profile. Unlock it with the PIN first.");
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>
    /// A household-scoped call where no membership was resolved (not a member, or kid mode on another household, T-103)
    /// keeps the plain non-member answer, so "kid-mode" never tells which households exist.
    /// </summary>
    private static bool IsUnreachableHousehold(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<HouseholdScopedMetadata>() is not null
        && context.RequestServices.GetRequiredService<ICurrentHousehold>().HouseholdId is null;
}
