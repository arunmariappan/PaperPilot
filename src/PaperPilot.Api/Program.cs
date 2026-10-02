using PaperPilot.Api.Endpoints;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure;
using PaperPilot.Infrastructure.Persistence;
using PaperPilot.Infrastructure.Search;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPaperPilotOptions();
builder.AddPaperPilotDatabase();
builder.AddPaperPilotSearch();

builder.Services.AddHostedService<SearchIndexInitializer>();
builder.Services.AddHealthChecks()
    .AddDatabaseCheck()
    .AddOpenSearchCheck()
    .AddOllamaCheck();

// Invalid request bodies get a 400 ProblemDetails response (C1), keyed by the snake_case field names clients send.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsNaming.UseSnakeCaseErrorKeys);
builder.Services.AddValidation();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapScalarApiReference("/docs");

var api = app.MapGroup("/api/v1");
api.MapHealthEndpoints();
api.MapSearchEndpoints();

app.Run();
