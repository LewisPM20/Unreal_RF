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
        var controllerProject = Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", "RenderFarm.Controller.Api.csproj");
        var controllerProgram = Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", "Program.cs");
        var activityLog = Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", "ActivityLog.cs");
        var systemEndpoints = Path.Combine(repoRoot, "src", "RenderFarm.Controller.Api", "SystemEndpoints.cs");
        var contracts = Path.Combine(repoRoot, "src", "RenderFarm.Shared", "Contracts.cs");

        Assert.True(File.Exists(dashboard), $"Dashboard was not found at {dashboard}");
        Assert.True(File.Exists(css), $"Dashboard stylesheet was not found at {css}");
        Assert.True(File.Exists(apiJs), $"Dashboard API module was not found at {apiJs}");
        Assert.True(File.Exists(stateJs), $"Dashboard state module was not found at {stateJs}");
        Assert.True(File.Exists(renderJs), $"Dashboard render module was not found at {renderJs}");
        Assert.True(File.Exists(dashboardJs), $"Dashboard entry module was not found at {dashboardJs}");
        Assert.True(File.Exists(controllerProject), $"Controller project was not found at {controllerProject}");
        Assert.True(File.Exists(controllerProgram), $"Controller Program.cs was not found at {controllerProgram}");
        Assert.True(File.Exists(activityLog), $"Activity log was not found at {activityLog}");
        Assert.True(File.Exists(systemEndpoints), $"System endpoints were not found at {systemEndpoints}");
        Assert.True(File.Exists(contracts), $"Shared contracts were not found at {contracts}");

        var html = File.ReadAllText(dashboard);
        var combinedAssets = string.Join('\n',
            html,
            File.ReadAllText(css),
            File.ReadAllText(apiJs),
            File.ReadAllText(stateJs),
            File.ReadAllText(renderJs),
            File.ReadAllText(dashboardJs),
            File.ReadAllText(controllerProject),
            File.ReadAllText(controllerProgram),
            File.ReadAllText(activityLog),
            File.ReadAllText(systemEndpoints),
            File.ReadAllText(contracts));

        Assert.Contains("RenderFarm Controller", html);
        Assert.Contains("/css/app.css", html);
        Assert.Contains("/js/dashboard.js", html);
        Assert.Contains("CopyToOutputDirectory", combinedAssets);
        Assert.Contains("UseStaticWebAssets", combinedAssets);
        Assert.Contains("/api/dashboard", combinedAssets);
        Assert.Contains("/api/activity/recent", combinedAssets);
        Assert.Contains("/api/workers/status", combinedAssets);
        Assert.Contains("/api/projects", combinedAssets);
        Assert.Contains("/api/render-profiles", combinedAssets);
        Assert.Contains("/api/jobs", combinedAssets);
        Assert.Contains("Shared Output Roots", combinedAssets);
        Assert.Contains("Import Farm Configuration", combinedAssets);
        Assert.Contains("Fill from worker heartbeat", combinedAssets);
        Assert.Contains("Pending Worker Approval", combinedAssets);
        Assert.Contains("activityFeed", combinedAssets);
        Assert.Contains("farmReadiness", combinedAssets);
        Assert.Contains("approvalBanner", combinedAssets);
        Assert.Contains("Farm readiness", combinedAssets);
        Assert.Contains("renderFarmReadiness", combinedAssets);
        Assert.Contains("workersNavBadge", combinedAssets);
        Assert.Contains("ActivityItemDto", combinedAssets);
        Assert.Contains("MapActivityEndpoints", combinedAssets);
        Assert.Contains("projectModal", combinedAssets);
        Assert.Contains("profileModal", combinedAssets);
        Assert.Contains("jobModal", combinedAssets);
        Assert.Contains("newRenderModal", combinedAssets);
        Assert.Contains("New Render", combinedAssets);
        Assert.Contains("Queue a render", combinedAssets);
        Assert.Contains("newRenderProjectSelect", combinedAssets);
        Assert.Contains("newRenderProfileSelect", combinedAssets);
        Assert.Contains("newRenderReadiness", combinedAssets);
        Assert.Contains("newRenderReview", combinedAssets);
        Assert.Contains("data-wizard-pane", combinedAssets);
        Assert.Contains("Worker Readiness", combinedAssets);
        Assert.Contains("Advanced Setup", combinedAssets);
        Assert.Contains("Render Setup", combinedAssets);
        Assert.DoesNotContain("Create Profile From Scan", combinedAssets);
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
        Assert.Contains("/api/diagnostics", combinedAssets);
        Assert.Contains("diagnosticsPanel", combinedAssets);
        Assert.Contains("copyDiagnosticsBtn", combinedAssets);
        Assert.Contains("queueFilters", combinedAssets);
        Assert.Contains("data-queue-filter", combinedAssets);
        Assert.Contains("recentCompletedRenders", combinedAssets);
        Assert.Contains("jobDrawerActions", combinedAssets);
        Assert.Contains("data-copy", combinedAssets);
        Assert.Contains("copyText", combinedAssets);
        Assert.Contains("data-retry-job", combinedAssets);
        Assert.Contains("data-cancel-job", combinedAssets);
        Assert.Contains("renderRecentCompletedRenders", combinedAssets);
        Assert.Contains("renderDiagnosticsPanel", combinedAssets);
        Assert.Contains("BuildDatabaseDiagnostics", combinedAssets);
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






