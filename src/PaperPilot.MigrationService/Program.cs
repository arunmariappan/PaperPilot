using PaperPilot.MigrationService;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();
