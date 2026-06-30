using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public static class SystemEndpoints
{
    private const string ServiceName = "RenderFarm.Controller.Api";
    private const string ControllerVersion = "0.16.0-operator-polish";
    private const string RuntimeName = "csharp";

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { ok = true, service = ServiceName, version = ControllerVersion, dashboard = "/" }));

        var system = app.MapGroup("/api/system").WithTags("System");
        system.MapGet("", async (IWorkerRepository workers, IProjectRepository projects, IRenderProfileRepository profiles, IJobRepository jobs, HttpResponse response, CancellationToken ct) =>
        {
            DisableClientCaching(response);
            var workerListTask = workers.ListAsync(ct);
            var projectListTask = projects.ListAsync(ct);
            var profileListTask = profiles.ListAsync(ct);
            var jobListTask = jobs.ListAsync(ct);

            await Task.WhenAll(workerListTask, projectListTask, profileListTask, jobListTask);

            var workerList = await workerListTask;
            var projectList = await projectListTask;
            var profileList = await profileListTask;
            var jobList = await jobListTask;

            return Results.Ok(new
            {
                ok = true,
                service = ServiceName,
                version = ControllerVersion,
                runtime = RuntimeName,
                dashboard = "/",
                workers = workerList.Count,
                projects = projectList.Count,
                render_profiles = profileList.Count,
                jobs = jobList.Count,
                job_states = BuildJobStateCounts(jobList)
            });
        });

        app.MapGet("/api/dashboard", async (
            IWorkerRepository workers,
            IProjectRepository projects,
            IRenderProfileRepository profiles,
            IJobRepository jobs,
            IJobAttemptRepository attempts,
            ISettingsRepository settings,
            HttpResponse response,
            CancellationToken ct) =>
        {
            DisableClientCaching(response);
            var now = DateTimeOffset.UtcNow;
            var workerListTask = workers.ListAsync(ct);
            var projectListTask = projects.ListAsync(ct);
            var profileListTask = profiles.ListAsync(ct);
            var jobListTask = jobs.ListAsync(ct);
            var attemptCountsTask = attempts.CountByJobAsync(ct);
            var approvalsTask = WorkerApproval.ListAsync(settings, ct);
            var schedulingModesTask = WorkerScheduling.ListAsync(settings, ct);

            await Task.WhenAll(workerListTask, projectListTask, profileListTask, jobListTask, attemptCountsTask, approvalsTask, schedulingModesTask);

            var workerList = await workerListTask;
            var projectList = await projectListTask;
            var profileList = await profileListTask;
            var jobList = await jobListTask;
            var attemptCounts = await attemptCountsTask;
            var approvals = await approvalsTask;
            var schedulingModes = await schedulingModesTask;

            var summary = new DashboardSummaryDto(
                true,
                ServiceName,
                ControllerVersion,
                RuntimeName,
                workerList.Count,
                projectList.Count,
                profileList.Count,
                jobList.Count,
                BuildJobStateCounts(jobList));

            var dashboardWorkers = new DashboardWorkerDto[workerList.Count];
            for (var i = 0; i < workerList.Count; i++)
            {
                var worker = workerList[i];
                var dto = worker.ToDto();
                dashboardWorkers[i] = new DashboardWorkerDto(
                    dto.Id,
                    dto.Name,
                    dto.Hostname,
                    dto.IpAddress,
                    dto.ServiceUrl,
                    dto.Status,
                    WorkerStatusProjection.GetEffectiveWorkerStatus(worker, now),
                    WorkerApproval.GetEffective(approvals, worker),
                    WorkerScheduling.GetEffective(schedulingModes, worker),
                    dto.Stage,
                    dto.CurrentJobId,
                    dto.AgentVersion,
                    dto.Capabilities,
                    dto.LastError,
                    dto.RegisteredAtUtc,
                    dto.LastHeartbeatUtc,
                    Math.Max(0, (int)(now - dto.LastHeartbeatUtc).TotalSeconds));
            }

            var projectDtos = new ProjectProfileDto[projectList.Count];
            for (var i = 0; i < projectList.Count; i++)
            {
                projectDtos[i] = projectList[i].ToDto();
            }

            var profileDtos = new RenderProfileDto[profileList.Count];
            for (var i = 0; i < profileList.Count; i++)
            {
                profileDtos[i] = profileList[i].ToDto();
            }

            var dashboardJobs = jobList
                .OrderByDescending(job => job.UpdatedAtUtc)
                .Select(job => new DashboardJobDto(job.ToDto(), attemptCounts.TryGetValue(job.Id, out var count) ? count : 0))
                .ToArray();

            return Results.Ok(new DashboardSnapshotDto(
                now,
                summary,
                dashboardWorkers,
                projectDtos,
                profileDtos,
                dashboardJobs));
        }).WithTags("System");

        app.MapGet("/api/diagnostics", async (
            IWorkerRepository workers,
            IProjectRepository projects,
            IRenderProfileRepository profiles,
            IJobRepository jobs,
            ISettingsRepository settings,
            IOptions<RenderFarmDatabaseOptions> databaseOptions,
            IActivityLog activity,
            HttpRequest request,
            HttpResponse response,
            CancellationToken ct) =>
        {
            DisableClientCaching(response);
            var now = DateTimeOffset.UtcNow;
            var workerListTask = workers.ListAsync(ct);
            var projectListTask = projects.ListAsync(ct);
            var profileListTask = profiles.ListAsync(ct);
            var jobListTask = jobs.ListAsync(ct);
            var approvalsTask = WorkerApproval.ListAsync(settings, ct);

            await Task.WhenAll(workerListTask, projectListTask, profileListTask, jobListTask, approvalsTask);

            var workerList = await workerListTask;
            var projectList = await projectListTask;
            var profileList = await profileListTask;
            var jobList = await jobListTask;
            var approvals = await approvalsTask;
            var approvedWorkers = workerList.Count(worker => string.Equals(WorkerApproval.GetEffective(approvals, worker), WorkerApproval.Accepted, StringComparison.OrdinalIgnoreCase));
            var pendingWorkers = workerList.Count(worker => string.Equals(WorkerApproval.GetEffective(approvals, worker), WorkerApproval.Pending, StringComparison.OrdinalIgnoreCase));
            var readyWorkers = workerList.Count(worker => IsWorkerSchedulable(worker, approvals, now));
            var outputRoots = workerList
                .SelectMany(worker => worker.Capabilities.SharedOutputRoots.Select(root => new
                {
                    workerId = worker.Id,
                    workerName = worker.Name,
                    path = root.Path,
                    exists = root.Exists,
                    writable = root.Writable,
                    freeDiskGb = root.FreeDiskGb,
                    message = root.Message
                }))
                .OrderBy(root => root.workerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(root => root.path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(new
            {
                generatedAtUtc = now,
                controller = new
                {
                    ok = true,
                    service = ServiceName,
                    version = ControllerVersion,
                    runtime = RuntimeName,
                    url = $"{request.Scheme}://{request.Host}",
                    dashboard = "/"
                },
                database = BuildDatabaseDiagnostics(databaseOptions.Value),
                counts = new
                {
                    workers = workerList.Count,
                    approvedWorkers,
                    pendingWorkers,
                    readyWorkers,
                    projects = projectList.Count,
                    renderProfiles = profileList.Count,
                    jobs = jobList.Count,
                    queuedJobs = CountJobs(jobList, JobState.Queued),
                    activeJobs = jobList.Count(job => IsActiveJobState(job.State)),
                    completedJobs = CountJobs(jobList, JobState.Succeeded),
                    failedJobs = CountJobs(jobList, JobState.Failed),
                    cancelledJobs = CountJobs(jobList, JobState.Cancelled),
                    retryWaitJobs = CountJobs(jobList, JobState.RetryWait)
                },
                sharedOutputRoots = outputRoots,
                recentWorkerHeartbeats = workerList
                    .OrderByDescending(worker => worker.LastHeartbeatUtc)
                    .Take(10)
                    .Select(worker => new
                    {
                        workerId = worker.Id,
                        workerName = worker.Name,
                        hostname = worker.Hostname,
                        status = WorkerStatusProjection.GetEffectiveWorkerStatus(worker, now).ToString(),
                        approval = WorkerApproval.GetEffective(approvals, worker).ToString(),
                        lastHeartbeatUtc = worker.LastHeartbeatUtc,
                        secondsSinceHeartbeat = Math.Max(0, (int)(now - worker.LastHeartbeatUtc).TotalSeconds),
                        currentJobId = worker.CurrentJobId
                    })
                    .ToArray(),
                recentWarnings = activity.ListRecent(50)
                    .Where(item => string.Equals(item.Severity, "warning", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase))
                    .Take(12)
                    .ToArray(),
                support = new
                {
                    contentRoot = AppContext.BaseDirectory,
                    process = Environment.ProcessPath,
                    machine = Environment.MachineName,
                    os = Environment.OSVersion.VersionString,
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                }
            });
        }).WithTags("System");
        app.MapGet("/api/config", () => Results.Ok(new
        {
            runtime = RuntimeName,
            controller = ServiceName,
            dashboard = "/",
            endpoints = new[]
            {
                "/api/workers",
                "/api/workers/status",
                "/api/projects",
                "/api/render-profiles",
                "/api/jobs",
                "/api/jobs/expire-leases"
            }
        })).WithTags("System");

        app.MapDelete("/api/admin/state", async (SqliteRenderFarmRepository repository, CancellationToken ct) =>
        {
            var cleared = await repository.ClearAllAsync(ct);
            return Results.Ok(new { cleared, message = "Controller state was reset and the SQLite database was compacted." });
        }).WithTags("System");

        return app;
    }

    private static bool IsWorkerSchedulable(Worker worker, IReadOnlyDictionary<string, string> approvals, DateTimeOffset now)
    {
        if (!string.Equals(WorkerApproval.GetEffective(approvals, worker), WorkerApproval.Accepted, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var status = WorkerStatusProjection.GetEffectiveWorkerStatus(worker, now);
        return string.Equals(status, WorkerStatus.Online.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(status, WorkerStatus.Idle.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveJobState(JobState state) => state is JobState.Reserved or JobState.Running or JobState.ValidatingWorker or JobState.PreparingUnrealQueue or JobState.LaunchingUnreal or JobState.Rendering or JobState.VerifyingOutputs or JobState.CancelRequested or JobState.Cancelling;

    private static int CountJobs(IReadOnlyList<RenderJob> jobs, JobState state) => jobs.Count(job => job.State == state);

    private static object BuildDatabaseDiagnostics(RenderFarmDatabaseOptions options)
    {
        var connectionStringConfigured = !string.IsNullOrWhiteSpace(options.ConnectionString);
        var configuredPath = string.IsNullOrWhiteSpace(options.Path)
            ? RenderFarmDatabaseOptions.GetDefaultDatabasePath()
            : ExpandConfiguredPath(options.Path);
        var dataSource = configuredPath;

        if (connectionStringConfigured)
        {
            try
            {
                dataSource = new SqliteConnectionStringBuilder(options.ResolveConnectionString()).DataSource;
            }
            catch (ArgumentException)
            {
                dataSource = "configured connection string";
            }
        }

        return new
        {
            path = dataSource,
            configuredPath = options.Path,
            usesDefaultPath = string.IsNullOrWhiteSpace(options.Path) && !connectionStringConfigured,
            connectionStringConfigured
        };
    }

    private static string ExpandConfiguredPath(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return System.IO.Path.IsPathFullyQualified(expanded) ? expanded : System.IO.Path.GetFullPath(expanded);
    }
    private static Dictionary<string, int> BuildJobStateCounts(IReadOnlyList<RenderJob> jobs)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            var key = job.State.ToString();
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    private static void DisableClientCaching(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, max-age=0";
        response.Headers.Pragma = "no-cache";
    }
}




