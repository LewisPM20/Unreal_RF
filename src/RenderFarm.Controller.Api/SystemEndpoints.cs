using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RenderFarm.Domain;
using RenderFarm.Persistence;
using RenderFarm.Shared;

namespace RenderFarm.Controller.Api;

public static class SystemEndpoints
{
    private const string ServiceName = "RenderFarm.Controller.Api";
    private static string ControllerVersion => RenderFarmVersion.ProductVersion;
    private const string RuntimeName = "csharp";

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { ok = true, service = ServiceName, version = ControllerVersion, protocolVersion = RenderFarmVersion.ProtocolVersion, apiContractVersion = RenderFarmVersion.ApiContractVersion, buildId = RenderFarmVersion.BuildId, dashboard = "/" }));

        app.MapGet("/api/version", () => Results.Ok(new { service = ServiceName, runtime = RuntimeName, productVersion = RenderFarmVersion.ProductVersion, protocolVersion = RenderFarmVersion.ProtocolVersion, apiContractVersion = RenderFarmVersion.ApiContractVersion, minimumWorkerProductVersion = RenderFarmVersion.MinimumWorkerProductVersion, minimumWorkerProtocolVersion = RenderFarmVersion.MinimumWorkerProtocolVersion, minimumWorkerApiContractVersion = RenderFarmVersion.MinimumWorkerApiContractVersion, buildId = RenderFarmVersion.BuildId })).WithTags("System");

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
                    Math.Max(0, (int)(now - dto.LastHeartbeatUtc).TotalSeconds),
                    dto.ProductVersion,
                    dto.ProtocolVersion,
                    dto.ApiContractVersion,
                    dto.BuildId,
                    dto.Compatibility);
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
            IOptions<ControllerSecurityOptions> securityOptions,
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
            var renderDefaultsTask = ControllerRenderDefaults.LoadAsync(settings, ct);

            await Task.WhenAll(workerListTask, projectListTask, profileListTask, jobListTask, approvalsTask, renderDefaultsTask);

            var workerList = await workerListTask;
            var projectList = await projectListTask;
            var profileList = await profileListTask;
            var jobList = await jobListTask;
            var approvals = await approvalsTask;
            var renderDefaults = await renderDefaultsTask;
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
                layout = BuildLayoutDiagnostics(),
                security = new
                {
                    apiToken = SecretRedaction.DescribeConfiguredSecret(securityOptions.Value.ApiToken),
                    protectedMutations = true,
                    defaultBinding = "Launcher defaults to http://127.0.0.1:9200 unless the operator chooses another host."
                },
                controllerConfiguration = new
                {
                    sourceOfTruth = "Controller database and dashboard own render execution settings; workers execute assigned payloads and report diagnostics.",
                    renderDefaults
                },
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
                        currentJobId = worker.CurrentJobId,
                        agentVersion = worker.AgentVersion,
                        compatibility = RenderFarmVersion.EvaluateWorkerAgent(worker.AgentVersion)
                    })
                    .ToArray(),
                recentWarnings = activity.ListRecent(50)
                    .Where(item => string.Equals(item.Severity, "warning", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase))
                    .Take(12)
                    .ToArray(),
                startupRecovery = activity.ListRecent(100)
                    .Where(item => string.Equals(item.Type, "Recovery", StringComparison.OrdinalIgnoreCase))
                    .Take(12)
                    .ToArray(),
                support = new
                {
                    contentRoot = AppContext.BaseDirectory,
                    singleInstance = ControllerSingleInstanceGuard.BuildDiagnostics(),
                    process = Environment.ProcessPath,
                    machine = Environment.MachineName,
                    os = Environment.OSVersion.VersionString,
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                }
            });
        }).WithTags("System");
        app.MapGet("/api/diagnostics/dispatch", async (
            IWorkerRepository workers,
            IProjectRepository projects,
            IRenderProfileRepository profiles,
            IJobRepository jobs,
            IJobLeaseRepository leases,
            ISettingsRepository settings,
            IDispatchDiagnostics dispatch,
            HttpResponse response,
            CancellationToken ct) =>
        {
            DisableClientCaching(response);
            var now = DateTimeOffset.UtcNow;
            var workerListTask = workers.ListAsync(ct);
            var projectListTask = projects.ListAsync(ct);
            var profileListTask = profiles.ListAsync(ct);
            var jobListTask = jobs.ListAsync(ct);
            var activeLeasesTask = leases.ListActiveAsync(ct);
            var staleLeasesTask = leases.ListExpiredAsync(now, ct);
            var approvalsTask = WorkerApproval.ListAsync(settings, ct);
            var schedulingModesTask = WorkerScheduling.ListAsync(settings, ct);
            var defaultsTask = ControllerRenderDefaults.LoadAsync(settings, ct);

            await Task.WhenAll(workerListTask, projectListTask, profileListTask, jobListTask, activeLeasesTask, staleLeasesTask, approvalsTask, schedulingModesTask, defaultsTask);

            var workerList = await workerListTask;
            var projectList = await projectListTask;
            var profileList = await profileListTask;
            var jobList = await jobListTask;
            var activeLeases = await activeLeasesTask;
            var staleLeases = await staleLeasesTask;
            var approvals = await approvalsTask;
            var schedulingModes = await schedulingModesTask;
            var defaults = await defaultsTask;
            var queuedJobs = jobList.Where(job => job.State == JobState.Queued).OrderByDescending(job => job.Priority).ThenBy(job => job.CreatedAtUtc).ToArray();

            var eligibility = new List<object>();
            foreach (var job in queuedJobs)
            {
                var project = projectList.FirstOrDefault(item => item.Id == job.ProjectId);
                var profile = profileList.FirstOrDefault(item => item.Id == job.RenderProfileId);
                foreach (var worker in workerList)
                {
                    var compatibility = RenderFarmVersion.EvaluateWorkerAgent(worker.AgentVersion);
                    var approval = WorkerApproval.GetEffective(approvals, worker);
                    var schedulingMode = WorkerScheduling.GetEffective(schedulingModes, worker);
                    WorkerProjectReadinessDto? readiness = null;
                    IReadOnlyList<string> reasons;
                    if (project is null || profile is null || profile.ProjectId != project.Id)
                    {
                        reasons = ["Job references a missing project/profile or a profile from another project."];
                    }
                    else
                    {
                        readiness = WorkerReadinessEvaluator.Evaluate(worker, project, profile, job.OutputDirectory, defaults);
                        reasons = readiness.Reasons;
                    }

                    eligibility.Add(new
                    {
                        jobId = job.Id,
                        workerId = worker.Id,
                        workerName = worker.Name,
                        approval,
                        schedulingMode,
                        effectiveStatus = WorkerStatusProjection.GetEffectiveWorkerStatus(worker, now),
                        agentVersion = worker.AgentVersion,
                        compatibility,
                        canRun = compatibility.Compatible
                            && string.Equals(approval, WorkerApproval.Accepted, StringComparison.OrdinalIgnoreCase)
                            && schedulingMode == WorkerSchedulingMode.Active
                            && readiness?.CanRun == true,
                        reasons
                    });
                }
            }

            var warnings = new List<string>();
            if (workerList.Count == 0)
            {
                warnings.Add("No workers have registered with this controller.");
            }

            var incompatibleWorkers = workerList.Where(worker => !RenderFarmVersion.EvaluateWorkerAgent(worker.AgentVersion).Compatible).ToArray();
            if (incompatibleWorkers.Length > 0)
            {
                warnings.Add($"{incompatibleWorkers.Length} worker(s) are running an incompatible RenderFarm version. Update or reinstall those workers before expecting dispatch.");
            }

            var pollingWorkers = dispatch.ListRecent(200).Select(item => item.WorkerId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (workerList.Count > 0 && pollingWorkers == 0)
            {
                warnings.Add("Workers are registered, but no worker has polled the request-job endpoint since this controller started.");
            }

            return Results.Ok(new
            {
                generatedAtUtc = now,
                controller = new
                {
                    service = ServiceName,
                    productVersion = RenderFarmVersion.ProductVersion,
                    protocolVersion = RenderFarmVersion.ProtocolVersion,
                    apiContractVersion = RenderFarmVersion.ApiContractVersion,
                    minimumWorkerProductVersion = RenderFarmVersion.MinimumWorkerProductVersion,
                    minimumWorkerProtocolVersion = RenderFarmVersion.MinimumWorkerProtocolVersion,
                    buildId = RenderFarmVersion.BuildId
                },
                queuedJobs = queuedJobs.Select(job => new
                {
                    job = job.ToDto(),
                    latestDecision = dispatch.GetLatestForJob(job.Id),
                    activeLease = activeLeases.FirstOrDefault(lease => lease.JobId == job.Id)?.ToDto()
                }).ToArray(),
                workers = workerList.Select(worker =>
                {
                    var dto = worker.ToDto();
                    return new
                    {
                        dto.Id,
                        dto.Name,
                        dto.Hostname,
                        dto.IpAddress,
                        dto.Status,
                        effectiveStatus = WorkerStatusProjection.GetEffectiveWorkerStatus(worker, now),
                        approval = WorkerApproval.GetEffective(approvals, worker),
                        schedulingMode = WorkerScheduling.GetEffective(schedulingModes, worker),
                        dto.AgentVersion,
                        dto.ProductVersion,
                        dto.ProtocolVersion,
                        dto.ApiContractVersion,
                        dto.BuildId,
                        dto.Compatibility,
                        dto.CurrentJobId,
                        dto.LastHeartbeatUtc,
                        secondsSinceHeartbeat = Math.Max(0, (int)(now - worker.LastHeartbeatUtc).TotalSeconds),
                        latestDecision = dispatch.GetLatestForWorker(worker.Id)
                    };
                }).ToArray(),
                activeLeases = activeLeases.Select(lease => lease.ToDto()).ToArray(),
                staleLeases = staleLeases.Select(lease => lease.ToDto()).ToArray(),
                workerEligibilityResults = eligibility,
                recentDecisions = dispatch.ListRecent(100),
                configWarnings = warnings
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

    private static object BuildLayoutDiagnostics()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var parent = Directory.GetParent(baseDirectory)?.FullName ?? baseDirectory;
        var launcherCandidates = new[]
        {
            Path.Combine(baseDirectory, "RenderFarm.Launcher.exe"),
            Path.Combine(parent, "RenderFarm.Launcher.exe")
        };
        var workerCandidates = new[]
        {
            Path.Combine(baseDirectory, "worker", "RenderFarm.Worker.Agent.exe"),
            Path.Combine(parent, "worker", "RenderFarm.Worker.Agent.exe"),
            Path.Combine(baseDirectory, "..", "worker", "RenderFarm.Worker.Agent.exe")
        };
        var dashboardIndex = Path.Combine(baseDirectory, "wwwroot", "index.html");

        return new
        {
            baseDirectory,
            processPath = Environment.ProcessPath,
            dashboardIndexExists = File.Exists(dashboardIndex),
            launcherExists = launcherCandidates.Any(File.Exists),
            workerExecutableExists = workerCandidates.Any(path => File.Exists(Path.GetFullPath(path))),
            expectedPackageShape = "RenderFarm.Launcher.exe beside controller/ and worker/ folders; controller serves wwwroot/index.html."
        };
    }
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







