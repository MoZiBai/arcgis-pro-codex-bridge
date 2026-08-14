from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ADDIN = ROOT / "addin" / "ArcGISCodexBridgeAddin"
INVENTORY = ROOT / "scripts" / "inventory-arcgis-sdk.ps1"
PWSH = Path(shutil.which("pwsh") or "pwsh")
ARCGIS_PRO = Path(
    os.environ.get("ARCGIS_PRO_INSTALL_DIR", r"C:\Program Files\ArcGIS\Pro")
)


def main() -> int:
    completed = subprocess.run(
        [
            str(PWSH),
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(INVENTORY),
            "-ArcGISProInstallDir",
            str(ARCGIS_PRO),
        ],
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=True,
        timeout=240,
        cwd=ROOT,
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(completed.stderr.strip() or completed.stdout.strip())
    inventory = json.loads(completed.stdout.strip().lstrip("\ufeff"))
    source = "\n".join(
        path.read_text(encoding="utf-8", errors="replace")
        for path in ADDIN.glob("*.cs")
    )
    assemblies: list[dict[str, object]] = []
    total_types = 0
    covered = 0
    for assembly in inventory.get("assemblies") or []:
        missing: list[str] = []
        present: list[str] = []
        for type_name in assembly.get("entry_point_types") or []:
            total_types += 1
            simple = str(type_name).rsplit(".", 1)[-1]
            if str(type_name) in source or simple in source:
                present.append(str(type_name))
                covered += 1
            else:
                missing.append(str(type_name))
        assemblies.append(
            {
                "assembly": assembly.get("assembly"),
                "version": assembly.get("version"),
                "public_type_count": assembly.get("public_type_count"),
                "sampled_entry_points": len(present) + len(missing),
                "referenced_count": len(present),
                "unreferenced_count": len(missing),
                "referenced_by_bridge": present[:20],
                "unreferenced_candidates": missing[:40],
            }
        )
    result = {
        "arcgis_pro_install": inventory.get("arcgis_pro_install"),
        "generated_at": inventory.get("generated_at"),
        "assembly_count": len(assemblies),
        "public_type_count": sum(int(item.get("public_type_count") or 0) for item in assemblies),
        "sampled_entry_point_count": total_types,
        "sampled_entry_points_referenced": covered,
        "sampled_entry_points_unreferenced": total_types - covered,
        "assemblies": assemblies,
        "interpretation": (
            "Name-based coverage is a triage signal only. Every unreferenced public entry point "
            "still requires API, threading, licensing, side-effect, and overlap review."
        ),
        "source_data_modified": False,
        "project_modified": False,
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
