using PaperPilot.Infrastructure.Persistence;
using PaperPilot.MigrationService;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddPaperPilotDatabase();

builder.Services.AddHostedService<MigrationWorker>();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(MigrationWorker.ActivitySourceName));

var host = builder.Build();
host.Run();
