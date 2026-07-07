using System.Net;
using RenderFarm.Controller.Api;
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

    [Fact]
    public void WildcardBindBuildsSeparateBindLocalAndLanUrls()
    {
        var settings = new LauncherSettings { Role = "controller", HostName = "0.0.0.0", Port = 9200, DiscoveryEnabled = true };

        var ok = RenderFarmLauncher.TryBuildControllerNetworkUrls(settings, "192.168.1.25", out var urls, out var error);

        Assert.True(ok, error);
        Assert.Equal("http://0.0.0.0:9200/", urls.BindUrl);
        Assert.Equal("http://127.0.0.1:9200/", urls.LocalDashboardUrl);
        Assert.Equal("http://127.0.0.1:9200/health", urls.LocalHealthUrl);
        Assert.Equal("http://192.168.1.25:9200/", urls.LanWorkerUrl);
        Assert.Equal("http://192.168.1.25:9200/health", urls.LanHealthUrl);
        Assert.Equal("http://192.168.1.25:9200/", urls.AdvertisedDiscoveryUrl);
        Assert.True(urls.IsLanMode);
        Assert.True(urls.IsWildcardBind);
    }
    [Fact]
    public void ControllerDiscoveryAdvertisedUrlUsesLanAddressForWildcardBind()
    {
        var settings = new LauncherSettings { Role = "controller", HostName = "0.0.0.0", Port = 9200, DiscoveryEnabled = true };

        var advertised = RenderFarmLauncher.BuildControllerDiscoveryUrl(settings, "192.168.1.25");

        Assert.Equal("http://192.168.1.25:9200/", advertised);
    }

    [Fact]
    public void ControllerDiscoveryRejectsLoopbackBind()
    {
        var settings = new LauncherSettings { Role = "controller", HostName = "127.0.0.1", Port = 9200, DiscoveryEnabled = true };

        var ok = RenderFarmLauncher.TryValidateControllerDiscoverySettings(settings, out var error);

        Assert.False(ok);
        Assert.Contains("0.0.0.0", error);
    }

    [Fact]
    public void ControllerStartInfoIncludesDiscoveryEnvironment()
    {
        var settings = new LauncherSettings { Role = "controller", HostName = "0.0.0.0", Port = 9200, DiscoveryEnabled = true, DiscoveryPort = 39200 };
        var startInfo = RenderFarmLauncher.BuildStartInfo("controller", "RenderFarm.Controller.Api.exe", settings, "http://0.0.0.0:9200/", captureOutput: false);

        Assert.Contains("--urls", startInfo.ArgumentList);
        Assert.Contains("http://0.0.0.0:9200", startInfo.ArgumentList);
        Assert.Equal("true", startInfo.Environment["RenderFarm__Discovery__Enabled"]);
        Assert.Equal("39200", startInfo.Environment["RenderFarm__Discovery__Port"]);
        Assert.True(startInfo.Environment.TryGetValue("RenderFarm__Discovery__ControllerUrl", out var advertisedUrl));
        Assert.DoesNotContain("0.0.0.0", advertisedUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", advertisedUrl, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void ControllerDiscoveryServiceDoesNotAdvertiseLoopbackOrWildcardConfiguredUrl()
    {
        var fromLoopback = ControllerDiscoveryService.ResolveControllerUrl("http://127.0.0.1:9200/");
        var fromWildcard = ControllerDiscoveryService.ResolveControllerUrl("http://0.0.0.0:9200/");

        Assert.DoesNotContain("127.0.0.1", fromLoopback, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.0.0.0", fromWildcard, StringComparison.OrdinalIgnoreCase);
    }
    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}





