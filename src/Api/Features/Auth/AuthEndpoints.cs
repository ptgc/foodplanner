using System.Net.Mail;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Email;
using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Auth;

public static class AuthEndpoints
{
    private const int MaxPasswordLength = 128;
    private const int MaxEmailLength = 256;

    // The same title and detail for every login failure, so the response says nothing about the account.
    private const string LoginFailedTitle = "Login failed";
    private const string LoginFailedDetail = "The email or password is wrong, or the account cannot sign in.";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapGet("/antiforgery", GetAntiforgery)
            .WithName("GetAntiforgeryToken")
            .Produces<AntiforgeryResponse>()
            .AllowAnonymous();

        auth.MapPost("/register", Register)
            .WithName("Register")
            .Produces<RegisterResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Auth);

        auth.MapPost("/confirm-email", ConfirmEmail)
            .WithName("ConfirmEmail")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Auth);

        auth.MapPost("/login", Login)
            .WithName("Login")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Auth);

        auth.MapPost("/logout", Logout)
            .WithName("Logout")
            .Produces(StatusCodes.Status204NoContent);

        auth.MapGet("/me", GetMe)
            .WithName("GetMe")
            .Produces<MeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return api;
    }

    private static IResult GetAntiforgery(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        return Results.Ok(new AntiforgeryResponse(tokens.RequestToken ?? string.Empty));
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        UserManager<AppUser> users,
        IEmailSender email,
        EmailConfirmationTokens tokens,
        IOptions<AuthOptions> options,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var address = ValidateEmail(request.Email, errors);
        ValidatePassword(request.Password, options.Value, errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Always the same response, whether the address is new or taken, so register does not reveal who has an account.
        var accepted = TypedResults.Accepted((string?)null, new RegisterResponse("If this address can be registered, an email is on its way."));
        var existing = await users.FindByEmailAsync(address!);
        if (existing is null)
        {
            var user = new AppUser { Id = Guid.NewGuid(), UserName = address, Email = address };
            var created = await users.CreateAsync(user, request.Password!);
            if (created.Succeeded)
            {
                await SendConfirmationAsync(email, tokens, options.Value, user, ct);
                return accepted;
            }

            var passwordErrors = created.Errors.Where(e => e.Code.StartsWith("Password", StringComparison.Ordinal)).Select(e => e.Description).ToArray();
            if (passwordErrors.Length > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = passwordErrors });
            }

            // A concurrent registration for the same address won the race: nothing to add, same response.
            return accepted;
        }

        if (existing.EmailConfirmed)
        {
            await email.SendAsync(
                existing.Email!,
                "You already have a Foodplanner account",
                $"Someone tried to register this address, but you already have an account. Sign in at {options.Value.PublicBaseUrl}/login.\n"
                + "If this was not you, you can ignore this email.",
                ct);
        }
        else
        {
            // Registered but never confirmed (for example the link expired): send a fresh link to the address owner.
            await SendConfirmationAsync(email, tokens, options.Value, existing, ct);
        }

        return accepted;
    }

    private static async Task<IResult> ConfirmEmail(
        ConfirmEmailRequest request,
        UserManager<AppUser> users,
        EmailConfirmationTokens tokens)
    {
        var invalid = AppProblems.Problem(
            StatusCodes.Status400BadRequest,
            AppProblems.ConfirmationInvalid,
            "Confirmation link is invalid",
            "The link is invalid, expired or already used.");
        if (string.IsNullOrWhiteSpace(request.Token) || !Guid.TryParse(request.UserId, out var id))
        {
            return invalid;
        }

        var user = await users.FindByIdAsync(id.ToString());
        if (user is null || !tokens.Validate(user, request.Token))
        {
            return invalid;
        }

        user.EmailConfirmed = true;
        var result = await users.UpdateAsync(user);
        return result.Succeeded ? Results.NoContent() : invalid;
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        IPasswordHasher<AppUser> hasher,
        LoginEmailRateLimiter emailLimiter,
        IOptions<AuthOptions> options,
        TimeProvider time)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            errors["email"] = ["Email is required."];
        }

        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > MaxPasswordLength)
        {
            errors["password"] = ["Password is required."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var emailAddress = request.Email!.Trim();
        if (!emailLimiter.TryAcquire(emailAddress.ToUpperInvariant(), out var retryAfter))
        {
            return new RateLimitedResult(retryAfter);
        }

        var failed = AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.LoginFailed, LoginFailedTitle, LoginFailedDetail);
        var user = await users.FindByEmailAsync(emailAddress);
        if (user is null)
        {
            // Still pay for one password-hash verification, to reduce the timing difference to a known account.
            hasher.VerifyHashedPassword(new AppUser(), DummyPasswordHash.Get(), request.Password!);
            return failed;
        }

        var now = time.GetUtcNow();
        var locked = user.LockoutEnd is { } lockoutEnd && lockoutEnd > now;
        var passwordOk = await users.CheckPasswordAsync(user, request.Password!);
        if (locked)
        {
            return failed;
        }

        var settings = options.Value;
        if (!passwordOk)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= settings.LockoutMaxFailedAttempts)
            {
                user.LockoutEnd = now.AddMinutes(settings.LockoutMinutes);
                user.AccessFailedCount = 0;
            }

            await users.UpdateAsync(user);
            return failed;
        }

        if (!user.EmailConfirmed)
        {
            return failed;
        }

        if (user.AccessFailedCount != 0 || user.LockoutEnd is not null)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            await users.UpdateAsync(user);
        }

        await signIn.SignInAsync(user, isPersistent: true);
        return Results.NoContent();
    }

    private static async Task<IResult> Logout(SignInManager<AppUser> signIn)
    {
        await signIn.SignOutAsync();
        return Results.NoContent();
    }

    private static IResult GetMe(ICurrentUser current) =>
        current is { IsAuthenticated: true, UserId: { } id, Email: { } email }
            ? Results.Ok(new MeResponse(id, email))
            : AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");

    private static string? ValidateEmail(string? value, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxEmailLength
            || !MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed || !trimmed.Contains('@', StringComparison.Ordinal))
        {
            errors["email"] = ["Enter a valid email address."];
            return null;
        }

        return trimmed;
    }

    private static void ValidatePassword(string? password, AuthOptions options, Dictionary<string, string[]> errors)
    {
        if (password is null || password.Length < options.MinPasswordLength)
        {
            errors["password"] = [$"Password must be at least {options.MinPasswordLength} characters."];
        }
        else if (password.Length > MaxPasswordLength)
        {
            errors["password"] = [$"Password must be at most {MaxPasswordLength} characters."];
        }
    }

    private static Task SendConfirmationAsync(IEmailSender email, EmailConfirmationTokens tokens, AuthOptions options, AppUser user, CancellationToken ct)
    {
        // The link carries the token, so it is only ever sent, never logged.
        var link = $"{options.PublicBaseUrl.TrimEnd('/')}/confirm-email?userId={user.Id}&token={Uri.EscapeDataString(tokens.Create(user))}";
        return email.SendAsync(
            user.Email!,
            "Confirm your Foodplanner email",
            $"Welcome to Foodplanner. Confirm your email address with this link:\n{link}\n"
            + $"The link works for {options.ConfirmationTokenLifetimeHours} hours. If you did not register, ignore this email.",
            ct);
    }

    private sealed class RateLimitedResult(TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => RateLimitingSetup.WriteRateLimitedAsync(httpContext, retryAfter);
    }

    public sealed record AntiforgeryResponse(string Token);

    public sealed record RegisterRequest(string? Email, string? Password);

    public sealed record RegisterResponse(string Message);

    public sealed record ConfirmEmailRequest(string? UserId, string? Token);

    public sealed record LoginRequest(string? Email, string? Password);

    public sealed record MeResponse(Guid UserId, string Email);
}
