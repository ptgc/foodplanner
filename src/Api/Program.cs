using Foodplanner.Api.Features.Auth;
using Foodplanner.Api.Features.Health;
using Foodplanner.Api.OpenApi;
using Foodplanner.Api.Security;
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
// T-011 replaces this with the membership-based implementation.
builder.Services.AddScoped<ICurrentHousehold, PlaceholderCurrentHousehold>();

var app = builder.Build();

// Only from the configured proxy networks; without any, X-Forwarded-* is ignored (an empty list would trust everyone).
if (RateLimitingSetup.TrustedNetworks(app.Configuration).Length > 0)
{
    app.UseForwardedHeaders();
}

// The auth and antiforgery cookies are Secure and the antiforgery system refuses to run on a request it sees as plain
// HTTP. TLS ends before the API (Container Apps ingress), and locally browsers treat http://localhost as a secure
// context, so the API always sees plain HTTP from its proxy. Declaring the scheme https weakens nothing: the cookies
// are Secure either way. Set Auth:AssumeHttps=false only to run the API directly behind its own TLS.
if (app.Configuration.GetValue("Auth:AssumeHttps", true))
{
    app.Use((context, next) =>
    {
        context.Request.Scheme = Uri.UriSchemeHttps;
        return next(context);
    });
}

app.UseRouting();
app.UseAuthentication();
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

app.Run();

// Makes Program visible to WebApplicationFactory<Program> in Api.Tests.
public partial class Program;
