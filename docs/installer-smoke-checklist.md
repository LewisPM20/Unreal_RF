# RenderFarm Setup EXE Smoke And Upgrade Checklist

Use this checklist on a clean Windows 10/11 machine or disposable VM.

## Prerequisites

- Windows 10/11.
- Administrator rights for setup install, firewall rule creation, and worker service install.
- Inno-built setup EXE from `dist\RenderFarmSetup-<version>-win-x64.exe`.
- .NET 8 Desktop Runtime bundled under `installer\redist` or already installed on the target machine.
- Unreal Engine and project access only if testing real worker renders.

## Clean-machine setup smoke

1. Copy `RenderFarmSetup-<version>-win-x64.exe` to the clean machine.
2. Install with the wizard.
3. Confirm Start Menu contains **RenderFarm Launcher**.
4. If selected, confirm the Desktop shortcut starts the launcher.
5. Choose **Controller**, save settings, start controller, and open `http://127.0.0.1:9200/`.
6. Confirm the dashboard loads and `/health` returns OK.
7. If using LAN workers, run the firewall helper from elevated PowerShell:

   ```powershell
   & "$env:ProgramFiles\RenderFarm\installer\configure_controller_firewall.ps1" -Port 9200 -Accept
   ```

8. On the same machine or a second machine, choose **Worker**, enter the controller URL, save settings, and check controller.
9. Start the worker or install it as a service.
10. Approve the worker in the dashboard and confirm heartbeat/capabilities appear.
11. Uninstall RenderFarm from Windows Apps.
12. Confirm the install folder is removed and user settings under `%LOCALAPPDATA%\RenderFarm` are preserved unless intentionally removed by a separate cleanup step.

## Worker service smoke

Run from an elevated PowerShell prompt after installing the product:

```powershell
& "$env:ProgramFiles\RenderFarm\installer\install_worker_service.ps1" `
  -ControllerUrl http://CONTROLLER_IP:9200 `
  -WorkerId worker-pc-01 `
  -DisplayName "Render Worker 01" `
  -UnrealSearchRoot "C:\Program Files\Epic Games" `
  -ProjectPath "D:\Projects\Example\Example.uproject" `
  -SharedOutputRoot "\\SERVER\RenderFarmOutput" `
  -Start
```

Expected result: the worker service is installed only after `ControllerUrl` is supplied and the service starts with the configured worker identity.

## Upgrade settings survival

1. Install version A.
2. Open launcher and save Controller or Worker settings.
3. Capture the settings hash:

   ```powershell
   & "$env:ProgramFiles\RenderFarm\installer\verify_upgrade_settings.ps1" -Mode capture
   ```

4. Install version B over version A.
5. Verify settings survived:

   ```powershell
   & "$env:ProgramFiles\RenderFarm\installer\verify_upgrade_settings.ps1" -Mode verify
   ```

Expected result: `%LOCALAPPDATA%\RenderFarm\app-role.json` remains unchanged across the upgrade.