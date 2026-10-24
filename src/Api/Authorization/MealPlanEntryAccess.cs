using System.Security.Claims;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Domain.Users;
using Microsoft.AspNetCore.Authorization;

namespace Foodplanner.Api.Authorization;

/// <summary>
/// Resource-based requirement (CLAUDE.md rule 2): the caller may change a <see cref="MealPlanEntry"/> when they are an
/// Adult or Owner of the household, or when they picked the entry themselves (a Child may only touch their own picks).
/// </summary>
public sealed class MealPlanEntryOwnerOrAdultRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Policy code, so it may read <see cref="ICurrentHousehold.Role"/> (architecture test No_role_checks_outside_policies
/// only scans feature code). No membership means no role, so the requirement fails.
/// </summary>
internal sealed class MealPlanEntryOwnerOrAdultHandler(ICurrentHousehold currentHousehold, ICurrentUser currentUser)
    : AuthorizationHandler<MealPlanEntryOwnerOrAdultRequirement, MealPlanEntry>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MealPlanEntryOwnerOrAdultRequirement requirement, MealPlanEntry entry)
    {
        if (currentHousehold.Role is { } role
            && (role.Satisfies(HouseholdRole.Adult) || (currentUser.UserId is { } userId && userId == entry.PickedByUserId)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// What feature code calls to ask the policy about an entry. It exists so endpoints never touch
/// <see cref="ClaimsPrincipal"/> (architecture test Handlers_use_ICurrentUser_only).
/// </summary>
public interface IMealPlanEntryAccess
{
    /// <summary>True when the caller may update or delete <paramref name="entry"/>.</summary>
    Task<bool> CanChangeAsync(MealPlanEntry entry);
}

internal sealed class MealPlanEntryAccess(IAuthorizationService authorization, IHttpContextAccessor accessor) : IMealPlanEntryAccess
{
    public async Task<bool> CanChangeAsync(MealPlanEntry entry) =>
        accessor.HttpContext?.User is { } user
        && (await authorization.AuthorizeAsync(user, entry, Policies.MealPlanEntryOwnerOrAdult)).Succeeded;
}
