# Unreal Python Bridge

C# is the RenderFarm runtime. This folder is reserved for optional Unreal Editor Python bridge scripts only.

Current status: `scan_project_assets.py` is available as an optional Unreal asset-registry bridge. The C# controller also has a safe filesystem scanner, and the C# worker/controller remain responsible for runtime orchestration. The legacy Python worker contained reference code for MRQ/MRG queue preparation, but that runtime has been moved under `legacy_python/` and must not be launched as the controller, worker, scheduler, or persistence layer.

Future bridge scripts must:

- Be invoked by C# as a direct child process with an argument list, not through a shell command string.
- Accept a per-attempt JSON input file and write a per-attempt JSON result file.
- Avoid controller, worker, scheduler, lease, retry, or SQLite responsibilities.
- Return Unreal-specific discovery or queue-preparation data only.
