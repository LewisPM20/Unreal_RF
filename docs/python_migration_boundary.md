# Python Migration Boundary

## Runtime Policy

C#/.NET is the product runtime for the render farm. The controller, worker agent, scheduler, job leases, state transitions, persistence, shared-output validation, process supervision, dashboard backend, and LAN orchestration live in C#.

Python runtime files have been retired under `legacy_python/`. Python may return only as optional Unreal-specific bridge scripts under `bridge/unreal_python/`, where Unreal Editor Python APIs provide information that cannot be obtained safely through command-line launch alone.

## Python File Classification

| File | Classification | Notes |
| --- | --- | --- |
| `legacy_python/controller_queue_ui.py` | safe_to_retire | Replaced by C# controller endpoints, scheduler, and persistence. |
| `legacy_python/worker_server.py` | legacy_reference | Replaced by C# worker agent; retained only as MRQ/MRG bridge reference. |
| `legacy_python/renderfarm_db.py` | safe_to_retire | Replaced by `RenderFarm.Persistence`. |
| `legacy_python/renderfarm_launcher.py` | safe_to_retire | Replaced by PowerShell launch scripts for C# entrypoints. |
| `legacy_python/controller_queue_ui_phase9_1_full.py` | legacy_reference | Snapshot of older Python controller behavior. |
| `legacy_python/worker_server_phase9_1_full.py` | legacy_reference | Snapshot of older Python worker and Unreal queue preparation behavior. |
| `legacy_python/renderfarm_launcher_phase9_1_fixed.py` | legacy_reference | Snapshot launcher. |
| `legacy_python/phase10_verify_features.py` | safe_to_retire | Python-era verification script; C# tests now cover active runtime behavior. |
| `legacy_python/phase9_1_verify_features.py` | safe_to_retire | Older Python-era verification script. |

No current file is classified as `required_unreal_bridge`. The bridge folder exists for future minimal Unreal-specific scripts only.

## Optional Unreal Bridge Boundary

C# can do directly:

- Resolve and validate Unreal executable paths.
- Validate `.uproject` paths and configured project roots.
- Validate shared output paths and expected output roots.
- Construct render command arguments without arbitrary shell execution.
- Launch `UnrealEditor-Cmd.exe` or another configured Unreal executable.
- Supervise process lifetime, cancellation, timeouts, and termination.
- Capture stdout, stderr, and per-attempt log files.
- Classify process exit failures and report job state transitions.
- Own all controller, worker, scheduler, lease, and persistence behavior.

Unreal Python may still be useful for:

- Inspecting Unreal assets through Editor APIs.
- Discovering maps, Level Sequences, Movie Render Queue assets, and Movie Render Graph assets.
- Creating or validating queue assets when command-line arguments are not enough.
- Handling engine-version-specific Movie Render Pipeline API differences.
- Producing Unreal-native validation details before C# launches the render command.

If a bridge is needed, C# invokes it safely:

- Use an allowlisted script path from `bridge/unreal_python/`.
- Start Python as a child process directly with an argument list, never through a shell command string.
- Pass a per-job JSON input file and require a per-job JSON output file.
- Validate all paths in the request and response before launch or persistence.
- Apply timeout and cancellation from the C# attempt.
- Capture bridge stdout/stderr into the same attempt log folder.
- Treat bridge failures as render preparation failures, not scheduler failures.

Python must not own job creation, worker registration, scheduling, leases, retries, SQLite writes, dashboard backend behavior, or LAN orchestration.
