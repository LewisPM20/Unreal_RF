# RenderFarm Packaging Plan

_Last updated: 2026-06-29._

## Current state

RenderFarm is a C#/.NET 8 product runtime with three packaging-facing entrypoints:

- `RenderFarm.Controller.Api`: ASP.NET Core controller, dashboard, scheduler, SQLite persistence, worker approval, and optional discovery/security.
- `RenderFarm.Worker.Agent`: worker process for heartbeat, capability reporting, lease renewal, Unreal launch, and output validation.
- `RenderFarm.Launcher`: lightweight WPF launcher that stores the machine role, opens the first-run/settings UI, and starts the published controller or worker executable.

Python is not part of the product runtime except optional Unreal bridge scripts under `bridge/unreal_python/`.

## Distribution direction

Use a small framework-dependent Windows package plus a branded Inno Setup setup EXE.

The runtime remains split into Controller and Worker executables because they have different machine responsibilities. A single role-selecting launcher is the operator-friendly front door and the installer shortcut target.

This keeps distribution practical:

- Framework-dependent publish keeps product files smaller.
- `PublishSingleFile=true` is used by default so each app publishes as a compact EXE plus required config/runtime metadata.
- The setup EXE can bundle the Microsoft .NET 8 Desktop Runtime redistributable for clean machines.
- Windows Service setup stays explicit because it needs elevation and a known controller URL.

## Implemented now

Developer/script mode:

- `scripts/start-controller.ps1`: friendly controller launcher.
- `scripts/start-worker.ps1`: friendly worker launcher.
- `scripts/start_controller.ps1` and `scripts/start_worker.ps1`: compatibility script names.
- `scripts/start-renderfarm.ps1`: PowerShell role launcher for development and early operator use.

Product/package mode:

- `src/RenderFarm.Launcher`: WPF launcher UI plus CLI automation flags.
- First run with no arguments opens the launcher role/settings screen.
- Controller mode stores host, port, and optional API token, then can start the controller and open the dashboard.
- Worker mode stores controller URL, optional LAN discovery, API token, worker ID/display name, Unreal search root, project path, and shared output root.
- Launcher settings are stored in `%LOCALAPPDATA%\RenderFarm\app-role.json`.
- Controller startup from the launcher waits for `/health` before reporting the dashboard ready.
- Worker service install is available through the elevated `install_worker_service.ps1` script after the controller URL is known.
- `scripts/publish_apps.ps1`: publishes controller, worker, and launcher into `publish/RenderFarm`.
- `scripts/build_installer.ps1`: publishes framework-dependent `win-x64` output and compiles the Inno Setup script with `ISCC.exe`.
- `installer/RenderFarm.iss`: setup EXE definition, Start Menu shortcut, optional Desktop shortcut, runtime check, and bundled runtime install step.
- `installer/redist/README.md`: explains where to place the .NET Desktop Runtime redistributable.
- `scripts/install_renderfarm.ps1`: basic per-user install from a package folder.
- `scripts/uninstall_renderfarm.ps1`: removes the installed product folder and optionally removes shortcuts/settings.
- `scripts/install_worker_service.ps1`: admin-only worker Windows Service installer using the published worker executable.
- `scripts/uninstall_worker_service.ps1`: admin-only worker service removal.
- `scripts/configure_controller_firewall.ps1`: opt-in controller firewall helper for TCP port `9200`.
- `scripts/sign_release.ps1`: code-signing hook for real certificates.
- `scripts/verify_upgrade_settings.ps1`: verifies `%LOCALAPPDATA%\RenderFarm` settings survive replacement.

## Product flow coverage

1. The setup EXE or package folder deploys RenderFarm binaries and creates launcher entrypoints.
2. First run opens the native launcher UI.
3. User chooses Controller or Worker.
4. Controller mode stores dashboard host/port/token settings and starts the dashboard.
5. The launcher waits for controller `/health` before reporting that the dashboard is ready.
6. Worker mode asks for controller URL or discovery, token, worker name, Unreal root, project path, and shared output root.
7. Choice is stored internally in per-user settings under `%LOCALAPPDATA%`.
8. Worker can be installed as a Windows Service with the elevated script after the controller URL is known.

## Build a lightweight package

```powershell
.\scripts\publish_apps.ps1 -Configuration Release -Runtime win-x64 -Mode framework-dependent -Zip
```

Expected outputs:

- `publish\RenderFarm\RenderFarm.Launcher.exe`
- `publish\RenderFarm\controller\RenderFarm.Controller.Api.exe`
- `publish\RenderFarm\worker\RenderFarm.Worker.Agent.exe`
- `publish\RenderFarm-win-x64-Release.zip`

The script prints the final package and archive sizes. Symbols are excluded by default to keep files small. Add `-IncludeSymbols` only for diagnostic builds. Add `-IncludeDocs` only when the package folder itself needs full documentation.

## Build the setup EXE

Install Inno Setup 6 on the build machine, then place the .NET Desktop Runtime redistributable here:

```text
installer\redist\windowsdesktop-runtime-8.x.x-win-x64.exe
```

Build the installer:

```powershell
.\scripts\build_installer.ps1 -Configuration Release -Runtime win-x64 -ProductVersion 0.1.0
```

Expected output:

```text
dist\RenderFarmSetup-0.1.0-win-x64.exe
```

The build script reports the exact published package size and setup EXE size. With the runtime bundled, the setup EXE size is mostly the product package plus the Microsoft runtime redistributable, compressed by Inno Setup.

The installed launcher, Start Menu shortcut, desktop shortcut, and uninstall entry use the RenderFarm product icon from packaging\\assets\\renderfarm.ico. The setup-window icon can also use that icon by passing -UseSetupIcon; the default build keeps the setup-window icon conservative because some local security tools reject custom setup resource updates.

## Controller database location

The controller stores its SQLite database under `%LOCALAPPDATA%\RenderFarm\Controller\renderfarm.db` by default. This keeps installed packages under Program Files read-only while allowing the controller to initialize automatically.

## Runtime requirement

RenderFarm includes a WPF launcher, so target machines need the Microsoft .NET 8 Desktop Runtime. The setup EXE checks for `Microsoft.WindowsDesktop.App` version 8 or newer. If it is missing and the redistributable is bundled, setup launches it silently with:

```text
/install /quiet /norestart
```

## Worker service flow

After installing the product on a worker PC, use the launcher in Worker mode to confirm the controller URL and worker settings. The service installer is intentionally kept as an elevated script for now.

The elevated path is:

```powershell
& "$env:LOCALAPPDATA\RenderFarm\Product\installer\install_worker_service.ps1" `
  -ControllerUrl http://CONTROLLER_IP:9200 `
  -WorkerId worker-pc-01 `
  -DisplayName "Render Worker 01" `
  -UnrealSearchRoot "C:\Program Files\Epic Games" `
  -ProjectPath "D:\Projects\Example\Example.uproject" `
  -SharedOutputRoot "\\SERVER\RenderFarmOutput" `
  -Start
```

Remove the service:

```powershell
& "$env:LOCALAPPDATA\RenderFarm\Product\installer\uninstall_worker_service.ps1" -RemoveRunner
```

## Clean-machine smoke checklist

1. Build `dist\RenderFarmSetup-0.1.0-win-x64.exe`.
2. Install it on a clean Windows 10/11 machine.
3. Confirm Start Menu contains **RenderFarm Launcher**.
4. Start RenderFarm Launcher.
5. Choose **Controller**, click **Start selected role**, and wait for `Controller dashboard ready!`.
6. Click **Open dashboard** and confirm `/health` returns OK.
7. If using LAN workers, run the firewall helper from elevated PowerShell with `-Accept`.
8. On a worker machine, choose **Worker**, enter the controller URL, and click **Start selected role**.
9. Optionally install the worker as a service using the elevated script.
10. Approve the worker in the dashboard and confirm heartbeat/capabilities appear.
11. Uninstall from Windows Apps and confirm `%LOCALAPPDATA%\RenderFarm\app-role.json` remains unless intentionally removed.

## Manual verification checklist

Developer/script mode:

1. Run `dotnet restore .\RenderFarm.sln`.
2. Run `dotnet build .\RenderFarm.sln -c Debug`.
3. Run `.\scripts\start-controller.ps1 -HostName 127.0.0.1 -Port 9200` from the repo root.
4. Open `http://127.0.0.1:9200/` and confirm the dashboard loads.
5. In a second terminal, run `.\scripts\start-worker.ps1 -ControllerUrl http://127.0.0.1:9200 -WorkerId local-worker-01`.
6. Approve the worker in the dashboard.
7. Open a job details drawer, click Close, then reopen the same job details drawer.
8. Confirm closing details does not cancel or mutate a running job.

Package mode:

1. Run `.\scripts\publish_apps.ps1 -Configuration Release -Runtime win-x64 -Mode framework-dependent`.
2. Run `.\publish\RenderFarm\RenderFarm.Launcher.exe --show-config`.
3. Run `.\publish\RenderFarm\RenderFarm.Launcher.exe --role controller --save --host 127.0.0.1 --port 9200`.
4. Run `.\publish\RenderFarm\RenderFarm.Launcher.exe` and confirm the launcher UI opens with saved controller settings and the two action buttons.
5. Install to local app data with `.\publish\RenderFarm\installer\install_renderfarm.ps1 -Role controller -CreateShortcuts`.

Installer mode:

1. Place `installer\redist\windowsdesktop-runtime-8.x.x-win-x64.exe`.
2. Run `.\scripts\build_installer.ps1 -Configuration Release -Runtime win-x64 -ProductVersion 0.1.0`.
3. Install `dist\RenderFarmSetup-0.1.0-win-x64.exe` on a clean Windows machine.
4. Open RenderFarm Launcher from Start Menu.
5. Choose Controller or Worker, click **Start selected role**, and confirm the launcher reports readiness or a clear failure reason.

## TODOs

- Add signed release automation once a real certificate is available.
- Add richer launcher controls for multiple project paths, shared output roots, Unreal roots, and API token rotation.
- Add optional installer page text for controller firewall setup once the product wording is final.
- Add screenshot-based clean-machine release evidence.




