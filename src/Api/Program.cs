using Foodplanner.Api.Features.Health;
using Foodplanner.Api.OpenApi;
using Foodplanner.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFoodplannerOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

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
