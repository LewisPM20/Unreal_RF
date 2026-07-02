# Non-CLI Operator Guide

The active runtime is C#/.NET. Operators do not need Python.

## Recommended Operator Flow

1. Install RenderFarm with the setup EXE or from the package folder.
2. Open **RenderFarm Launcher** from the Start Menu or desktop shortcut.
3. Choose **Controller** on the main machine, then click **Start selected role**.
4. Wait for **Controller dashboard ready!**, then click **Open dashboard** to manage workers, projects, render profiles, jobs, logs, and settings.
5. Choose **Worker** on render machines, enter the controller URL, worker name, Unreal search root, project path, and shared output root, then click **Start selected role**.
6. Use the elevated worker-service script only when the worker should run at startup.

## Creating a Package

Create a framework-dependent package for machines with the .NET 8 Desktop Runtime:

```powershell
.\scripts\publish_apps.ps1 -Configuration Release -Runtime win-x64 -Mode framework-dependent -Zip
```

Main package output:

- `publish/RenderFarm/RenderFarm.Launcher.exe`
- `publish/RenderFarm/controller/RenderFarm.Controller.Api.exe`
- `publish/RenderFarm/worker/RenderFarm.Worker.Agent.exe`
- `publish/RenderFarm/installer/*.ps1`

## Setup EXE

Install Inno Setup 6 on the build machine. Place the Microsoft .NET 8 Desktop Runtime redistributable here:

```text
installer/redist/windowsdesktop-runtime-8.x.x-win-x64.exe
```

Build the setup EXE:

```powershell
.\scripts\build_installer.ps1 -Configuration Release -Runtime win-x64 -ProductVersion 0.1.0
```

Install `dist\RenderFarmSetup-0.1.0-win-x64.exe`, then open **RenderFarm Launcher** from the Start Menu.

## Basic Folder Install

From inside the published package folder:

```powershell
.\installer\install_renderfarm.ps1 -Role controller -CreateShortcuts
```

Worker example:

```powershell
.\installer\install_renderfarm.ps1 `
  -Role worker `
  -ControllerUrl http://CONTROLLER_IP:9200 `
  -WorkerId worker-pc-01 `
  -DisplayName "Render Worker 01" `
  -CreateShortcuts
```

## Controller PC Requirements

- Windows 10/11.
- .NET 8 Desktop Runtime, unless using the setup EXE with bundled runtime.
- The controller SQLite database is stored under `%LOCALAPPDATA%\RenderFarm\Controller\renderfarm.db` by default.
- Firewall inbound rule for controller port, normally `9200`, when workers are on other PCs.
- Optional API token if the farm should reject unauthenticated write actions.

## Worker PC Requirements

- Windows 10/11.
- .NET 8 Desktop Runtime, unless using the setup EXE with bundled runtime.
- Unreal Engine installed for the project version.
- Access to the controller-supplied `.uproject` path.
- Read/write access to the controller-supplied shared output root.
- Firewall/network access to the controller URL.

## Worker Service

The visible launcher keeps service setup hidden for now. Worker service setup is still available from an elevated PowerShell session:

```powershell
& "$env:LOCALAPPDATA\RenderFarm\Product\installer\install_worker_service.ps1" `
  -ControllerUrl http://CONTROLLER_IP:9200 `
  -WorkerId worker-pc-01 `
  -DisplayName "Render Worker 01" `
  -Start
```

Remove it with:

```powershell
& "$env:LOCALAPPDATA\RenderFarm\Product\installer\uninstall_worker_service.ps1" -RemoveRunner
```
