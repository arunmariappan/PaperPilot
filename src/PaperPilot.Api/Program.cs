using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPaperPilotOptions();
builder.AddPaperPilotDatabase();

// Invalid request bodies get a 400 ProblemDetails response (C1).
builder.Services.AddProblemDetails();
builder.Services.AddValidation();

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
