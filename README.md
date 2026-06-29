# Unreal RenderFarm

C#/.NET 8 is the active runtime for the Unreal RenderFarm. The controller owns the dashboard, queue, scheduler, leases, worker approval, and SQLite persistence. Worker agents report machine capabilities, heartbeat to the controller, renew leases, launch Unreal, and validate render output.

Python runtime files have been moved to `legacy_python/` for reversible migration reference only. Optional Unreal Editor Python bridge scripts belong under `bridge/unreal_python/` and must be invoked by C#.

## Active components

- `src/RenderFarm.Controller.Api` - ASP.NET Core controller API, dashboard, scheduler, job leases, attempts, and events.
- `src/RenderFarm.Worker.Agent` - worker heartbeat, capability detection, shared-output validation, and Unreal process launching.
- `src/RenderFarm.Persistence` - SQLite persistence.
- `src/RenderFarm.Domain` and `src/RenderFarm.Shared` - domain models and DTO contracts.
- `scripts/` - developer-friendly launch and publish scripts.

## Build

```powershell
dotnet restore .\RenderFarm.sln
dotnet build .\RenderFarm.sln -c Debug
```

## Start in developer/script mode

From the repo root, start the controller:

```powershell
.\scripts\start-controller.ps1 -HostName 127.0.0.1 -Port 9200
```

Open the dashboard:

```text
http://127.0.0.1:9200/
```

Start a worker in a second terminal:

```powershell
.\scripts\start-worker.ps1 `
  -ControllerUrl http://127.0.0.1:9200 `
  -WorkerId local-worker-01 `
  -ProjectPaths "C:\Path\To\Project.uproject" `
  -SharedOutputRoots "C:\RenderFarmOutput" `
  -UnrealSearchRoots "C:\Program Files\Epic Games"
```

Useful script options:

- Controller: `-HostName`, `-Port`, `-DiscoveryEnabled`, `-DiscoveryUrl`, `-ApiToken`, `-DatabasePath`, `-LogLevel`, `-Configuration`.
- Worker: `-ControllerUrl`, `-WorkerId`, `-DisplayName`, `-ApiToken`, `-ProjectPaths`, `-SharedOutputRoots`, `-UnrealSearchRoots`, `-RenderTimeoutSeconds`, `-LogLevel`, `-Configuration`.

The underscore script names still work for compatibility: `start_controller.ps1` and `start_worker.ps1`.

## Role launcher foundation

For a future product-style launcher, use the role launcher script now:

```powershell
.\scripts\start-renderfarm.ps1 -Role controller -SaveRole
```

or:

```powershell
.\scripts\start-renderfarm.ps1 -Role worker -ControllerUrl http://CONTROLLER_IP:9200 -SaveRole
```

After a role is saved, this command launches the saved role:

```powershell
.\scripts\start-renderfarm.ps1
```

Saved role settings live in `%LOCALAPPDATA%\RenderFarm\app-role.json`. Valid roles are `controller` and `worker`; rerun the command with another `-Role` and `-SaveRole` to change the machine role.

## Worker approval, discovery, and security

Workers use a stable generated identity file unless `-WorkerId` is supplied. First-time workers appear as pending in the dashboard and must be approved before they can reserve jobs.

Manual `-ControllerUrl` remains the preferred production setup. Optional LAN discovery can be tested with:

```powershell
.\scripts\start-controller.ps1 -HostName 0.0.0.0 -Port 9200 -DiscoveryEnabled -DiscoveryUrl http://CONTROLLER_IP:9200
.\scripts\start-worker.ps1 -DiscoveryEnabled
```

If controller API token protection is enabled, pass the same token to the controller and worker scripts with `-ApiToken`, and save it in the dashboard Settings panel for browser actions.

## Packaging status

Current publishing creates a distributable package with a launcher, controller, worker, config templates, docs, and installer scripts:

```powershell
.\scripts\publish_apps.ps1 -Configuration Release -Runtime win-x64 -Mode framework-dependent -Zip
```

Basic per-user install from the package folder:

```powershell
.\publish\RenderFarm\installer\install_renderfarm.ps1 -Role controller -CreateShortcuts
```

Worker install example:

```powershell
.\publish\RenderFarm\installer\install_renderfarm.ps1 -Role worker -ControllerUrl http://CONTROLLER_IP:9200 -WorkerId worker-pc-01 -CreateShortcuts
```

The package includes `RenderFarm.Launcher.exe` as the product-mode entrypoint. For internal verification on machines where runtime-specific publish is blocked, run `dotnet build .\RenderFarm.sln -c Debug` and then `.\scripts\publish_apps.ps1 -Configuration Debug -Portable -AllowBuildOutputFallback`. For end-user distribution, build the Inno Setup setup EXE with `.\scripts\build_installer.ps1 -Configuration Release -Runtime win-x64 -ProductVersion 0.1.0` after placing the .NET Desktop Runtime installer under `installer\redist`.

## More docs

- `docs/csharp_build_test_guide.md` - local and LAN smoke tests.
- `docs/current_status.md` - current endpoints, lifecycle, persistence, dashboard, discovery, and limitations.
- `docs/packaging-plan.md` - setup EXE direction and role-selection plan.

Frame range and chunking fields are modelled but intentionally disabled for execution until Unreal MRQ/MRG per-chunk output can be proven deterministic.


