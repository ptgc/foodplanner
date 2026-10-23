using Foodplanner.Api.Features.Health;
using Foodplanner.Api.OpenApi;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFoodplannerOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
// T-011 replaces this with the membership-based implementation.
builder.Services.AddScoped<ICurrentHousehold, PlaceholderCurrentHousehold>();

var app = builder.Build();

// Runtime exposure of /openapi/v1.json is Development only.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();

app.Run();

// Makes Program visible to WebApplicationFactory<Program> in Api.Tests.
public partial class Program;
