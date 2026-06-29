# C#/.NET Takeover Plan

Status: C#/.NET is the product runtime. Retired Python runtime files live under `legacy_python/`; optional Unreal bridge scripts, if ever needed, belong under `bridge/unreal_python/`. See `docs/python_migration_boundary.md`.

## Why C#

C#/.NET is a good fit for the controller and worker agent because this tool is Windows/LAN-heavy and needs:

- Strong long-running process behaviour.
- Windows service/tray app support.
- Good filesystem and process APIs.
- Strong typing.
- SQLite integration.
- Good HTTP/WebSocket support.
- Easier packaging than a loose Python service.

Python should remain the Unreal bridge language because Unreal automation and Movie Render Queue preparation are easiest there.

## Intended solution structure

```text
src/
  RenderFarm.Domain/
  RenderFarm.Shared/
  RenderFarm.Controller.Api/
  RenderFarm.Worker.Agent/

tests/
  RenderFarm.Tests/
```

## Boundary

C# owns:

- Worker registry.
- Scheduler.
- Job leases.
- Job state machine.
- SQLite persistence.
- Shared output validation orchestration.
- Worker process supervision.
- Controller API/dashboard backend.

Python owns:

- Unreal asset scanning.
- MRQ/MRG queue preparation.
- Per-version Unreal API shims.
- Generated queue creation.

## Worker execution shape

If Unreal Python bridge code becomes necessary, the C# worker may run bridge commands like:

```text
python bridge/unreal_python/prepare_mrq_job.py --job <job.json> --result <result.json>
UnrealEditor-Cmd.exe <project.uproject> -MoviePipelineLocalExecutorClass=... <generated queue args>
```

C# supervises the process, captures logs, validates outputs, and reports status.

## Current C# skeleton

The files in `src/` are the active C# runtime. Use `scripts/start_controller.ps1` and `scripts/start_worker.ps1` to launch the controller and worker.
