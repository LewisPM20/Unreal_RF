using Xunit;

namespace RenderFarm.Tests;

public sealed class DashboardAssetTests
{
    [Fact]
    public void ControllerDashboardAssetExistsAndReferencesPackagedAssets()
    {
        var repoRoot = FindRepoRoot();
        var webRoot = Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", "wwwroot");
        var dashboard = Path.Combine(webRoot, "index.html");
        var css = Path.Combine(webRoot, "css", "app.css");
        var apiJs = Path.Combine(webRoot, "js", "api.js");
        var stateJs = Path.Combine(webRoot, "js", "state.js");
        var renderJs = Path.Combine(webRoot, "js", "render.js");
        var dashboardJs = Path.Combine(webRoot, "js", "dashboard.js");

        Assert.True(File.Exists(dashboard), $"Dashboard was not found at {dashboard}");
        Assert.True(File.Exists(css), $"Dashboard stylesheet was not found at {css}");
        Assert.True(File.Exists(apiJs), $"Dashboard API module was not found at {apiJs}");
        Assert.True(File.Exists(stateJs), $"Dashboard state module was not found at {stateJs}");
        Assert.True(File.Exists(renderJs), $"Dashboard render module was not found at {renderJs}");
        Assert.True(File.Exists(dashboardJs), $"Dashboard entry module was not found at {dashboardJs}");

        var html = File.ReadAllText(dashboard);
        var combinedAssets = string.Join('\n',
            html,
            File.ReadAllText(css),
            File.ReadAllText(apiJs),
            File.ReadAllText(stateJs),
            File.ReadAllText(renderJs),
            File.ReadAllText(dashboardJs));

        Assert.Contains("RenderFarm Controller", html);
        Assert.Contains("/css/app.css", html);
        Assert.Contains("/js/dashboard.js", html);
        Assert.Contains("/api/dashboard", combinedAssets);
        Assert.Contains("/api/workers/status", combinedAssets);
        Assert.Contains("/api/projects", combinedAssets);
        Assert.Contains("/api/render-profiles", combinedAssets);
        Assert.Contains("/api/jobs", combinedAssets);
        Assert.Contains("Shared Output Roots", combinedAssets);
        Assert.Contains("Import Farm Configuration", combinedAssets);
        Assert.Contains("Fill from worker heartbeat", combinedAssets);
        Assert.Contains("Pending Worker Approval", combinedAssets);
        Assert.Contains("Create Profile From Scan", combinedAssets);
        Assert.Contains("Preview chunks", combinedAssets);
        Assert.Contains("/api/jobs/chunk-preview", combinedAssets);
        Assert.Contains("Controller API Token", combinedAssets);
        Assert.Contains("renderFarmApiToken", combinedAssets);
        Assert.Contains("data-accept-worker", combinedAssets);
        Assert.Contains("data-worker-mode", combinedAssets);
        Assert.Contains("jobDrawer", combinedAssets);
        Assert.Contains("data-close-job-drawer", combinedAssets);
        Assert.Contains("activeJobDetailsRequest", combinedAssets);
        Assert.Contains("setJobDrawerOpen", combinedAssets);
        Assert.Contains(".job-drawer.hidden", combinedAssets);
        Assert.Contains("is-open", combinedAssets);
        Assert.Contains("state.selectedJobId = null", combinedAssets);
        Assert.Contains("event?.stopPropagation?.()", combinedAssets);
        Assert.Contains("event?.stopImmediatePropagation?.()", combinedAssets);
        Assert.Contains("tr[data-job-id]", combinedAssets);
        Assert.Contains("/api/jobs/${encodeURIComponent(jobId)}/attempts", combinedAssets);
        Assert.Contains("/api/jobs/${encodeURIComponent(jobId)}/events", combinedAssets);
        Assert.Contains("summary-grid", combinedAssets);
        Assert.Contains("nav-tab", combinedAssets);
        Assert.Contains("Rescan workers", combinedAssets);
        Assert.Contains("Reset controller database", combinedAssets);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RenderFarm.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root containing RenderFarm.sln.");
    }
}

