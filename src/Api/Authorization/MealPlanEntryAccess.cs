using System.Security.Claims;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Domain.Users;
using Microsoft.AspNetCore.Authorization;

namespace Foodplanner.Api.Authorization;

/// <summary>
/// Resource-based requirement (CLAUDE.md rule 2): the caller may change a <see cref="MealPlanEntry"/> when they are an
/// Adult or Owner of the household, or when they picked the entry themselves (a Child may only touch their own picks). In kid
/// mode (T-026) only the entries picked by the ACTIVE profile qualify, so siblings sharing one account cannot edit each
/// other's picks and a kid cannot touch an entry a parent made.
/// </summary>
public sealed class MealPlanEntryOwnerOrAdultRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Policy code, so it may read <see cref="ICurrentHousehold.Role"/> and the kid claims (architecture test
/// No_role_checks_outside_policies only scans feature code). No membership means no role, so the requirement fails.
/// </summary>
internal sealed class MealPlanEntryOwnerOrAdultHandler(ICurrentHousehold currentHousehold, ICurrentUser currentUser)
    : AuthorizationHandler<MealPlanEntryOwnerOrAdultRequirement, MealPlanEntry>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MealPlanEntryOwnerOrAdultRequirement requirement, MealPlanEntry entry)
    {
        if (currentHousehold.Role is not { } role)
        {
            return Task.CompletedTask;
        }

        if (currentUser.IsKidMode)
        {
            if (currentUser.ActiveProfileId is { } profileId && entry.PickedByProfileId == profileId)
            {
                context.Succeed(requirement);
            }
        }
        else if (role.Satisfies(HouseholdRole.Adult) || (currentUser.UserId is { } userId && userId == entry.PickedByUserId))
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

/// <summary>
/// Resource-based requirement (T-026): the caller may write to the week plan. Adult and Owner always; any other effective
/// role (a Child member, or any role in kid mode) only while the week is not locked. Evaluated with the plan as resource for
/// every plan write (add, change and delete an entry).
/// </summary>
public sealed class MealPlanWeekWritableRequirement : IAuthorizationRequirement
{
}

/// <summary>The only reader of <see cref="MealPlan.LockedAt"/> for authorization (policy code).</summary>
internal sealed class MealPlanWeekWritableHandler(ICurrentHousehold currentHousehold)
    : AuthorizationHandler<MealPlanWeekWritableRequirement, MealPlan>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MealPlanWeekWritableRequirement requirement, MealPlan plan)
    {
        if (currentHousehold.Role is { } role && (role.Satisfies(HouseholdRole.Adult) || !plan.IsLocked))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>What feature code calls to ask whether the week plan may be written. Answered 409 week-locked when false.</summary>
public interface IMealPlanWeekAccess
{
    Task<bool> CanWriteAsync(MealPlan plan);
}

internal sealed class MealPlanWeekAccess(IAuthorizationService authorization, IHttpContextAccessor accessor) : IMealPlanWeekAccess
{
    public async Task<bool> CanWriteAsync(MealPlan plan) =>
        accessor.HttpContext?.User is { } user
        && (await authorization.AuthorizeAsync(user, plan, Policies.MealPlanWeekWritable)).Succeeded;
}
