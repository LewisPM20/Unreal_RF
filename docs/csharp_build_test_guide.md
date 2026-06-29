# C# Build And Test Guide

This guide targets the C# runtime. Retired Python files live under `legacy_python/`; optional future Unreal-specific bridge scripts belong under `bridge/unreal_python/`.

## Requirements

Both controller and worker machines need:

- Windows 10/11 or another OS supported by .NET 8 and Unreal for the intended render workload.
- .NET 8 SDK for development/script mode, or the .NET 8 Desktop Runtime for framework-dependent published binaries.
- Network access between workers and the controller HTTP port.
- Matching project files and access to any required Unreal assets.
- Read/write access to the shared output location.

Worker machines also need:

- Unreal Engine installed for the project version being rendered.
- A valid `.uproject` path.
- Permission to launch Unreal command-line processes.
- Firewall rules allowing outbound HTTP to the controller.

The controller machine also needs:

- A writable location for the SQLite database.
- Firewall rules allowing inbound HTTP from workers when running over LAN.

## Local Single-PC Smoke Test

1. Restore dependencies:

   ```powershell
   dotnet restore .\RenderFarm.sln
   ```

2. Build the C# runtime:

   ```powershell
   dotnet build .\RenderFarm.sln -c Debug
   ```

3. Start the controller:

   ```powershell
   .\scripts\start-controller.ps1 -HostName 127.0.0.1 -Port 9200
   ```

4. Open the dashboard:

   ```text
   http://127.0.0.1:9200/
   ```

5. In a second terminal, start the worker agent:

   ```powershell
   .\scripts\start-worker.ps1 `
     -ControllerUrl http://127.0.0.1:9200 `
     -WorkerId local-worker-01 `
     -ProjectPaths "C:\Path\To\Project.uproject" `
     -SharedOutputRoots "C:\RenderFarmOutput" `
     -UnrealSearchRoots "C:\Program Files\Epic Games"
   ```

6. Approve the pending worker in the dashboard.

7. Confirm the controller sees the worker:

   ```powershell
   Invoke-RestMethod http://127.0.0.1:9200/api/workers
   ```

8. Create or import a project and render profile, then queue a job from the dashboard.

9. Open the job details drawer from the Jobs table. Click Close, then reopen the same job details row. Closing the details drawer must not cancel or mutate the job.

## Two-PC LAN Smoke Test

1. On the controller PC, find the LAN IP address:

   ```powershell
   ipconfig
   ```

2. Open the chosen controller port in Windows Firewall, for example `9200`.

3. Start the controller bound to the LAN:

   ```powershell
   .\scripts\start-controller.ps1 -HostName 0.0.0.0 -Port 9200
   ```

4. On the worker PC, confirm the controller is reachable:

   ```powershell
   Invoke-RestMethod http://CONTROLLER_IP:9200/health
   ```

5. Configure and start the worker:

   ```powershell
   .\scripts\start-worker.ps1 `
     -ControllerUrl http://CONTROLLER_IP:9200 `
     -WorkerId worker-pc-01 `
     -ProjectPaths "D:\Projects\Example\Example.uproject" `
     -SharedOutputRoots "\\SERVER\RenderFarmOutput" `
     -UnrealSearchRoots "C:\Program Files\Epic Games"
   ```

6. On the controller dashboard, approve the worker and confirm heartbeat/capabilities appear.

7. Submit a job whose project/profile matches the worker capabilities.

8. Confirm a second worker request does not receive the same leased job before the first lease is completed, failed, or expired.

9. Stop the worker long enough for the lease to expire, then call the lease-expiry endpoint and confirm the job is requeued or failed according to the configured retry rule:

   ```powershell
   Invoke-RestMethod -Method Post http://127.0.0.1:9200/api/jobs/expire-leases
   ```

## Role Launcher Smoke Test

The role launcher is the foundation for a future first-run product UI.

1. Show current saved role settings:

   ```powershell
   .\scripts\start-renderfarm.ps1 -ShowConfig
   ```

2. Save this machine as a controller:

   ```powershell
   .\scripts\start-renderfarm.ps1 -Role controller -SaveRole
   ```

3. Stop the controller and run the launcher again without `-Role`:

   ```powershell
   .\scripts\start-renderfarm.ps1
   ```

4. Save a worker role when setting up a render PC:

   ```powershell
   .\scripts\start-renderfarm.ps1 -Role worker -ControllerUrl http://CONTROLLER_IP:9200 -WorkerId worker-pc-01 -SaveRole
   ```

Saved settings live in `%LOCALAPPDATA%\RenderFarm\app-role.json`.

## Packaged Install Smoke Test

1. Build a distributable package:

   ```powershell
   .\scripts\publish_apps.ps1 -Configuration Release -Runtime win-x64 -Mode framework-dependent
   ```

2. Confirm the launcher can read settings without starting a role:

   ```powershell
   .\publish\RenderFarm\RenderFarm.Launcher.exe --show-config
   ```

3. Install the package for the current user as a controller:

   ```powershell
   .\publish\RenderFarm\installer\install_renderfarm.ps1 -Role controller -CreateShortcuts
   ```

4. Start the installed launcher:

   ```powershell
   & "$env:LOCALAPPDATA\RenderFarm\Product\RenderFarm.Launcher.exe"
   ```

5. For a worker PC, install with controller details:

   ```powershell
   .\publish\RenderFarm\installer\install_renderfarm.ps1 `
     -Role worker `
     -ControllerUrl http://CONTROLLER_IP:9200 `
     -WorkerId worker-pc-01 `
     -DisplayName "Render Worker 01" `
     -CreateShortcuts
   ```

6. Optional worker service setup requires an elevated PowerShell session:

   ```powershell
   & "$env:LOCALAPPDATA\RenderFarm\Product\installer\install_worker_service.ps1" -ControllerUrl http://CONTROLLER_IP:9200 -WorkerId worker-pc-01 -Start
   ```

## Setup EXE Smoke Test

1. Place the Microsoft .NET 8 Desktop Runtime redistributable at:

   ```text
   installer\redist\windowsdesktop-runtime-8.x.x-win-x64.exe
   ```

2. Build the setup EXE:

   ```powershell
   .\scripts\build_installer.ps1 -Configuration Release -Runtime win-x64 -ProductVersion 0.1.0
   ```

3. Install `dist\RenderFarmSetup-0.1.0-win-x64.exe` on a clean Windows machine.

4. Open **RenderFarm Launcher** from the Start Menu, choose Controller or Worker, save settings, and verify the selected role starts.
## Worker approval, discovery, and token protection

First-time workers appear as pending in the dashboard. Approve trusted machines before queueing renders.

Manual controller URLs remain the preferred production setup. Optional LAN discovery can be smoke-tested with:

```powershell
.\scripts\start-controller.ps1 -HostName 0.0.0.0 -Port 9200 -DiscoveryEnabled -DiscoveryUrl http://CONTROLLER_IP:9200
.\scripts\start-worker.ps1 -DiscoveryEnabled
```

If token protection is enabled, start the controller and workers with the same token:

```powershell
.\scripts\start-controller.ps1 -ApiToken "change-me"
.\scripts\start-worker.ps1 -ControllerUrl http://127.0.0.1:9200 -ApiToken "change-me"
```

Then enter the token in the dashboard Settings panel for browser actions.

Frame chunking is currently gated. The API can preview deterministic frame chunks, but job execution remains a single normal render until Unreal MRQ/MRG output behavior is proven safe per chunk.

