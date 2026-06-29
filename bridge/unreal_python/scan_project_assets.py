#!/usr/bin/env python3
"""Optional Unreal Python bridge for RenderFarm project asset scanning.

This script is intentionally bridge-only. The C# controller/worker remain the product
runtime; C# may invoke Unreal with this script only when Unreal asset-registry data is
needed beyond filesystem filename hints.

Expected invocation from Unreal:
    UnrealEditor-Cmd.exe MyProject.uproject -run=pythonscript -script=scan_project_assets.py -- <project.uproject>

It writes JSON only to stdout.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path


def _fallback_scan(project_path: Path) -> dict:
    root = project_path.parent
    content = root / "Content"
    maps: list[str] = []
    sequences: list[str] = []
    mrq: list[str] = []
    mrg: list[str] = []
    if content.exists():
        for path in content.rglob("*"):
            if path.suffix.lower() not in {".umap", ".uasset"}:
                continue
            game_path = "/Game/" + path.relative_to(content).with_suffix("").as_posix()
            name = path.stem.lower()
            if path.suffix.lower() == ".umap":
                maps.append(game_path)
            if "sequence" in name or name.startswith("seq_"):
                sequences.append(game_path)
            if "mrq" in name or "renderqueue" in name or "moviepipeline" in name:
                mrq.append(game_path)
            if "mrg" in name or "moviegraph" in name:
                mrg.append(game_path)
    engine_version = None
    plugins: list[str] = []
    try:
        data = json.loads(project_path.read_text(encoding="utf-8-sig"))
        engine_version = data.get("EngineAssociation")
        for plugin in data.get("Plugins", []):
            if plugin.get("Enabled") and any(token in plugin.get("Name", "") for token in ["Movie", "Pipeline", "Sequencer", "Python"]):
                plugins.append(plugin.get("Name"))
    except Exception:
        pass
    return {
        "projectPath": str(project_path),
        "engineVersion": engine_version,
        "maps": sorted(maps),
        "levelSequences": sorted(sequences),
        "movieRenderQueueConfigs": sorted(mrq),
        "movieRenderGraphs": sorted(mrg),
        "relevantPlugins": sorted(p for p in plugins if p),
        "usedUnrealBridge": False,
        "ok": True,
        "error": None,
    }


def main(argv: list[str]) -> int:
    args = [arg for arg in argv[1:] if arg != "--"]
    project_arg = next((arg for arg in args if arg.lower().endswith(".uproject")), None)
    if not project_arg:
        print(json.dumps({"ok": False, "error": "No .uproject path was supplied."}, separators=(",", ":")))
        return 2
    project_path = Path(os.path.expandvars(project_arg)).expanduser().resolve()
    if not project_path.exists():
        print(json.dumps({"ok": False, "projectPath": str(project_path), "error": "Project file was not found."}, separators=(",", ":")))
        return 2

    # If running inside Unreal, use AssetRegistry for type-accurate discovery.
    try:
        import unreal  # type: ignore

        registry = unreal.AssetRegistryHelpers.get_asset_registry()
        assets = registry.get_assets_by_path("/Game", recursive=True)
        maps: list[str] = []
        sequences: list[str] = []
        mrq: list[str] = []
        mrg: list[str] = []
        for asset in assets:
            class_name = str(asset.asset_class_path.asset_name or asset.asset_class)
            object_path = str(asset.package_name)
            if class_name in {"World"}:
                maps.append(object_path)
            elif "LevelSequence" in class_name:
                sequences.append(object_path)
            elif "MoviePipeline" in class_name or "MovieRenderQueue" in class_name:
                mrq.append(object_path)
            elif "MovieGraph" in class_name:
                mrg.append(object_path)
        result = _fallback_scan(project_path)
        result.update({
            "maps": sorted(set(result["maps"]) | set(maps)),
            "levelSequences": sorted(set(result["levelSequences"]) | set(sequences)),
            "movieRenderQueueConfigs": sorted(set(result["movieRenderQueueConfigs"]) | set(mrq)),
            "movieRenderGraphs": sorted(set(result["movieRenderGraphs"]) | set(mrg)),
            "usedUnrealBridge": True,
        })
    except Exception as exc:
        result = _fallback_scan(project_path)
        result["error"] = f"Unreal AssetRegistry unavailable; fallback scan used. {exc}"
    print(json.dumps(result, separators=(",", ":")))
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))