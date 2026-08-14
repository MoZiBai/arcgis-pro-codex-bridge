from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from scripts import (
    live_v013_validation,
    live_v014_validation,
    live_v015_validation,
    live_v016_validation,
    live_v017_validation,
    live_v018_validation,
    live_v019_validation,
    live_v020_validation,
    live_v021_validation,
    live_v022_validation,
    live_v023_validation,
    live_v024_validation,
    live_v025_validation,
    live_v026_validation,
    live_v027_validation,
)
from src.arcgis_mcp.server import ArcGISMCP


def fingerprint_tree(root: Path) -> dict[str, dict[str, int | str]]:
    result: dict[str, dict[str, int | str]] = {}
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        stat = path.stat()
        result[path.relative_to(root).as_posix()] = {
            "size": stat.st_size,
            "mtime_ns": stat.st_mtime_ns,
            "sha256": digest.hexdigest(),
        }
    return result


def load_json(path: Path) -> dict[str, object]:
    return json.loads(path.read_text(encoding="utf-8"))


def require_clean_summary(name: str, summary: dict[str, object]) -> None:
    if summary.get("source_data_modified") is not False:
        raise RuntimeError(f"{name} did not affirm source_data_modified=false")
    if summary.get("project_saved") is not False:
        raise RuntimeError(f"{name} did not affirm project_saved=false")
    cleanup = summary.get("cleanup")
    if not isinstance(cleanup, (dict, list)):
        raise RuntimeError(f"{name} has no cleanup result")
    if isinstance(cleanup, list):
        failures = [item for item in cleanup if isinstance(item, dict) and item.get("error")]
        if failures:
            raise RuntimeError(f"{name} cleanup reported an error: {failures}")
        return
    if any(str(key).endswith("_error") for key in cleanup):
        raise RuntimeError(f"{name} cleanup reported an error: {cleanup}")
    map_results = cleanup.get("maps")
    if isinstance(map_results, list):
        failures = [
            item
            for item in map_results
            if isinstance(item, dict)
            and (item.get("error") or item.get("removed_from_project") is False)
        ]
        if failures:
            raise RuntimeError(f"{name} map cleanup failed: {failures}")


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    status_before = server._run_live("status", {})
    if status_before.get("protocol_version") != "0.28.1":
        raise RuntimeError(
            "ArcGIS Pro has not loaded bridge 0.28.1. Restart ArcGIS Pro before "
            f"running the suite; current={status_before.get('protocol_version')}"
        )
    project_path = Path(str(status_before["project"]))
    project_stat_before = project_path.stat()
    synthetic_roots = {
        "vector": ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015",
        "raster": ROOT / "outputs" / "bridge_runtime_tests" / "raster_inputs_v016",
    }
    source_before = {
        name: fingerprint_tree(path) for name, path in synthetic_roots.items()
    }

    live_v013_validation.main()
    v013_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v013_validation_latest.json"
    )
    v013 = load_json(v013_path)
    require_clean_summary("v0.13", v013)

    live_v014_validation.main()
    v014_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v014_validation_latest.json"
    )
    v014 = load_json(v014_path)
    require_clean_summary("v0.14", v014)

    live_v015_validation.main()
    v015_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v015_validation_latest.json"
    )
    v015 = load_json(v015_path)
    require_clean_summary("v0.15", v015)

    live_v016_validation.main()
    v016_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v016_validation_latest.json"
    )
    v016 = load_json(v016_path)
    require_clean_summary("v0.16", v016)

    live_v017_validation.main()
    v017_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v017_validation_latest.json"
    )
    v017 = load_json(v017_path)
    require_clean_summary("v0.17", v017)

    live_v018_validation.main()
    v018_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v018_validation_latest.json"
    )
    v018 = load_json(v018_path)
    require_clean_summary("v0.18", v018)

    live_v019_validation.main()
    v019_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v019_validation_latest.json"
    )
    v019 = load_json(v019_path)
    require_clean_summary("v0.19", v019)

    live_v020_validation.main()
    v020_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v020_validation_latest.json"
    )
    v020 = load_json(v020_path)
    require_clean_summary("v0.20", v020)

    live_v021_validation.main()
    v021_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v021_validation_latest.json"
    )
    v021 = load_json(v021_path)
    require_clean_summary("v0.21", v021)

    live_v022_validation.main()
    v022_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v022_validation_latest.json"
    )
    v022 = load_json(v022_path)
    require_clean_summary("v0.22", v022)

    live_v023_validation.main()
    v023_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v023_validation_latest.json"
    )
    v023 = load_json(v023_path)
    require_clean_summary("v0.23", v023)

    live_v024_validation.main()
    v024_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v024_validation_latest.json"
    )
    v024 = load_json(v024_path)
    require_clean_summary("v0.24", v024)

    live_v025_validation.main()
    v025_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v025_validation_latest.json"
    )
    v025 = load_json(v025_path)
    require_clean_summary("v0.25", v025)

    live_v026_validation.main()
    v026_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v026_validation_latest.json"
    )
    v026 = load_json(v026_path)
    require_clean_summary("v0.26", v026)

    live_v027_validation.main()
    v027_path = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_v027_validation_latest.json"
    )
    v027 = load_json(v027_path)
    require_clean_summary("v0.27", v027)

    status_after = server._run_live("status", {})
    inventory = server._run_live("project_inventory", {})
    project_stat_after = project_path.stat()
    source_after = {
        name: fingerprint_tree(path) for name, path in synthetic_roots.items()
    }

    project_unchanged = (
        project_stat_before.st_size == project_stat_after.st_size
        and project_stat_before.st_mtime_ns == project_stat_after.st_mtime_ns
    )
    source_unchanged = source_before == source_after
    inventory_text = json.dumps(inventory, ensure_ascii=False, default=str)
    temporary_objects_absent = not any(
        prefix in inventory_text
        for prefix in (
            "CodexV013", "CodexV014", "CodexV015", "CodexV016", "CodexV017", "CodexV018", "CodexV019", "CodexV020", "CodexV021", "CodexV022", "CodexV023", "CodexV024", "CodexV025", "CodexV026", "CodexV027",
            "Validation Extent"
        )
    )
    if not project_unchanged:
        raise RuntimeError("The open .aprx file changed during validation")
    if not source_unchanged:
        raise RuntimeError("Synthetic source data changed during validation")
    if not temporary_objects_absent:
        raise RuntimeError("Temporary validation project objects remain after cleanup")

    summary = {
        "protocol_version": status_after.get("protocol_version"),
        "action_count": len(status_after.get("capabilities") or []),
        "project": str(project_path),
        "project_file_unchanged": project_unchanged,
        "synthetic_source_fingerprint_unchanged": source_unchanged,
        "temporary_project_objects_absent": temporary_objects_absent,
        "source_data_modified": False,
        "project_saved": False,
        "v013": str(v013_path),
        "v014": str(v014_path),
        "v015": str(v015_path),
        "v016": str(v016_path),
        "v017": str(v017_path),
        "v018": str(v018_path),
        "v019": str(v019_path),
        "v020": str(v020_path),
        "v021": str(v021_path),
        "v022": str(v022_path),
        "v023": str(v023_path),
        "v024": str(v024_path),
        "v025": str(v025_path),
        "v026": str(v026_path),
        "v027": str(v027_path),
    }
    output = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "live_validation_suite_latest.json"
    )
    output.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
