using System.Globalization;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Foodplanner.Api.Features.Auth;

/// <summary>
/// Set or change the PIN. <c>CurrentPin</c> or <c>Password</c> is needed only to REPLACE an existing PIN; setting the first
/// one needs nothing more than the full session.
/// </summary>
public sealed record SetPinRequest(string? Pin, string? CurrentPin, string? Password);

/// <summary>Return from kid mode to the full session.</summary>
public sealed record UnlockRequest(string? Pin);

/// <summary>
/// The PIN endpoints (T-026): <c>PUT /api/auth/pin</c> and <c>POST /api/auth/pin/unlock</c>. Both are per account, rate
/// limited per client IP and user (<see cref="RateLimitPolicies.PinUnlock"/>), and every wrong answer is counted against the
/// account's lock (<see cref="PinService"/>).
/// </summary>
public static class PinEndpoints
{
    public static IEndpointRouteBuilder MapPinEndpoints(this IEndpointRouteBuilder auth)
    {
        auth.MapPut("/pin", SetPin)
            .WithName("SetPin")
            .RequireAuthorization(Policies.FullSession)
            .RequireRateLimiting(RateLimitPolicies.PinUnlock)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces<PinIncorrectProblem>(StatusCodes.Status403Forbidden, "application/problem+json")
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/pin/unlock", Unlock)
            .WithName("UnlockWithPin")
            .RequireRateLimiting(RateLimitPolicies.PinUnlock)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces<PinIncorrectProblem>(StatusCodes.Status403Forbidden, "application/problem+json")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return auth;
    }

    private static async Task<IResult> SetPin(
        SetPinRequest? request,
        ICurrentUser current,
        PinService pins,
        UserManager<AppUser> users,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (current.UserId is not { } userId)
        {
            return Unauthenticated();
        }

        if (ValidatePin(request?.Pin) is { } invalid)
        {
            return invalid;
        }

        if (await pins.HasPinAsync(userId, cancellationToken))
        {
            // Replacing: prove the right to, with the old PIN or the account password. A wrong proof is counted like a wrong unlock.
            var currentPin = request!.CurrentPin;
            var password = request.Password;
            if (string.IsNullOrEmpty(currentPin) && string.IsNullOrEmpty(password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPin"] = ["The current PIN or the account password is required to change the PIN."],
                });
            }

            var user = await users.FindByIdAsync(userId.ToString());
            var proof = await pins.AttemptAsync(
                userId,
                async pin => !string.IsNullOrEmpty(currentPin)
                    ? currentPin.Length == PinRules.Length && pins.Matches(pin, currentPin)
                    : user is { PasswordHash: not null } && password!.Length <= 128 && await users.CheckPasswordAsync(user, password),
                cancellationToken);
            if (proof.Outcome != PinOutcome.Ok)
            {
                return Refusal(proof, time);
            }
        }

        await pins.SetAsync(userId, request!.Pin!, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> Unlock(
        UnlockRequest? request,
        ICurrentUser current,
        PinService pins,
        IKidModeSession session,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (current.UserId is not { } userId)
        {
            return Unauthenticated();
        }

        // First: a full session has nothing to unlock. Nothing is hashed or counted, so this is no PIN oracle.
        if (!current.IsKidMode)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict, AppProblems.NotInKidMode, "Not in kid mode", "This session is not locked to a profile.");
        }

        var candidate = request?.Pin;
        if (candidate is null || candidate.Length != PinRules.Length || !candidate.All(char.IsAsciiDigit))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["pin"] = ["The PIN is 4 digits."] });
        }

        var result = await pins.AttemptAsync(userId, pin => Task.FromResult(pins.Matches(pin, candidate)), cancellationToken);
        if (result.Outcome != PinOutcome.Ok)
        {
            return Refusal(result, time);
        }

        return await session.ExitAsync(userId) ? Results.NoContent() : Unauthenticated();
    }

    private static IResult? ValidatePin(string? pin) => PinRules.Check(pin) switch
    {
        PinProblem.None => null,
        PinProblem.TooWeak => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["pin"] = ["That PIN is too easy to guess. Do not use the same digit four times or a straight run like 1234."],
        }),
        _ => Results.ValidationProblem(new Dictionary<string, string[]> { ["pin"] = ["The PIN is exactly 4 digits."] }),
    };

    private static IResult Refusal(PinCheck result, TimeProvider time) => result.Outcome switch
    {
        PinOutcome.NotSet => AppProblems.Problem(
            StatusCodes.Status409Conflict, AppProblems.PinNotSet, "No PIN set", "This account has no PIN yet. Set one first."),
        PinOutcome.Locked => new PinLockedResult(
            result.LockedUntil is { } until ? until - time.GetUtcNow() : TimeSpan.FromMinutes(1)),
        _ => AppProblems.Problem(
            StatusCodes.Status403Forbidden,
            AppProblems.PinIncorrect,
            "The PIN is wrong",
            null,
            new Dictionary<string, object?> { ["attemptsRemaining"] = result.AttemptsRemaining }),
    };

    private static IResult Unauthenticated() =>
        AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");

    /// <summary>429 <c>pin-locked</c> with Retry-After: the account's PIN is locked after too many wrong attempts.</summary>
    private sealed class PinLockedResult(TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return AppProblems.WriteAsync(
                httpContext,
                StatusCodes.Status429TooManyRequests,
                AppProblems.PinLocked,
                "The PIN is locked",
                $"Too many wrong attempts. Try again in {seconds} seconds.");
        }
    }
}
