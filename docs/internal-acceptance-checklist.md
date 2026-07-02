# RenderFarm Internal Acceptance Checklist

_Last updated: 2026-07-01._

Use this checklist before handing a build to another internal user. The active runtime is C#/.NET 8. Python must not be used for controller, worker, scheduler, persistence, dashboard backend, process supervision, or LAN orchestration.

## 1. Build And Package

1. From the repo root, run `dotnet restore .\RenderFarm.sln`.
2. Run `dotnet build .\RenderFarm.sln -c Debug`.
3. Publish a framework-dependent package with `.\scripts\publish_apps.ps1 -Configuration Release -Runtime win-x64 -Mode framework-dependent`.
4. Confirm the package has this shape:
   - `publish\RenderFarm\RenderFarm.Launcher.exe`
   - `publish\RenderFarm\controller\RenderFarm.Controller.Api.exe`
   - `publish\RenderFarm\controller\wwwroot\index.html`
   - `publish\RenderFarm\worker\RenderFarm.Worker.Agent.exe`
5. Run `.\publish\RenderFarm\RenderFarm.Launcher.exe --show-config` and confirm it prints settings without crashing.

## 2. Controller First Run

1. Start `RenderFarm.Launcher.exe`.
2. Select Controller.
3. Use `127.0.0.1` and port `9200` for local testing.
4. Click Start selected role.
5. Confirm the launcher reports the dashboard is ready.
6. Open `http://127.0.0.1:9200/` and confirm the dashboard loads.
7. Open `http://127.0.0.1:9200/health` and confirm it returns controller health JSON.
8. Open Dashboard > Diagnostics and confirm:
   - database path is under `%LOCALAPPDATA%\RenderFarm\Controller` unless explicitly configured,
   - dashboard asset is present,
   - API token is shown only as `configured (redacted)` or `not configured`,
   - launcher/worker package layout indicators are understandable.

## 3. Worker First Run

1. Start `RenderFarm.Launcher.exe` on the worker machine or second local terminal.
2. Select Worker.
3. Enter `http://127.0.0.1:9200` for local testing or `http://CONTROLLER_IP:9200` for LAN testing.
4. Set a clear display name such as `Local Worker 01`.
5. Start the worker.
6. Confirm it appears in Dashboard > Workers as pending.
7. Approve the worker.
8. Confirm heartbeat age updates and status becomes idle/online when it is not rendering.
9. Confirm the worker log says the heartbeat reached the controller and whether it is approved or pending.

## 4. Controller-Owned Render Setup

1. In Dashboard > Settings, set Controller Render Defaults:
   - Unreal executable path visible to workers,
   - shared output root visible to workers,
   - output subfolder pattern such as `{ProjectName}\{JobId}`.
2. Create or import a project.
3. Confirm the `.uproject` path is visible from the worker. Add a worker-specific mapping if each machine uses a different drive/path.
4. Create a render profile with the correct map/level and MRQ/MRG asset, or a safe command template.
5. Confirm readiness shows at least one approved worker eligible.

## 5. Queue And Execute A Local Smoke Render

1. Queue a render from the saved project/profile.
2. Confirm the job starts as queued and then moves to reserved/running/rendering.
3. Open the job details drawer and confirm attempts/events populate.
4. Confirm the worker creates a per-attempt render request JSON and log path.
5. When the render finishes, confirm the job ends in succeeded only if output validation finds non-empty expected output files.
6. Confirm the completed job shows output/artifact details in the dashboard.

## 6. Failure And Recovery Checks

1. Queue a job with an intentionally bad project path. It should fail with a validation/path failure instead of launching Unreal.
2. Queue a job with an unresolved command-template token such as `{MissingToken}`. It should fail before launch with a clear command validation error.
3. Stop a worker while a job is reserved/running.
4. Wait for lease expiry or click Expire leases in the dashboard.
5. Confirm the job is requeued, retry-wait, or failed according to retry policy, and that an event explains the transition.
6. Restart the controller and confirm startup recovery does not reopen terminal jobs.
7. Confirm due retry-wait jobs are promoted automatically by controller lease recovery or by the next scheduler operation.

## 7. LAN Smoke Test

1. On the controller PC, bind the controller to the LAN IP only when you intentionally want LAN access.
2. Allow TCP 9200 through Windows Firewall using the provided helper only after reviewing the prompt.
3. On the worker PC, set Controller URL to `http://CONTROLLER_IP:9200`.
4. Confirm the worker appears pending, then approve it.
5. Queue a render whose project path, Unreal executable, and shared output root are visible to the worker account.
6. Confirm the job runs on the worker and writes output to the shared root.
7. Confirm diagnostics do not expose API tokens or local secrets.

## 8. Uninstall And Upgrade Sanity

1. Replace the published package with a newer build.
2. Confirm `%LOCALAPPDATA%\RenderFarm\app-role.json` and the controller database survive the replacement.
3. Uninstall using the provided script or Windows uninstall entry if using the setup build.
4. Confirm product binaries are removed and user settings remain unless explicitly removed.

## Known Internal Limits

- Worker service install remains an explicit elevated script, not an automatic installer step.
- Dashboard updates still use polling, not SSE/WebSockets.
- Log tailing from the browser is not implemented yet.
- Distributed chunk execution remains disabled until Unreal MRQ/MRG frame-range behavior is proven deterministic.

## 9. Recovery, Retry, And Process Safety

1. Queue a render and open the job details drawer. Confirm status, attempts, events, command/log copy actions, artifacts, and diagnostics export are visible without using the database or filesystem manually.
2. Cancel a running/rendering job from the job details drawer. Confirm the in-app confirmation modal appears and the job records a cancellation event.
3. Force a job to fail with a bad path or fake renderer failure. Confirm the failed terminal job shows `Retry as new job`, not an in-place reopen action.
4. Use `Retry as new job` and confirm a new queued job appears. The original job must remain failed and both jobs should have events linking the source and retry job IDs.
5. Put a job into retry-wait through retry policy. Confirm the dashboard explains that it is waiting for the configured retry delay rather than offering a destructive action.
6. Start the controller, then try starting a second controller from the same install. The second process should exit clearly instead of silently running another scheduler.
7. Restart the controller after a reserved/running job loses its lease. Confirm Diagnostics shows startup reconciliation activity and the job moves according to retry policy.
8. Confirm a valid active lease with a fresh worker heartbeat is not marked stale by startup recovery.
9. Run a fake or harmless command that exits non-zero. Confirm the attempt fails with a clear failure reason and bounded captured logs.
10. Run a fake or harmless command that produces no output past the configured watchdog window. Confirm it is stopped safely and recorded as a timeout/stuck failure.
11. Confirm chunk execution is still disabled and no UI path implies distributed chunks are production-ready.
12. Ask a developer/operator to run `dotnet test .\RenderFarm.sln -c Debug` and review any failures before handing the build to another tester.
