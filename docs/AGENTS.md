# AGENTS.md - Unreal RenderFarm

This repository is a LAN Unreal Engine render farm being moved into a production-style C#/.NET render orchestration tool.

## Product direction

The project must remain a standalone render farm controller/worker system with Unreal integration. Do not turn it into only an Unreal plugin.

Preferred long-term split:

- C#/.NET: controller API, worker agent, scheduler, SQLite persistence, Windows service/desktop app, process supervision.
- Python: optional Unreal bridge scripts only, for MRQ/MRG preparation, asset/project scanning, or editor automation that cannot be done safely from C# command-line launch.
- Unreal C++ plugin: optional future submit/status helper inside the editor.

## Current runtime

The active runtime is C#/.NET:

- `src/RenderFarm.Controller.Api/` - controller API, scheduler, leases, and job orchestration.
- `src/RenderFarm.Worker.Agent/` - worker heartbeat, capability detection, shared-output validation, and Unreal process launch.
- `src/RenderFarm.Persistence/` - SQLite persistence.
- `src/RenderFarm.Domain/` and `src/RenderFarm.Shared/` - domain model and DTO contracts.
- `scripts/*.ps1` - C# runtime launch helpers.

Retired Python runtime files live under `legacy_python/`. Do not launch them as controller, worker, scheduler, persistence, or dashboard backend. Optional Unreal bridge scripts belong under `bridge/unreal_python/`.

## Highest priorities

1. Do not break existing MRQ rendering.
2. Prefer shared network output roots over per-worker local output for now.
3. Make workers self-report capabilities and heartbeat status.
4. Make project/render profiles first-class instead of hard-coding one project.
5. Make failures explainable with structured categories.
6. Keep C# as the product runtime.

## Coding rules

### Python

- Do not add Python controller, worker, scheduler, persistence, launcher, or dashboard runtime features.
- New Python is allowed only for optional Unreal Editor bridge scripts under `bridge/unreal_python/`.
- Bridge scripts must use explicit JSON input/output contracts and be invoked by C# without shell command strings.

### C#

- Target .NET 8 or later.
- Enable nullable reference types.
- Use file-scoped namespaces.
- Use async APIs with `CancellationToken`.
- Use dependency injection and strongly typed options.
- Do not expose database entities directly from API endpoints.
- Add tests when behaviour becomes non-trivial.

## Batch files

Batch launchers have been moved into `legacy_batch/`. New work should prefer:

- C# launcher commands in `scripts/`.
- PowerShell scripts if needed.
- C# worker/controller executables once packaged.

Do not add new `.bat` launchers unless specifically requested.

## Useful docs

Read these before major changes:

- `docs/architecture_current.md`
- `docs/roadmap.md`
- `docs/codex_handoff.md`
- `docs/csharp_takeover.md`
- `docs/shared_output.md`
- `docs/python_migration_boundary.md`
