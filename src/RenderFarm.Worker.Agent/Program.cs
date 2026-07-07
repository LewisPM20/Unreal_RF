using RenderFarm.Worker.Agent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<WorkerAgentOptions>(builder.Configuration.GetSection("RenderFarm"));
builder.Services.Configure<RenderProcessExecutionOptions>(builder.Configuration.GetSection("RenderFarm:Process"));
builder.Services.Configure<WorkerPreflightOptions>(builder.Configuration.GetSection("RenderFarm:Preflight"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IWorkerIdentityProvider, WorkerIdentityProvider>();
builder.Services.AddSingleton<IControllerEndpointProvider, ControllerEndpointProvider>();
builder.Services.AddSingleton<IWorkerCapabilityDetector, WorkerCapabilityDetector>();
builder.Services.AddSingleton<ISharedOutputValidator, SharedOutputValidator>();
builder.Services.AddSingleton<IWorkerPreflightService, WorkerPreflightService>();
builder.Services.AddSingleton<IWorkerExecutionStateStore, WorkerExecutionStateStore>();
builder.Services.AddSingleton<IUnrealEngineLocator, UnrealEngineLocator>();
builder.Services.AddSingleton<IProcessLauncher, ProcessLauncher>();
builder.Services.AddSingleton<IUnrealCommandBuilder, UnrealCommandBuilder>();
builder.Services.AddSingleton<IUnrealProcessLauncher, UnrealProcessLauncher>();
builder.Services.AddHostedService<WorkerSingleInstanceService>();
builder.Services.AddHostedService<WorkerHeartbeatService>();
builder.Services.AddHostedService<WorkerJobService>();

var host = builder.Build();
host.Run();



