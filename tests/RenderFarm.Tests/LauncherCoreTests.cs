using System.Net;
using RenderFarm.Launcher;
using Xunit;

namespace RenderFarm.Tests;

public sealed class LauncherCoreTests
{
    [Fact]
    public void BuildsDashboardAndHealthUrls()
    {
        var settings = new LauncherSettings { Role = "controller", HostName = "127.0.0.1", Port = 9200 };

        var dashboardUrl = RenderFarmLauncher.GetDashboardUrl(settings);

        Assert.Equal("http://127.0.0.1:9200/", dashboardUrl);
        Assert.Equal("http://127.0.0.1:9200/health", RenderFarmLauncher.GetHealthUrl(dashboardUrl));
    }

    [Fact]
    public void RejectsInvalidControllerPort()
    {
        var settings = new LauncherSettings { Role = "controller", HostName = "127.0.0.1", Port = 70000 };

        var ok = RenderFarmLauncher.TryBuildDashboardUrl(settings, out _, out var error);

        Assert.False(ok);
        Assert.Contains("between 1 and 65535", error);
    }

    [Fact]
    public void RuntimeExecutableCandidatesMatchPublishedLayout()
    {
        var controllerCandidates = RenderFarmLauncher.GetRuntimeExecutableCandidates("controller");
        var workerCandidates = RenderFarmLauncher.GetRuntimeExecutableCandidates("worker");

        Assert.Contains(controllerCandidates, path => path.EndsWith(Path.Combine("controller", "RenderFarm.Controller.Api.exe"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(workerCandidates, path => path.EndsWith(Path.Combine("worker", "RenderFarm.Worker.Agent.exe"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProcessNotFoundMessageIncludesCheckedPaths()
    {
        var candidates = new[] { "C:\\RenderFarm\\controller\\RenderFarm.Controller.Api.exe", "C:\\RenderFarm\\RenderFarm.Controller.Api.exe" };

        var message = RenderFarmLauncher.BuildExecutableNotFoundMessage("controller", candidates);

        Assert.Contains("controller\\RenderFarm.Controller.Api.exe", message);
        Assert.Contains(candidates[0], message);
        Assert.Contains(candidates[1], message);
    }

    [Fact]
    public async Task ControllerHealthCheckReportsSuccess()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK));

        var result = await RenderFarmLauncher.CheckControllerHealthAsync(client, "http://127.0.0.1:9200/health", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task ControllerHealthCheckReportsFailure()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable));

        var result = await RenderFarmLauncher.CheckControllerHealthAsync(client, "http://127.0.0.1:9200/health", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public void ControllerStartupFailureExplainsSqliteOpenFailure()
    {
        var message = RenderFarmLauncher.ExplainControllerStartupFailure(
            "Controller process exited immediately with code 1.",
            "Microsoft.Data.Sqlite.SqliteException: SQLite Error 14: 'unable to open database file'.");

        Assert.Contains("SQLite database", message);
        Assert.Contains("renderfarm.db", message);
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
