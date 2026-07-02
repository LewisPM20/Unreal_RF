# RenderFarm Setup EXE Smoke And Upgrade Checklist

Use this checklist on a clean Windows 10/11 machine or disposable VM.

## Prerequisites

- Windows 10/11.
- Administrator rights if you opt into the controller firewall helper or install the worker service through the script.
- Inno-built setup EXE from `dist\RenderFarmSetup-<version>-win-x64.exe`.
- .NET 8 Desktop Runtime bundled under `installer\redist` or already installed on the target machine.
- Unreal Engine and project/shared-output access only if testing real renders.

## Clean-machine setup smoke

1. Copy `RenderFarmSetup-<version>-win-x64.exe` to the clean machine.
2. Install with the wizard.
3. Confirm Start Menu contains **RenderFarm Launcher**.
4. If selected, confirm the Desktop shortcut starts the launcher.
5. Choose **Controller**, start the selected role, and open `http://127.0.0.1:9200/`.
6. Confirm the dashboard loads and `/health` returns OK.
7. In Dashboard > Settings, configure Controller Render Defaults: Unreal executable/search root, shared output root, and output pattern.
8. If using LAN workers, run the firewall helper from elevated PowerShell:

   ```powershell
   & "$env:LOCALAPPDATA\RenderFarm\Product\installer\configure_controller_firewall.ps1" -Port 9200 -Accept
   ```

9. On the same machine or a second machine, choose **Worker**, enter only the controller URL plus optional worker ID/display name/token, and start the selected role.
10. Approve the worker in the dashboard and confirm heartbeat/capabilities appear.
11. Queue a render from a saved project/profile and confirm the worker receives an assignment payload with execution details.
12. Uninstall RenderFarm from Windows Apps.
13. Confirm the install folder is removed and user settings under `%LOCALAPPDATA%\RenderFarm` are preserved unless intentionally removed by a separate cleanup step.

## Optional worker service smoke

The launcher can request worker-service installation for non-CLI operators. The script below is the equivalent elevated path for technical verification when you want the worker to start with Windows.

```powershell
& "$env:LOCALAPPDATA\RenderFarm\Product\installer\install_worker_service.ps1" `
  -ControllerUrl http://CONTROLLER_IP:9200 `
  -WorkerId worker-pc-01 `
  -DisplayName "Render Worker 01" `
  -Start
```

Expected result: the worker service starts with only connection identity settings. Unreal/project/output/render configuration is supplied later by controller-assigned job payloads.

## Uninstall behaviour

The Inno Setup uninstaller removes installed application files, Start Menu entries, the optional Desktop shortcut, and the standard Windows uninstall entry. If the optional worker service was installed using the packaged service script, the setup uninstaller also runs the worker-service cleanup script before removing files.

Render outputs are never deleted by uninstall. Local state under `%LOCALAPPDATA%\RenderFarm`, including launcher role settings, controller database files, logs, and upgrade-verification hashes, is preserved by default so upgrades and accidental reinstalls are recoverable.

For a full manual cleanup after uninstall, run an elevated PowerShell session only if you also need to remove the worker service:

```powershell
& "$env:LOCALAPPDATA\RenderFarm\Product\installer\uninstall_renderfarm.ps1" -RemoveShortcuts -RemoveWorkerService
```

To remove local settings as well, add `-RemoveSettings`. Do this only after confirming you no longer need controller history or launcher role settings.
## Upgrade settings survival

1. Install version A.
2. Open launcher and start Controller or Worker once so settings are written.
3. Capture the settings hash:

   ```powershell
   & "$env:LOCALAPPDATA\RenderFarm\Product\installer\verify_upgrade_settings.ps1" -Mode capture
   ```

4. Install version B over version A.
5. Verify settings survived:

   ```powershell
   & "$env:LOCALAPPDATA\RenderFarm\Product\installer\verify_upgrade_settings.ps1" -Mode verify
   ```

Expected result: `%LOCALAPPDATA%\RenderFarm\app-role.json` and controller data under `%LOCALAPPDATA%\RenderFarm\Controller` remain in place across the upgrade.

