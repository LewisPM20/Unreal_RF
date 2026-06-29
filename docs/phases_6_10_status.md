# Phases 6-10 Status

_Last updated: 2026-06-26._

C#/.NET remains the active RenderFarm runtime. Python is not used for controller, worker, scheduler, persistence, dashboard backend, or LAN orchestration.

## Phase 6: worker readiness

Controller readiness is based on the latest C# worker heartbeat. It checks:

- worker status is schedulable (`Online` or `Idle`)
- worker has the project path or worker-specific mapping
- worker has a compatible Unreal installation
- worker can write the requested/default output root when one is known
- reported free disk satisfies `minFreeGb` profile setting when present

Useful endpoints:

```powershell
Invoke-RestMethod http://127.0.0.1:9200/api/projects/<projectId>/readiness
Invoke-RestMethod "http://127.0.0.1:9200/api/workers/<workerId>/readiness?projectId=<projectId>&renderProfileId=<profileId>"
Invoke-RestMethod "http://127.0.0.1:9200/api/workers/<workerId>/validate-project-path?path=D:\Projects\Demo\Demo.uproject"
Invoke-RestMethod "http://127.0.0.1:9200/api/workers/<workerId>/validate-engine?version=5.7"
Invoke-RestMethod "http://127.0.0.1:9200/api/workers/<workerId>/validate-output-root?path=\\SERVER\RenderFarmOutput"
```

## Phase 7: project/profile management

Implemented now:

- multiple render profiles per project
- worker-specific project path mappings
- duplicate profile endpoint
- import/export JSON for projects and profiles
- guarded deletes with optional `?force=true`
- validation/readiness endpoints

Profile metadata that does not yet have first-class columns should be stored in `RenderProfile.Settings`, for example:

```json
{
  "map": "Minimal_Default1",
  "sequence": "/Game/Cinematics/Seq_Main",
  "defaultOutputRoot": "\\\\SERVER\\RenderFarmOutput",
  "defaultOutputSubfolder": "{jobId}",
  "outputNamingPattern": "{sequence}_{frame}",
  "minFreeGb": "50",
  "extraArgs": "-SomeUnrealFlag"
}
```

## Phase 8: Unreal project scanner bridge

The controller can scan project filesystem metadata through:

```powershell
Invoke-RestMethod http://127.0.0.1:9200/api/projects/<projectId>/scan -Method Post -ContentType application/json -Body '{}'
```

The optional bridge script lives at:

```text
bridge/unreal_python/scan_project_assets.py
```

Boundary:

- C# can directly inspect `.uproject` JSON and filenames under `Content/`.
- Unreal Python may be used only for Unreal-specific Asset Registry accuracy.
- Bridge scripts must output JSON only and must not mutate the project.
- C# should invoke Unreal directly with an argument list, never arbitrary shell text.

## Phase 9: deterministic render preparation

Before launching Unreal, the worker writes a per-attempt structured render request JSON beside the attempt logs. It records:

- project path
- map/sequence/MRQ config
- output directory
- file format
- optional frame/chunk metadata from profile settings
- job ID and attempt ID

If preparation fails, the worker reports `CommandValidationFailed` and does not fake a render launch.

## Phase 10: chunking

Chunking execution remains disabled. `RenderChunkPlanner` can split inclusive frame ranges deterministically, but the scheduler still rejects chunked render requests until MRQ/MRG frame-range rendering and output naming are verified with a real Unreal project.

Example future split:

- frames `1-1000`
- chunk size `200`
- ranges: `1-200`, `201-400`, `401-600`, `601-800`, `801-1000`

This is deliberate: the farm will not pretend distributed chunk rendering works until Unreal output behavior is proven.