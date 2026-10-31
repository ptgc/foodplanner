using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Auth;

public static class AuthSetup
{
    public const string AuthCookieName = "__Host-foodplanner";

    /// <summary>
    /// The Identity security stamp check, plus: kid mode survives it (T-026). When the stamp interval has passed, Identity
    /// rebuilds the principal from the claims factory, which knows nothing about the kid claims; without this the refresh
    /// would silently unlock the device. A stale claim (its profile was deleted) is copied too: the device stays locked.
    /// </summary>
    private static async Task KeepKidModeAsync(CookieValidatePrincipalContext context)
    {
        var before = context.Principal;
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        if (before is not null && context.Principal is { } after && !ReferenceEquals(before, after))
        {
            KidModeClaims.CopyTo(before, after);
        }
    }

    public static IServiceCollection AddFoodplannerAuth(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddHttpContextAccessor();
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section);
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.AddScoped<EmailConfirmationTokens>();
        services.AddScoped<PinService>();
        services.AddScoped<IKidModeSession, KidModeSession>();
        services.AddHostedService<UnconfirmedAccountCleanup>();

        // Roles are per membership (T-011), so Identity's role tables and services are not added.
        services.AddIdentityCore<AppUser>(identity =>
            {
                identity.User.RequireUniqueEmail = true;
                // Emails are the user names and may contain characters outside Identity's default list.
                identity.User.AllowedUserNameCharacters = string.Empty;
                identity.Password.RequireDigit = false;
                identity.Password.RequireLowercase = false;
                identity.Password.RequireUppercase = false;
                identity.Password.RequireNonAlphanumeric = false;
                identity.Password.RequiredUniqueChars = 1;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager();
        services.TryAddScoped<IUserClaimsPrincipalFactory<AppUser>, UserClaimsPrincipalFactory<AppUser>>();
        services.TryAddScoped<ISecurityStampValidator, SecurityStampValidator<AppUser>>();
        // The login handler implements lockout against TimeProvider (UserManager reads the system clock). The Identity
        // lockout options carry the same values so SignInManager paths (T-010) cannot drift.
        services.AddOptions<IdentityOptions>().Configure<IOptions<AuthOptions>>((identity, auth) =>
        {
            identity.Password.RequiredLength = auth.Value.MinPasswordLength;
            identity.Lockout.MaxFailedAccessAttempts = auth.Value.LockoutMaxFailedAttempts;
            identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(auth.Value.LockoutMinutes);
        });

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, cookie =>
            {
                cookie.Cookie.Name = AuthCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.Path = "/";
                cookie.Cookie.IsEssential = true;
                cookie.SlidingExpiration = true;
                cookie.Events.OnValidatePrincipal = KeepKidModeAsync;
                // The API never redirects for auth.
                cookie.Events.OnRedirectToLogin = context => AppProblems.WriteAsync(
                    context.HttpContext, StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");
                cookie.Events.OnRedirectToAccessDenied = context => AppProblems.WriteAsync(
                    context.HttpContext, StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
            });
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme).Configure<IOptions<AuthOptions>>(
            (cookie, auth) => cookie.ExpireTimeSpan = TimeSpan.FromDays(auth.Value.CookieLifetimeDays));

        services.AddAuthorization(authorization =>
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = ApiRoot.HeaderName;
            antiforgery.Cookie.Name = ApiRoot.CookieName;
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.Path = "/";
        });
        return services;
    }
}
