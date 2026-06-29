# Shared Network Output

The current preferred output model is direct render to a shared network root.

Example config:

```json
{
  "shared_output_root": "\\\\RenderNAS\\UnrealRenders",
  "output_policy": {
    "mode": "shared_folder_and_filename",
    "overwrite_existing": false
  }
}
```

Each job should render into:

```text
<shared_output_root>/<project_subfolder>/<job_name>/
```

The worker validates before launch:

- Root exists.
- Root is a directory.
- Job folder is inside the root.
- Job folder can be created.
- A test file can be written and deleted.
- Optional free-space threshold is satisfied.

For now, local-cache-then-copy is a later feature. Direct shared output keeps the farm easier to reason about.
