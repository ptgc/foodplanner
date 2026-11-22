using Foodplanner.Api.Authorization;
using Foodplanner.Api.Support;
using Foodplanner.Domain.Support;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Import.Ai;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Support;

/// <summary>One limit as the admin page shows and sends it: <c>Unlimited</c>, or a count in <c>Limit</c> (0 to 10000; 0 means none allowed).</summary>
public sealed record SupportPlanLimit(bool Unlimited, int? Limit);

/// <summary>The configured default plan (T-123), for "default: 2" beside a custom limit.</summary>
public sealed record SupportPlanDefaults(SupportPlanLimit PhotoImportsPerDay, SupportPlanLimit LineHelpPerDay, SupportPlanLimit LineHelpPerMonth);

/// <summary><c>Source</c> is <c>Default</c> (no row; the limits are the defaults) or <c>Custom</c>.</summary>
public sealed record SupportPlanResponse(
    string Source,
    string? Name,
    SupportPlanLimit PhotoImportsPerDay,
    SupportPlanLimit LineHelpPerDay,
    SupportPlanLimit LineHelpPerMonth,
    SupportPlanDefaults Defaults);

/// <summary>PUT replaces the whole plan: all three limits are required and explicit, an omitted one is a 400, never "unchanged".</summary>
public sealed record SupportPlanRequest(
    string? Name, SupportPlanLimit? PhotoImportsPerDay, SupportPlanLimit? LineHelpPerDay, SupportPlanLimit? LineHelpPerMonth);

/// <summary>
/// View, set and reset a household's plan (T-123): the Claude limits. Support only, in the session household (never the route id),
/// audited like every other support action. There is no household-facing endpoint that reads the details or changes a plan.
/// </summary>
public static class SupportPlanEndpoints
{
    public static RouteGroupBuilder MapSupportPlanEndpoints(this RouteGroupBuilder household)
    {
        household.MapGet("/plan", GetPlan)
            .WithMetadata(new SupportAuditedMetadata(SupportActions.ViewPlan, SupportActionKind.View))
            .WithName("GetSupportHouseholdPlan")
            .Produces<SupportPlanResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapPut("/plan", SetPlan)
            .RequireAuthorization(Policies.SupportSessionWrite)
            .WithMetadata(new SupportAuditedMetadata(SupportActions.PlanSet, SupportActionKind.Write))
            .WithName("SetSupportHouseholdPlan")
            .Produces<SupportPlanResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        household.MapDelete("/plan", ResetPlan)
            .RequireAuthorization(Policies.SupportSessionWrite)
            .WithMetadata(new SupportAuditedMetadata(SupportActions.PlanReset, SupportActionKind.Write))
            .WithName("ResetSupportHouseholdPlan")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return household;
    }

    private static async Task<IResult> GetPlan(
        AppDbContext db, IOptions<AiImportOptions> lineHelp, IOptions<AiPhotoImportOptions> photo, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var plan = await db.HouseholdPlans.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var response = ToResponse(plan, HouseholdPlanDefaults.From(lineHelp.Value, photo.Value));
        audit.Detail("source", response.Source);
        return Results.Ok(response);
    }

    private static async Task<IResult> SetPlan(
        SupportPlanRequest? request, AppDbContext db, IOptions<AiImportOptions> lineHelp, IOptions<AiPhotoImportOptions> photo,
        SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = ValidateName(request?.Name, errors);
        var limits = new HouseholdPlanLimits(
            ParseLimit(request?.PhotoImportsPerDay, "photoImportsPerDay", errors),
            ParseLimit(request?.LineHelpPerDay, "lineHelpPerDay", errors),
            ParseLimit(request?.LineHelpPerMonth, "lineHelpPerMonth", errors));
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var defaults = HouseholdPlanDefaults.From(lineHelp.Value, photo.Value);
        // Through the tenant filter: only the session household's row can be found, and the write guard refuses any other household id.
        var plan = await db.HouseholdPlans.FirstOrDefaultAsync(cancellationToken);
        var before = HouseholdPlanLimits.Resolve(plan, defaults);
        var hadPlan = plan is not null;
        if (plan is null)
        {
            plan = new HouseholdPlan(name, limits);
            db.HouseholdPlans.Add(plan);
        }
        else
        {
            plan.Replace(name, limits);
        }

        // Numbers only (the "ids and counts" rule): the label's text is never stored, only its length.
        audit.Detail("previousSource", hadPlan ? "Custom" : "Default");
        AddLimitDetail(audit, "photoImportsPerDay", before.PhotoImportsPerDay, limits.PhotoImportsPerDay);
        AddLimitDetail(audit, "lineHelpPerDay", before.LineHelpPerDay, limits.LineHelpPerDay);
        AddLimitDetail(audit, "lineHelpPerMonth", before.LineHelpPerMonth, limits.LineHelpPerMonth);
        audit.Detail("nameLength", name?.Length ?? 0);

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(plan, defaults));
    }

    private static async Task<IResult> ResetPlan(AppDbContext db, SupportAuditScope audit, CancellationToken cancellationToken)
    {
        var plan = await db.HouseholdPlans.FirstOrDefaultAsync(cancellationToken);
        audit.Detail("hadPlan", plan is not null);
        if (plan is not null)
        {
            db.HouseholdPlans.Remove(plan);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    private static string? ValidateName(string? name, Dictionary<string, string[]> errors)
    {
        if (HouseholdPlan.TryNormalizeName(name, out var normalized))
        {
            return normalized;
        }

        errors["name"] = [$"The label is at most {HouseholdPlan.MaxNameLength} characters, with no control characters."];
        return null;
    }

    private static AiLimit ParseLimit(SupportPlanLimit? limit, string field, Dictionary<string, string[]> errors)
    {
        if (limit is null)
        {
            errors[field] = ["The limit is required: unlimited, or a number."];
            return default;
        }

        if (limit.Unlimited)
        {
            if (limit.Limit is not null)
            {
                errors[field] = ["An unlimited limit has no number."];
            }

            return AiLimit.Unlimited;
        }

        if (limit.Limit is not { } count || !AiLimit.IsValidCount(count))
        {
            errors[field] = [$"The limit must be a number from 0 to {AiLimit.MaxCount}."];
            return default;
        }

        return AiLimit.Of(count);
    }

    private static void AddLimitDetail(SupportAuditScope audit, string field, AiLimit before, AiLimit after)
    {
        audit.Detail(field + "Before", Describe(before));
        audit.Detail(field + "After", Describe(after));
    }

    private static object Describe(AiLimit limit) => limit.Count is { } count ? count : "unlimited";

    private static SupportPlanLimit ToDto(AiLimit limit) => new(limit.IsUnlimited, limit.Count);

    private static SupportPlanResponse ToResponse(HouseholdPlan? plan, HouseholdPlanLimits defaults)
    {
        var limits = HouseholdPlanLimits.Resolve(plan, defaults);
        return new SupportPlanResponse(
            plan is null ? "Default" : "Custom",
            plan?.Name,
            ToDto(limits.PhotoImportsPerDay),
            ToDto(limits.LineHelpPerDay),
            ToDto(limits.LineHelpPerMonth),
            new SupportPlanDefaults(ToDto(defaults.PhotoImportsPerDay), ToDto(defaults.LineHelpPerDay), ToDto(defaults.LineHelpPerMonth)));
    }
}
