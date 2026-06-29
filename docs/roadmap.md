# Unreal RenderFarm Roadmap

## Phase 10 - Architecture Hardening

Goal: preserve Python-era learnings while the C# runtime owns the product.

### Done in this package

- Added repo guidance in `AGENTS.md` and `docs/AGENTS.md`.
- Added architecture/roadmap/Codex handoff docs.
- Moved `.bat` launchers into `legacy_batch/`.
- Added the Python-era `renderfarm_db.py` SQLite helper, now retired under `legacy_python/`.
- Added controller SQLite init and job mirroring.
- Added worker heartbeat registry endpoint.
- Added registered worker listing endpoint.
- Added worker capability endpoint.
- Added optional worker-to-controller heartbeat loop through environment variables.
- Added shared output validation endpoint on workers.
- Added controller-side shared output validation before `/run-mrq`.
- Added early C#/.NET skeleton.

### Still needed

- Make SQLite the primary source of truth.
- Merge configured workers and heartbeat workers into one scheduler registry.
- Add real job leases.
- Add retry policy by failure category.
- Add readiness matrix for worker/project combinations.
- Add dashboard UI for registered workers, shared output health, and failure categories.
- Add project/profile scanning through Unreal Python.

## Phase 11 - Project and Render Profile Scanner

Goal: reduce manual setup.

- Scan `.uproject` locations.
- Detect Unreal version requirements.
- Discover maps.
- Discover Level Sequences.
- Discover Movie Render Queue assets.
- Discover Movie Render Graph assets when available.
- Cache findings into project/render profiles.
- Add rescan API/button.

## Phase 12 - C#/.NET Takeover

Goal: move app infrastructure out of Python while keeping Unreal Python automation.

- Build `RenderFarm.Controller.Api` as the real controller.
- Build `RenderFarm.Worker.Agent` as the real worker agent.
- Move scheduling, leases, registry, persistence and validation into C#.
- Keep Python bridge scripts for Unreal-specific actions.
- Package worker as a Windows service or tray app.
- Package controller as a local dashboard/API.
- Retired Python runtime files live under `legacy_python/`; optional Unreal bridge scripts belong under `bridge/unreal_python/`.

## Phase 13 - Optional Unreal Plugin

Goal: convenience inside Unreal Editor.

- Submit current sequence/queue to the farm.
- Register project with the controller.
- Publish render profiles.
- See worker status inside editor.
- Validate project setup before submission.

The plugin must remain optional; the standalone farm remains the core product.
