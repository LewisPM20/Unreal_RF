# Runtime Redistributable

Place the Microsoft .NET 8 Desktop Runtime installer for Windows x64 in this folder before building the setup EXE.

Expected file pattern:

```text
installer/redist/windowsdesktop-runtime-8.x.x-win-x64.exe
```

RenderFarm includes a WPF launcher, so the Windows Desktop Runtime is required rather than only the base console runtime. The build script picks the newest file matching that pattern and passes it to Inno Setup. During install, the setup checks for `Microsoft.WindowsDesktop.App` version 8 or newer. If the runtime is missing, the bundled installer is launched silently with:

```text
/install /quiet /norestart
```

The redistributable EXE is intentionally ignored by source control because it is large and should be refreshed from Microsoft's official .NET 8 download page for each release.