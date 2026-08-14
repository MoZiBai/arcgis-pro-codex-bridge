from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import SERVER_INSTRUCTIONS


def main() -> int:
    compact = " ".join(SERVER_INSTRUCTIONS.split())
    first_window = compact[:512].casefold()

    required = (
        "arcgis pro",
        "gis datasets",
        "spatial analysis",
        "do not call any tool for unrelated",
        "arcgis_status",
        "preserve source data",
        "output root",
        "arcgis pro 3.6",
    )
    missing = [phrase for phrase in required if phrase not in compact.casefold()]
    if missing:
        raise RuntimeError(f"Server instructions are missing required boundaries: {missing}")

    early_required = (
        "arcgis pro",
        "do not call any tool for unrelated",
    )
    late = [phrase for phrase in early_required if phrase not in first_window]
    if late:
        raise RuntimeError(f"Critical routing guidance must appear within 512 characters: {late}")

    print(
        "server instructions contract ok: ArcGIS-only routing, unrelated-task "
        "exclusion, source preservation, and Pro 3.6 scope"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
