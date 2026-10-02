using PaperPilot.Core.Options;
using PaperPilot.Infrastructure;
using PaperPilot.Infrastructure.Persistence;
using PaperPilot.Infrastructure.Search;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPaperPilotOptions();
builder.AddPaperPilotDatabase();
builder.AddPaperPilotSearch();

builder.Services.AddHostedService<SearchIndexInitializer>();
builder.Services.AddHealthChecks().AddOpenSearchCheck();

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
