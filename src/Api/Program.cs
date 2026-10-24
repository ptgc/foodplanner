using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.Auth;
using Foodplanner.Api.Features.Catalog;
using Foodplanner.Api.Features.Health;
using Foodplanner.Api.Features.Households;
using Foodplanner.Api.Features.Recipes;
using Foodplanner.Api.Features.Tags;
using Foodplanner.Api.OpenApi;
using Foodplanner.Api.Security;
using Foodplanner.Api.Seeding;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure;
using Foodplanner.Infrastructure.Email;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFoodplannerOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEmailSender(builder.Environment.IsDevelopment());
builder.Services.AddFoodplannerAuth();
builder.Services.AddFoodplannerRateLimiting();
builder.Services.AddHouseholdPolicies();
builder.Services.AddOptions<HouseholdOptions>().BindConfiguration(HouseholdOptions.Section).ValidateDataAnnotations().ValidateOnStart();
// One light per-request instance, set by HouseholdContextMiddleware (Q-004).
builder.Services.AddScoped<RequestCurrentHousehold>();
builder.Services.AddScoped<ICurrentHousehold>(services => services.GetRequiredService<RequestCurrentHousehold>());

var app = builder.Build();

// One-shot host command (scripts/local-seed): seeds the local admin and exits. Refuses outside Development.
if (args.Contains(LocalAdminSeeder.Command))
{
    return await LocalAdminSeeder.RunAsync(app.Services);
}

// The auth and antiforgery cookies are Secure and the antiforgery system refuses to run on a request it sees as plain
// HTTP.
if (RateLimitingSetup.TrustedNetworks(app.Configuration).Length > 0)
{
    // A configured proxy network: honour its X-Forwarded-For/-Proto (the proxy must send Proto=https). Without any
    // trusted network the headers are ignored, because an empty list would trust everyone.
    app.UseForwardedHeaders();
}
else
{
    if (!app.Environment.IsDevelopment())
    {
        app.Logger.LogWarning(
            "{Setting} is not set: X-Forwarded-For is ignored, so every client behind the proxy shares one rate-limit bucket.",
            RateLimitingSetup.TrustedNetworksSetting);
    }

    // No trusted proxy (local compose, Vite proxy): the API sees plain HTTP from nginx. Locally browsers treat
    // http://localhost as a secure context, so declaring https weakens nothing: the cookies are Secure either way.
    // Set Auth:AssumeHttps=false only to run the API directly behind its own TLS.
    if (app.Configuration.GetValue("Auth:AssumeHttps", true))
    {
        app.Use((context, next) =>
        {
            context.Request.Scheme = Uri.UriSchemeHttps;
            return next(context);
        });
    }
}

app.UseRouting();
app.UseAuthentication();
// After authentication (needs the caller) and before authorization (policies read the role).
app.UseMiddleware<HouseholdContextMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

// Runtime exposure of /openapi/v1.json is Development only. It is the one anonymous endpoint besides the allow-list
// (health, antiforgery, register, login, confirm-email), and it does not exist in other environments.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// Every endpoint hangs off this root group, which validates the antiforgery token on unsafe methods.
var api = app.MapApiRoot();
api.MapHealthEndpoints();
api.MapAuthEndpoints();
api.MapHouseholdEndpoints();
api.MapCatalogEndpoints();
api.MapRecipeEndpoints();
api.MapTagEndpoints();

app.Run();
return 0;

// Makes Program visible to WebApplicationFactory<Program> in Api.Tests.
public partial class Program;
