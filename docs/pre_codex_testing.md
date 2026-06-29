# C# Runtime Test Checklist

Python runtime checks are retired. C#/.NET is the active runtime.

## 1. Restore

```powershell
dotnet restore .\RenderFarm.sln
```

## 2. Build

```powershell
dotnet build .\RenderFarm.sln -c Debug
```

## 3. Test

```powershell
dotnet test .\RenderFarm.sln -c Debug
```

## 4. Local controller smoke test

```powershell
.\scripts\start_controller.ps1 -HostName 127.0.0.1 -Port 9200
```

In another terminal:

```powershell
Invoke-RestMethod http://127.0.0.1:9200/health
```

## 5. Local worker smoke test

```powershell
.\scripts\start_worker.ps1 -ControllerUrl http://127.0.0.1:9200 -WorkerId local-worker-01 -ProjectPaths "C:\Path\To\Project.uproject" -SharedOutputRoots "C:\RenderFarmOutput" -UnrealSearchRoots "C:\Program Files\Epic Games"
```

Then verify the worker appears:

```powershell
Invoke-RestMethod http://127.0.0.1:9200/api/workers
```
