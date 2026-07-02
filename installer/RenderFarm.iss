#define MyAppName "RenderFarm"
#ifndef MyAppVersion
#define MyAppVersion "0.1.0"
#endif
#ifndef SourceDir
#define SourceDir "..\publish\RenderFarm"
#endif
#ifndef OutputDir
#define OutputDir "..\dist"
#endif

[Setup]
AppId={{9D3C8C7B-9330-40E8-A157-1E980F7805D1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=RenderFarm
AppPublisherURL=https://renderfarm.local/
AppSupportURL=https://renderfarm.local/support
AppUpdatesURL=https://renderfarm.local/releases
DefaultDirName={autopf}\RenderFarm
DefaultGroupName=RenderFarm
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=RenderFarmSetup-{#MyAppVersion}-win-x64
#ifndef SkipSetupIcon
SetupIconFile=..\packaging\assets\renderfarm.ico
#endif
UninstallDisplayIcon={app}\RenderFarm.Launcher.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
CloseApplications=yes
RestartIfNeededByRun=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef RuntimeInstallerName
Source: "redist\{#RuntimeInstallerName}"; DestDir: "{tmp}"; DestName: "dotnet-desktop-runtime.exe"; Flags: deleteafterinstall; Check: NeedsDotNetDesktopRuntime
#endif

[Icons]
Name: "{group}\RenderFarm Launcher"; Filename: "{app}\RenderFarm.Launcher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\RenderFarm.Launcher.exe"
Name: "{group}\Uninstall RenderFarm"; Filename: "{uninstallexe}"
Name: "{autodesktop}\RenderFarm Launcher"; Filename: "{app}\RenderFarm.Launcher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\RenderFarm.Launcher.exe"; Tasks: desktopicon

[Run]
#ifdef RuntimeInstallerName
Filename: "{tmp}\dotnet-desktop-runtime.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft .NET 8 Desktop Runtime..."; Flags: waituntilterminated; Check: NeedsDotNetDesktopRuntime
#endif
Filename: "{app}\RenderFarm.Launcher.exe"; Description: "Launch RenderFarm"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\uninstall_worker_service.ps1"" -InstallRoot ""{app}"" -RemoveRunner"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveRenderFarmWorkerService"; Check: WorkerServiceUninstallScriptExists
[Code]
function WorkerServiceUninstallScriptExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\installer\uninstall_worker_service.ps1'));
end;

function RuntimeMajorAtLeast(Version: String; RequiredMajor: Integer): Boolean;
var
  DotPos: Integer;
  MajorText: String;
  Major: Integer;
begin
  DotPos := Pos('.', Version);
  if DotPos > 0 then
    MajorText := Copy(Version, 1, DotPos - 1)
  else
    MajorText := Version;

  Major := StrToIntDef(MajorText, 0);
  Result := Major >= RequiredMajor;
end;

function HasDotNetDesktopRuntime: Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', 'Version', Version) and
    RuntimeMajorAtLeast(Version, 8);

  if not Result then
  begin
    Result :=
      RegQueryStringValue(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', 'Version', Version) and
      RuntimeMajorAtLeast(Version, 8);
  end;
end;

function NeedsDotNetDesktopRuntime: Boolean;
begin
  Result := not HasDotNetDesktopRuntime();
end;

#ifndef RuntimeInstallerName
function InitializeSetup(): Boolean;
begin
  if NeedsDotNetDesktopRuntime() then
  begin
    MsgBox('The Microsoft .NET 8 Desktop Runtime is not installed and this setup was built without a bundled runtime installer. Install the runtime from Microsoft before launching RenderFarm.', mbInformation, MB_OK);
  end;

  Result := True;
end;
#endif
