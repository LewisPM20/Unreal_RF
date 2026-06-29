# Codex Handoff

C#/.NET is the active RenderFarm runtime. Python/FastAPI runtime files have been moved to `legacy_python/` for reversible reference only.

## Active Flow

```text
RenderFarm.Worker.Agent heartbeat
  -> RenderFarm.Controller.Api
  -> SQLite via RenderFarm.Persistence
  -> worker request-job endpoint
  -> controller lease/attempt/event records
  -> worker launches Unreal through controlled C# process launcher
```

## Important Rules

- Do not reintroduce Python as controller, worker, scheduler, persistence, dashboard backend, or LAN orchestration runtime.
- Optional Unreal Python bridge scripts, if needed, must live under `bridge/unreal_python/` and be invoked by C# using JSON input/output files.
- Retired Python files under `legacy_python/` are not active scripts.
- Use `dotnet build .\RenderFarm.sln -c Debug` and ask the user to run `dotnet test .\RenderFarm.sln -c Debug` when test execution is requested externally.

## Key Endpoints

```text
GET  /health
GET  /api/workers
POST /api/workers/heartbeat
POST /api/workers/{workerId}/request-job
POST /api/jobs
POST /api/jobs/{jobId}/renew-lease
POST /api/jobs/{jobId}/start
POST /api/jobs/{jobId}/complete
POST /api/jobs/{jobId}/fail
POST /api/jobs/expire-leases
```
