using Foodplanner.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace Foodplanner.Api.Authorization;

/// <summary>
/// Named authorization policies (CLAUDE.md rule 2). Endpoints attach them with <c>RequireAuthorization(Policies.X)</c>
/// on a group under <c>/api/households/{householdId}</c>; no endpoint compares roles itself. Roles are hierarchical:
/// Owner can do what Adult can, and Adult what Child can.
/// </summary>
public static class Policies
{
    /// <summary>Any role in the household of the route.</summary>
    public const string HouseholdMember = "HouseholdMember";

    /// <summary>Owner or Adult.</summary>
    public const string HouseholdAdult = "HouseholdAdult";

    /// <summary>Owner.</summary>
    public const string HouseholdOwner = "HouseholdOwner";

    /// <summary>Resource policy (T-030): Adult or Owner, or the member who picked the meal plan entry. Evaluated with the entry as resource.</summary>
    public const string MealPlanEntryOwnerOrAdult = "MealPlanEntryOwnerOrAdult";

    /// <summary>Resource policy (T-026): Adult or Owner, or any other role while the week is not locked. Evaluated with the plan as resource.</summary>
    public const string MealPlanWeekWritable = "MealPlanWeekWritable";

    /// <summary>The session is not locked to a kid profile (T-026). For account-level actions that are not under a household role policy.</summary>
    public const string FullSession = "FullSession";
}

/// <summary>Satisfied when the caller's role in the current household is at least <see cref="Minimum"/>.</summary>
public sealed class HouseholdRoleRequirement(HouseholdRole minimum) : IAuthorizationRequirement
{
    public HouseholdRole Minimum { get; } = minimum;
}

/// <summary>
/// The only reader of <see cref="ICurrentHousehold.Role"/> outside the implementation (architecture test
/// No_role_checks_outside_policies). No membership means no role, so the requirement fails.
/// </summary>
internal sealed class HouseholdRoleHandler(ICurrentHousehold currentHousehold) : AuthorizationHandler<HouseholdRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, HouseholdRoleRequirement requirement)
    {
        if (currentHousehold.Role is { } role && role.Satisfies(requirement.Minimum))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class AuthorizationSetup
{
    public static IServiceCollection AddHouseholdPolicies(this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, HouseholdRoleHandler>();
        services.AddScoped<IAuthorizationHandler, MealPlanEntryOwnerOrAdultHandler>();
        services.AddScoped<IAuthorizationHandler, MealPlanWeekWritableHandler>();
        services.AddScoped<IAuthorizationHandler, FullSessionHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, KidModeAuthorizationResultHandler>();
        services.AddScoped<IMealPlanWeekAccess, MealPlanWeekAccess>();
        services.AddScoped<IMealPlanEntryAccess, MealPlanEntryAccess>();
        services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(
                Policies.MealPlanEntryOwnerOrAdult,
                new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new MealPlanEntryOwnerOrAdultRequirement()).Build());
            authorization.AddPolicy(
                Policies.MealPlanWeekWritable,
                new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new MealPlanWeekWritableRequirement()).Build());
            authorization.AddPolicy(
                Policies.FullSession,
                new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new FullSessionRequirement()).Build());
            authorization.AddPolicy(Policies.HouseholdMember, Build(HouseholdRole.Child));
            authorization.AddPolicy(Policies.HouseholdAdult, Build(HouseholdRole.Adult));
            authorization.AddPolicy(Policies.HouseholdOwner, Build(HouseholdRole.Owner));
        });
        return services;
    }

    private static AuthorizationPolicy Build(HouseholdRole minimum) =>
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new HouseholdRoleRequirement(minimum)).Build();
}
