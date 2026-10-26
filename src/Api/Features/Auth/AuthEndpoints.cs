using System.Net.Mail;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Realtime;
using Foodplanner.Api.Security;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Email;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
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
            .ProducesValidationProblem()
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
        TimeProvider time,
        ILiveConnections live,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var address = ValidateEmail(request.Email, errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Always the same response, whether the address is new or taken, so register does not reveal who has an account.
        var accepted = TypedResults.Accepted((string?)null, new RegisterResponse("If this address can be registered, an email is on its way."));
        var existing = await users.FindByEmailAsync(address!);
        if (existing is null)
        {
            // No password: register only records the address (Q-011). Whoever opens the emailed link chooses it.
            var user = new AppUser { Id = Guid.NewGuid(), UserName = address, Email = address, RegisteredAt = time.GetUtcNow() };
            var created = await users.CreateAsync(user);
            if (created.Succeeded)
            {
                await SendSetPasswordLinkAsync(email, tokens, options.Value, user, ct);
            }

            // Otherwise a concurrent registration for the same address won the race: nothing to add, same response.
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
            return accepted;
        }

        // Unconfirmed: send a fresh link and kill the earlier ones by rotating the security stamp. This is safe now
        // because a link confirms nobody's chosen password: whoever opens it sets the password, and only the inbox
        // owner can open it. A registrant who never sees the inbox gets nothing from it.
        //
        // At most one fresh link per address per ResendCooldownMinutes, answered with the same 202: this bounds how
        // often anyone can kill the owner's link and how much mail one address gets. The registration time is
        // refreshed on every link that is sent (the cleanup window restarts): the owner can always register again,
        // so keeping an account alive gives a squatter nothing.
        var now = time.GetUtcNow();
        if (existing.RegisteredAt is { } last && last > now.AddMinutes(-options.Value.ResendCooldownMinutes))
        {
            return accepted;
        }

        // The registration time and the new security stamp go in one save, guarded by Identity's concurrency stamp:
        // the cleanup job cannot delete the account between two steps, and two parallel registers cannot both win.
        existing.RegisteredAt = now;
        var stamped = await users.UpdateSecurityStampAsync(existing);
        if (stamped.Succeeded)
        {
            // The stamp changed: close the user's live connections (Q-015).
            await live.CloseUserAsync(existing.Id);
            await SendSetPasswordLinkAsync(email, tokens, options.Value, existing, ct);
        }

        // Otherwise the account changed under us (a concurrent register or confirm won). The answer is still the same
        // 202; if the other request sent a link, that one is the usable one. Nothing is lost: the owner can register again.
        return accepted;
    }

    private static async Task<IResult> ConfirmEmail(
        ConfirmEmailRequest request,
        UserManager<AppUser> users,
        EmailConfirmationTokens tokens,
        IOptions<AuthOptions> options,
        ILiveConnections live)
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

        // A weak password is a validation problem and leaves the link usable, so the person can try again.
        var errors = new Dictionary<string, string[]>();
        ValidatePassword(request.Password, options.Value, errors);
        if (errors.Count == 0)
        {
            var passwordErrors = new List<string>();
            foreach (var validator in users.PasswordValidators)
            {
                var verdict = await validator.ValidateAsync(users, user, request.Password!);
                passwordErrors.AddRange(verdict.Errors.Select(e => e.Description));
            }

            if (passwordErrors.Count > 0)
            {
                errors["password"] = [.. passwordErrors];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // One save sets the password, confirms the address and rotates the stamp (which kills the link). The save is
        // guarded by Identity's concurrency stamp, so two submissions of one link cannot both succeed.
        user.PasswordHash = users.PasswordHasher.HashPassword(user, request.Password!);
        user.EmailConfirmed = true;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.RegisteredAt = null; // only meaningful while unconfirmed
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return invalid;
        }

        // The stamp rotated: sessions and live connections of the old one must end (Q-015).
        await live.CloseUserAsync(user.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        IPasswordHasher<AppUser> hasher,
        LoginEmailRateLimiter emailLimiter,
        IOptions<AuthOptions> options,
        TimeProvider time,
        ILogger<LoginRequest> logger)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length > MaxEmailLength)
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
            hasher.VerifyHashedPassword(new AppUser(), DummyPasswordHash.Get(hasher), request.Password!);
            return failed;
        }

        if (user.PasswordHash is null)
        {
            // Registered but never set a password (T-072): nothing to check, but cost the same as a real attempt.
            hasher.VerifyHashedPassword(user, DummyPasswordHash.Get(hasher), request.Password!);
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

            var saved = await users.UpdateAsync(user);
            if (!saved.Succeeded)
            {
                logger.LogWarning(
                    "Could not record a failed login attempt for user {UserId}: {Errors}",
                    user.Id,
                    string.Join(", ", saved.Errors.Select(e => e.Code)));
            }

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

    private static async Task<IResult> Logout(SignInManager<AppUser> signIn, ICurrentUser current, ILiveConnections live)
    {
        await signIn.SignOutAsync();

        // The hub checks the cookie only when a connection opens, so a signed-out browser would keep receiving signals
        // (Q-015, T-048). Connections do not record which cookie opened them, so ALL of this user's connections close,
        // including other devices'; those reconnect and, with a still valid cookie, simply rejoin.
        if (current is { IsAuthenticated: true, UserId: { } userId })
        {
            await live.CloseUserAsync(userId);
        }

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

    private static Task SendSetPasswordLinkAsync(IEmailSender email, EmailConfirmationTokens tokens, AuthOptions options, AppUser user, CancellationToken ct)
    {
        // The link carries the token, so it is only ever sent, never logged.
        var link = $"{options.PublicBaseUrl.TrimEnd('/')}/set-password?userId={user.Id}&token={Uri.EscapeDataString(tokens.Create(user))}";
        return email.SendAsync(
            user.Email!,
            "Set your Foodplanner password",
            $"Welcome to Foodplanner. Choose your password and confirm your email address with this link:\n{link}\n"
            + $"The link works for {options.ConfirmationTokenLifetimeHours} hours. If you did not register, ignore this email.",
            ct);
    }

    private sealed class RateLimitedResult(TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => RateLimitingSetup.WriteRateLimitedAsync(httpContext, retryAfter);
    }

    public sealed record AntiforgeryResponse(string Token);

    public sealed record RegisterRequest(string? Email);

    public sealed record RegisterResponse(string Message);

    public sealed record ConfirmEmailRequest(string? UserId, string? Token, string? Password);

    public sealed record LoginRequest(string? Email, string? Password);

    public sealed record MeResponse(Guid UserId, string Email);
}
