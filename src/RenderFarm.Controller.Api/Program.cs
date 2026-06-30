using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using RenderFarm.Controller.Api;
using RenderFarm.Persistence;
using RenderFarm.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
builder.Services.ConfigureHttpJsonOptions(options => RenderFarmJson.AddConverters(options.SerializerOptions));
builder.Services.Configure<RenderFarmDatabaseOptions>(builder.Configuration.GetSection("RenderFarm:Database"));
builder.Services.Configure<JobSchedulerOptions>(builder.Configuration.GetSection("RenderFarm:Scheduler"));
builder.Services.Configure<ControllerDiscoveryOptions>(builder.Configuration.GetSection("RenderFarm:Discovery"));
builder.Services.Configure<ControllerSecurityOptions>(builder.Configuration.GetSection("RenderFarm:Security"));
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection("RenderFarm:Notifications"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IActivityLog, InMemoryActivityLog>();
builder.Services.AddSingleton<SqliteRenderFarmRepository>();
builder.Services.AddSingleton<IRenderFarmDatabase>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<IWorkerRepository>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<IProjectRepository>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<IRenderProfileRepository>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<IJobRepository>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<IJobAttemptRepository>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<IJobLeaseRepository>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<IJobEventRepository>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<ISettingsRepository>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<ISchedulerStateRepository>(sp => sp.GetRequiredService<SqliteRenderFarmRepository>());
builder.Services.AddSingleton<IRetryPolicy, ConfiguredRetryPolicy>();
builder.Services.AddSingleton<IUnrealProjectScanner, UnrealProjectScanner>();
builder.Services.AddSingleton<IJobNotificationSink, WebhookJobNotificationSink>();
builder.Services.AddSingleton<IJobScheduler, JobScheduler>();
builder.Services.AddHostedService<ControllerDiscoveryService>();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

builder.Services.AddHostedService<ControllerStartupRecoveryService>();

var app = builder.Build();
try
{
    await app.Services.GetRequiredService<IRenderFarmDatabase>().InitializeAsync(app.Lifetime.ApplicationStopping);
}
catch (Exception ex)
{
    Console.Error.WriteLine("RenderFarm controller failed to initialize its SQLite database.");
    Console.Error.WriteLine("Check that the configured RenderFarm:Database:Path directory exists or can be created by this Windows user.");
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
    return;
}

app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseOptionalApiTokenProtection();

app.MapSystemEndpoints();
app.MapActivityEndpoints();
app.MapWorkerEndpoints();
app.MapProjectEndpoints();
app.MapRenderProfileEndpoints();
app.MapJobEndpoints();
app.MapSettingsEndpoints();

app.Run();



