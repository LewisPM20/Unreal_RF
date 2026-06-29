# Current Architecture

The active RenderFarm runtime is C#/.NET.

```text
RenderFarm.Controller.Api
  -> SQLite persistence through RenderFarm.Persistence
  -> controller-owned scheduler, job states, leases, attempts, and events
  -> worker heartbeat and job request endpoints

RenderFarm.Worker.Agent
  -> stable worker identity
  -> hostname/IP/capability detection
  -> Unreal installation detection
  -> shared output validation
  -> heartbeat loop to the C# controller
  -> controlled Unreal command launch and process supervision
```

Python runtime files have been retired to `legacy_python/`. They are not active controller, worker, scheduler, launcher, or persistence code. Future Python code is allowed only as optional Unreal Editor bridge scripts under `bridge/unreal_python/` and must be invoked by C# with a narrow JSON contract.

## Active C# Projects

- `src/RenderFarm.Domain/` - domain models and lifecycle enums.
- `src/RenderFarm.Shared/` - DTOs/contracts shared by controller and worker.
- `src/RenderFarm.Persistence/` - SQLite repositories and schema initialization.
- `src/RenderFarm.Controller.Api/` - ASP.NET Core controller API and scheduler.
- `src/RenderFarm.Worker.Agent/` - worker agent, capabilities, heartbeat, and Unreal launcher shell.
- `tests/RenderFarm.Tests/` - xUnit coverage for domain, scheduler, and process launcher behavior.

## Launching

Use PowerShell scripts in `scripts/`:

```powershell
.\scripts\start_controller.ps1 -HostName 127.0.0.1 -Port 9200
.\scripts\start_worker.ps1 -ControllerUrl http://127.0.0.1:9200 -WorkerId local-worker-01
```

Do not launch `controller_queue_ui.py`, `worker_server.py`, or `renderfarm_launcher.py`; those files are preserved only under `legacy_python/` for migration reference.


## Worker approval

Fresh worker heartbeats are persisted as pending. Approve them from the C# dashboard before they can reserve render jobs. Optional LAN discovery exists for convenience, but explicit `-ControllerUrl` is still the preferred production setup.
