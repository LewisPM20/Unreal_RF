# Current Status

_Last updated: 2026-06-29._

## Runtime direction

The active product runtime is C#/.NET 8. Python is not part of the controller, worker, scheduler, persistence, dashboard backend, process supervision, or LAN orchestration runtime. Retired Python files live under `legacy_python/`; optional Unreal Python bridge scripts belong under `bridge/unreal_python/`.

## Solution layout

- `src/RenderFarm.Controller.Api`: ASP.NET Core Minimal API controller with endpoint group extension classes, dashboard static files, worker approval, scheduler endpoints, and optional UDP discovery broadcaster.
- `src/RenderFarm.Worker.Agent`: worker background services for heartbeat, controller endpoint resolution, capability detection, shared output validation, job polling, and Unreal process launch.
- `src/RenderFarm.Persistence`: SQLite repository implementation, schema bootstrap, schema version marker, indexes, pragmas, and scheduler transaction helper.
- `src/RenderFarm.Domain`: domain records and lifecycle enums.
- `src/RenderFarm.Shared`: DTOs, request/response contracts, JSON options, and mapping helpers.
- `tests/RenderFarm.Tests`: xUnit tests for scheduler behavior, JSON contracts, launcher behavior, dashboard asset coverage, and worker identity.

## Current controller endpoints

System and dashboard:

- `GET /`: static dashboard from `wwwroot/index.html`.
- `GET /health`: controller health and dashboard URL.
- `GET /api/system`: runtime summary counts and job state counts.
- `GET /api/config`: basic endpoint listing and runtime info.

Workers:

- `GET /api/workers`: list controller-known workers.
- `GET /api/workers/registered`: compatibility alias for registered workers.
- `GET /api/workers/status`: list workers with effective status, approval, scheduling mode, capabilities, and heartbeat age.
- `POST /api/workers/rescan`: compatibility refresh endpoint returning current worker status data.
- `GET /api/workers/{workerId}`: get one worker.
- `GET /api/workers/{workerId}/readiness?projectId=...&renderProfileId=...`: evaluate one worker against a project/profile.
- `GET /api/workers/{workerId}/validate-project-path?path=...`: validate a reported project path from heartbeat data.
- `GET /api/workers/{workerId}/validate-engine?version=...`: validate a reported Unreal engine version.
- `GET /api/workers/{workerId}/validate-output-root?path=...`: validate a reported shared output root.
- `POST /api/workers/heartbeat`: worker heartbeat upsert; new workers become pending until approved.
- `GET /api/workers/pending`: list pending workers.
- `POST /api/workers/{workerId}/approve`: approve a worker for scheduling.
- `POST /api/workers/{workerId}/reject`: reject a worker.
- `POST /api/workers/{workerId}/scheduling`: set operator scheduling mode: `Active`, `Draining`, or `Disabled`.
- `POST /api/workers/{workerId}/request-job`: worker pull endpoint for job assignment.

Projects and render profiles:

- `GET /api/projects`, `GET /api/projects/{projectId}`, `POST /api/projects`, `PUT /api/projects/{projectId}`, `DELETE /api/projects/{projectId}`.
- `GET /api/projects/{projectId}/readiness?renderProfileId=...`: readiness matrix across workers.
- `GET /api/projects/{projectId}/validate/worker/{workerId}`: project/profile readiness for one worker.
- `POST /api/projects/{projectId}/scan`: scan project assets using safe filesystem hints; optional Unreal bridge boundary is defined under `bridge/unreal_python/`.
- `GET /api/projects/export`, `POST /api/projects/import`: JSON import/export for projects and render profiles.
- `GET /api/render-profiles`, `GET /api/projects/{projectId}/render-profiles`, `GET /api/render-profiles/{profileId}`, `POST /api/render-profiles`, `PUT /api/render-profiles/{profileId}`, `DELETE /api/render-profiles/{profileId}`.
- `POST /api/render-profiles/{profileId}/duplicate`: copy an existing profile with a new id/display name.

Jobs and events:

- `GET /api/jobs`, `POST /api/jobs`, `GET /api/jobs/{jobId}`, `PUT /api/jobs/{jobId}`, `DELETE /api/jobs`.
- `GET /api/jobs/groups`.
- `POST /api/jobs/chunk-preview`: dry-run preview of deterministic frame chunks. This does not create child jobs or enable distributed chunk execution.
- `POST /api/jobs/{jobId}/cancel`, `POST /api/jobs/{jobId}/retry`.
- `POST /api/jobs/{jobId}/renew-lease`, `POST /api/jobs/{jobId}/start`, `POST /api/jobs/{jobId}/complete`, `POST /api/jobs/{jobId}/fail`.
- `POST /api/jobs/expire-leases`.
- `GET /api/jobs/{jobId}/attempts`, `POST /api/jobs/{jobId}/attempts`.
- `GET /api/jobs/{jobId}/events`, `POST /api/jobs/{jobId}/events`.
- `GET /api/events`.

Settings and compatibility queue endpoints:

- `GET /api/settings`, `GET /api/settings/{key}`, `PUT /api/settings/{key}`.
- `GET /api/queue/settings`, `POST /api/queue/settings`, `POST /api/queue/tick`.
- `DELETE /api/admin/state`: clears all persisted controller state. This is dashboard-visible and should be hardened in a later phase.

## Current DTOs and contracts

Shared DTOs live in `RenderFarm.Shared`:

- Worker contracts: `WorkerHeartbeatDto`, `WorkerDto`, `WorkerCapabilitiesDto`, `UnrealEngineInstallationDto`, `ProjectPathStatusDto`, `SharedOutputStatusDto`.
- Project/profile contracts: `ProjectProfileDto`, `WorkerProjectPathDto`, `RenderProfileDto`, `WorkerProjectReadinessDto`, `ReadinessMatrixDto`, `UnrealProjectScanRequest`, `UnrealProjectScanResultDto`, `PreparedRenderRequestDto`, `RenderPreparationResultDto`.
- Job contracts: `RenderJobDto`, `JobAttemptDto`, `JobEventDto`, `CreateRenderJobRequest`, `JobLeaseDto`, `JobAssignmentDto`, `JobLeaseRenewalRequest`, `JobStartRequest`, `JobCompletionRequest`, `JobFailureRequest`, `RenderArtifactSummaryDto`.
- Settings and validation contracts: `SettingDto`, `SharedOutputValidationRequest`, `SharedOutputValidationResult`, `SharedOutputPolicyDto`.
- Notification and planning contracts: `JobNotificationPayloadDto`, `ChunkPreviewRequest`, `ChunkPreviewItemDto`, `ChunkPreviewResponseDto`.

Enum JSON handling is centralized in `RenderFarmJson`, and controller/worker both use the shared options.

## Current DB schema

SQLite schema bootstrap happens in `SqliteRenderFarmRepository.InitializeAsync`. The repository configures each opened connection with:

- `PRAGMA foreign_keys = ON`
- `PRAGMA journal_mode = WAL`
- `PRAGMA busy_timeout = 5000`

Tables are created with `CREATE TABLE IF NOT EXISTS` and fresh databases include foreign keys for project/profile/job/attempt/lease/event relationships where practical. A `schema_migrations` table records baseline version 1. Indexes exist for common scheduler/dashboard queries, including jobs by state/priority/created date, jobs by assigned worker, leases by active/expiry, events by job/time, workers by status/heartbeat, and render profiles by project.

Scheduler multi-row state updates use `ISchedulerStateRepository.ApplyAsync` so job, attempt, lease, and event writes commit together. Existing databases migrate forward by additive bootstrap; rebuilding old tables to retrofit FK constraints is still a future migration task.

## Current job lifecycle

Job states live in `JobState`: `Created`, `Queued`, `Reserved`, `Running`, `ValidatingWorker`, `PreparingUnrealQueue`, `LaunchingUnreal`, `Rendering`, `VerifyingOutputs`, `Succeeded`, `Failed`, `CancelRequested`, `Cancelling`, `Cancelled`, `Stale`, `RetryWait`.

Current scheduler behavior:

- `CreateJobAsync` creates queued jobs and records a queued event.
- `RequestJobAsync` expires stale leases, promotes due `RetryWait` jobs, then assigns only eligible queued jobs to accepted workers with status `Online` or `Idle`.
- A lease and attempt are created transactionally when a job is reserved.
- Start, complete, fail, renew lease, and expire lease operations validate active leases where applicable.
- Legal state transitions are centralized in `JobStateMachine`; terminal `Succeeded`, `Failed`, and `Cancelled` states are terminal.
- A worker cannot complete/fail another worker's lease.
- Retry policy is category-based via `ConfiguredRetryPolicy`; retryable failures can requeue immediately or enter `RetryWait` with configurable delay/backoff.
- Expired leases mark attempts stale and requeue, retry-wait, or fail according to policy.

Current lifecycle gaps:

- Dashboard/API manual retry currently requeues only states allowed by the state machine; failed terminal jobs are not reopened directly.
- Full user-facing retry controls and chunk-specific retry workflows remain future work.
- Controller startup recovery for stale running jobs is still part of a later crash-recovery phase.

## Current worker lifecycle

Worker behavior:

- Worker identity uses manual `WorkerId` when provided; otherwise it persists a generated ID in a local identity file.
- Worker display name can be configured; otherwise it uses host/IP information.
- Worker sends periodic heartbeats with hostname, primary IP, service URL, status, agent version, capabilities, project paths, Unreal installations, and shared output status.
- New workers are persisted as pending until approved.
- Approved idle/online workers may request jobs.
- Worker polls controller for one job at a time in the current background loop.
- Worker resolves project/profile DTOs, writes a deterministic per-attempt render request JSON, builds an Unreal render command from structured data, launches Unreal through the C# process launcher, and posts complete/fail.

Worker hardening now in place:

- Worker writes a local current-job state file while executing and clears it after successfully reporting completion/failure.
- Heartbeat reports `Busy` and current job ID when local execution state exists.
- Worker startup reconciles any local state with the controller and does not blindly relaunch old jobs.
- The job loop is guarded so one worker runs one job at a time.
- Long renders renew their lease periodically.
- Process timeout and host cancellation kill the process tree where supported.
- Logs now include worker/job/attempt/lease context plus process id, command, exit code, and failure category.

Remaining worker gaps:

- There is no active controller-to-worker cancellation signal during a running render yet.
- Restart reconciliation is conservative: it clears the local marker and lets controller lease expiry/policy recover the job.
- Worker concurrency remains intentionally single-job only.

## Current security and notifications

- API token protection is optional. When `RenderFarm:Security:ApiToken` is configured, mutating worker, job, project, profile, settings, queue, and admin endpoints require `Authorization: Bearer <token>`.
- Read-only dashboard/API endpoints remain available without a token so local visibility is simple.
- The worker agent accepts `ApiToken` in configuration and sends the bearer token to the controller for heartbeat, scheduling, lease, and job lifecycle calls.
- The dashboard stores the operator token in browser `localStorage` and attaches it to mutating API calls.
- Terminal job notifications are optional. When `RenderFarm:Notifications:Enabled` and `WebhookUrl` are configured, the controller posts a generic JSON payload for succeeded, failed, and cancelled jobs according to the configured flags.
- Webhook failures are logged and do not roll back or block terminal job transitions.

## Current discovery behavior

- Manual `ControllerUrl` is still supported and has priority.
- If no manual URL is configured and worker discovery is enabled, the worker listens for UDP discovery announcements.
- Controller discovery broadcast is optional and disabled by default.
- Discovery announcements use service `renderfarm-controller`, URL, and machine name.
- If discovery fails, worker falls back to `http://127.0.0.1:9200/`.

Current discovery gaps:

- No controller identity trust or token validation yet.
- No dashboard settings for discovery.
- No blocklist/rate limiting for rejected workers yet.

## Current project/profile behavior

- Projects and render profiles are persisted in SQLite.
- A project supports a default `.uproject` path, engine version hints, optional output policy id, and worker-specific path mappings.
- A render profile supports MRQ queue, MRG graph, command template, or manual type, plus asset path, command template, default output type, chunking support flag, and free-form settings.
- Dashboard supports creating projects/profiles manually, importing legacy-style JSON, filling project fields from worker heartbeat data, deleting profiles, and deleting projects if no jobs depend on them.

Current project/profile status:

- Multiple profiles can exist per project.
- Profile duplication exists.
- JSON import/export exists for project/profile sets.
- Worker-specific project path mappings exist.
- Controller-side validation/readiness endpoints evaluate worker project paths, engine versions, output roots, disk, status from heartbeat data, and optional render-profile capability requirements stored in profile settings (`minCpuCores`, `minRamGb`, `minVramGb`, `gpuNameContains`).
- Project scanning exists using safe filesystem metadata and an optional Unreal Python bridge script.
- The dashboard can scan a project and create a render profile from discovered maps, level sequences, MRQ configs, or MRG assets without hand-copying asset paths.

Remaining project/profile gaps:

- The domain model still uses compact project/profile records; richer notes/enabled/timestamp/output-pattern fields are represented through profile settings for now rather than first-class columns.
- The controller does not yet push validation commands to live worker processes; readiness is based on the latest worker heartbeat.

## Current dashboard capabilities

The dashboard is a single static HTML file at `src/RenderFarm.Controller.Api/wwwroot/index.html` with inline CSS and JS.

Current capabilities:

- Controller status and summary counts.
- Worker list with status, approval, heartbeat, capabilities, Unreal installs, project paths, and shared output roots.
- Worker search/status filter.
- Pending worker approval panel with approve/reject actions.
- Shared output root status list.
- Project and render profile list/create/delete forms.
- Job table and queue job form.
- Clear jobs, reset DB, expire leases, refresh/rescan controls.
- Legacy JSON import for projects/profiles.
- Chunking fields are preview-only: the dashboard can call the dry-run chunk planner and show frame ranges, but queueing still creates one normal render job.

Current dashboard gaps:

- Still a single large file with inline CSS/JS.
- No dedicated details views for jobs/workers/projects.
- No real-time updates; it polls every 10 seconds.
- No modal confirmation for high-risk reset; browser confirm is used.
- No log tail/output artifact view.

## Current chunking limitations

Chunking is intentionally disabled for execution. `CreateRenderJobRequest` includes `FrameStart`, `FrameEnd`, and `ChunkSizeFrames`, and `RenderProfile` has `SupportsChunking`, but the scheduler rejects frame/chunk requests with an explicit error. `RenderChunkPlanner` now provides deterministic inclusive frame-range splitting for future parent/child jobs and is covered by tests.

Reason: Unreal MRQ/MRG frame-range behavior and output naming must be proven deterministic before distributed chunk execution is enabled. The UI continues to show chunk fields as gated rather than pretending distributed chunk renders work.

## Repository hygiene status

The repo now has a `.gitignore` for .NET build output, local SQLite files, logs, Python caches, and local render/output folders. Generated artifacts should not be tracked:

- `bin/`, `obj/`
- `*.db`, `*.db-shm`, `*.db-wal`
- `*.log`, `*.tmp`
- `__pycache__/`, `*.pyc`
- local render/output folders

## Recommended next phase

Phase 11 should split and professionalize the dashboard UX now that phases 6-10 have added readiness, scanning, deterministic preparation, and honest chunk-planning scaffolding.

## Recent operator hardening

- The dashboard job table opens a details drawer with job metadata, attempts, event history, and artifact summaries when completion events include verified output data.
- Workers poll their assigned job while Unreal is running. If the controller marks the job as cancel requested, the worker cancels the local render token and reports `CancelledByUser` through the normal job failure endpoint, which the scheduler records as terminal `Cancelled` rather than generic `Failed`.
- Successful worker renders now verify that the configured output directory contains non-empty expected media/image files before completing the job. Missing output is reported as `RenderOutputMissing` and is not retried by default.
- Controller startup runs a recovery service that expires stale leases and requeues or fails active jobs that no longer have a valid active lease according to the existing retry policy.
- Workers can be placed into `Active`, `Draining`, or `Disabled` scheduling mode. The scheduler assigns only `Active` workers, while heartbeat status remains separate from operator scheduling mode.
