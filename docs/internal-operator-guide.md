# RenderFarm Internal Operator Guide

RenderFarm's active runtime is C#/.NET 8. The controller owns scheduling, SQLite persistence, project/render setup, shared output defaults, and Unreal launch configuration. Python is not part of the active controller, worker, scheduler, or persistence runtime; any Python left in the repo is legacy reference or optional Unreal bridge work.

## Production Model

- Controller/database/dashboard are the source of truth for render settings.
- Workers only need connection identity: controller URL, optional worker ID, optional display name, and optional API token.
- Workers may report diagnostics such as discovered Unreal installs, project paths, and writable output roots, but those reports are advisory.
- When a job is assigned, the controller sends the worker a resolved execution payload containing the Unreal executable, project path, render profile, output directory, log directory, timeout, and template variables.
- The worker validates local access to the controller-supplied project/output paths, launches Unreal, renews the lease, and reports completion or failure.

## Start Controller

1. Launch `RenderFarm.Launcher.exe`.
2. Choose `Controller`.
3. Keep host `127.0.0.1` for local testing, or bind to the LAN IP/host name for another PC.
4. Start the selected role.
5. Open `http://127.0.0.1:9200/` and confirm `/health` responds.

The launcher stores controller data under `%LOCALAPPDATA%\RenderFarm`. The controller database defaults to `%LOCALAPPDATA%\RenderFarm\Controller\renderfarm.db` so upgrades do not wipe queue/project state.

## Configure Render Defaults

Open Dashboard > Settings > Controller Render Defaults.

Set these before queueing production work:

- Unreal executable path, for example `C:\Program Files\Epic Games\UE_5.7\Engine\Binaries\Win64\UnrealEditor-Cmd.exe`.
- Unreal search root, if you prefer storing the engine root instead of the executable.
- Shared output root, for example `\\SERVER\RenderFarmOutput`.
- Output subfolder pattern, usually `{JobId}` or `{ProjectName}\{JobId}`.

Project and render profiles can override these defaults. Workers validate the resolved output path when they receive a job.

## Start Worker

1. Launch `RenderFarm.Launcher.exe` on the worker PC.
2. Choose `Worker`.
3. Enter the controller URL, for example `http://CONTROLLER_IP:9200`.
4. Optionally set a stable worker ID and display name.
5. Start the selected role.
6. Approve the worker in Dashboard > Workers.

Do not configure Unreal search roots, project paths, MRQ/MRG presets, command templates, or output roots on the worker in the normal flow. Those are controller-owned settings.

## Add Project And Render Profile

1. Use New Render or Add project.
2. Register the `.uproject` path as it should be used by workers. If a worker has a different local path, add a worker-specific mapping on the project.
3. Create a render profile with map/level, MRQ/MRG asset, command template, or manual settings.
4. Add optional profile overrides for Unreal executable, output root, output pattern, timeout, and hardware requirements only when that profile needs them.

## Queue Render

1. Click New Render.
2. Select or create the project.
3. Select or create the render setup.
4. Select an output path or leave it blank to use the controller render default.
5. Review readiness and queue the render.
6. Open the job details drawer to inspect attempts, events, failure category, output path, and artifacts.

## Diagnostics

Use Dashboard > Diagnostics > Copy diagnostics for internal support. It includes controller version, controller URL, database path, controller-owned render defaults, counts, worker heartbeats, output reports, and recent warnings. Secrets are not intentionally included in diagnostics; avoid pasting local tokens if you add them manually.

## Common Issues

- Controller not starting: check whether port 9200 is already in use, whether the install folder is writable, and whether the database path exists under `%LOCALAPPDATA%\RenderFarm\Controller`.
- Dashboard not loading: confirm the controller process is running and `http://127.0.0.1:9200/health` returns OK.
- Worker not appearing: confirm the worker's Controller URL points to the controller, check firewall rules on the controller PC, and verify both machines are on the same LAN.
- Worker pending approval: approve it from Dashboard > Workers before it can receive jobs.
- Output root invalid on worker: make sure the controller shared output root is reachable from the worker account and writable.
- Unreal path/config wrong: set Controller Render Defaults or profile overrides to the exact worker-visible Unreal executable path.
- Project path inaccessible from worker: use a shared path or add a worker-specific project mapping.
- Render failed: open job details, copy failure details, and inspect the attempt log path and job events.
## Recover Failed Or Stale Jobs

Terminal jobs stay terminal. A failed job should not be edited back into `Queued`, because that destroys the audit trail and can relaunch work with stale attempt data.

Use this flow instead:

1. Open Dashboard > Queue.
2. Open the failed job details drawer.
3. Click `Retry as new job`.
4. Review the confirmation message and continue only if the failed setup has been corrected or you intentionally want to rerun it.
5. Confirm the new job appears as queued and the original job remains failed.

State-specific operator actions:

- `Running`, `LaunchingUnreal`, `Rendering`, `VerifyingOutputs`: use `Cancel render` if the job should stop.
- `Failed`: use `Retry as new job`; the retry keeps a traceability event pointing back to the source job.
- `RetryWait`: wait for the configured retry delay or inspect retry policy. The controller will promote the job when it is due.
- `Stale`: let controller startup/lease recovery apply the retry policy, then inspect the job history before retrying manually.
- `Succeeded` and `Cancelled`: no destructive default action is shown.

## Startup Recovery

On controller startup, the controller reconciles SQLite state before scheduling new work. It expires invalid leases, marks stale attempts, and either requeues, moves to retry-wait, or fails affected jobs according to the retry policy. It never marks a job succeeded unless output validation already proved success.

Open Dashboard > Diagnostics to see recent startup recovery activity. Recovery events are idempotent; running startup reconciliation twice should not create duplicate attempts or reopen terminal jobs.

## Duplicate Controller Protection

The controller uses a named Windows mutex scoped to the installation folder. If a second controller from the same install starts, it exits with a clear message. If you see that message, close the existing controller first or use the running dashboard instead of starting another backend.

Separate copied installs may still be started intentionally. For normal internal testing, avoid running two controllers against the same database or port.

## Render Watchdog And Process Safety

The worker launches render processes through the C# process launcher. The launcher captures bounded stdout/stderr, writes attempt logs, reports non-zero exits, honors cancellation, and can stop a render that exceeds the configured timeout or produces no stdout/stderr progress for too long.

Relevant worker configuration lives under `RenderFarm:Process`:

- `MaxCapturedOutputCharacters`: maximum combined captured output kept in memory for diagnostics.
- `NoProgressTimeoutSeconds`: optional watchdog window. `0` disables the no-progress watchdog.

Cleanup is conservative. The worker stops the process tree it directly launched for the active attempt, but it does not kill unrelated Unreal Editor sessions or uncertain orphan processes.
