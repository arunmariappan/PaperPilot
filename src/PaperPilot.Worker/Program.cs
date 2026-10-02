using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPaperPilotOptions();
builder.AddPaperPilotDatabase();

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
