from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import TOOLS


def main() -> int:
    tools = {item["name"]: item for item in TOOLS}
    required = {"live_manage_report_composition", "live_configure_presentation_transition"}
    if not required.issubset(tools):
        raise RuntimeError(f"Advanced document tools are missing: {required - set(tools)}")
    operations = set(
        tools["live_manage_report_composition"]["inputSchema"]["properties"]["operation"]["enum"]
    )
    expected = {
        "inspect", "add_layout_page", "replace_layout_page", "add_subreport",
        "set_expressions", "remove_supplemental_page", "remove_related_report",
    }
    if operations != expected:
        raise RuntimeError(f"Report composition operation surface is incomplete: {expected - operations}")
    source = (
        ROOT / "addin" / "ArcGISCodexBridgeAddin" / "AdvancedDocumentActions.cs"
    ).read_text(encoding="utf-8")
    required_source = {
        "report.AddLayoutPage", "report.ReplaceLayoutPage", "report.AddSubReport",
        "report.SetExpressions", "report.RemoveSupplementalPage", "report.RemoveRelatedReport",
        "confirm_remove=true is required", "page.SetTransition(new CIMPresentationTransition",
        "PresentationTransitionType", "SwipeDirection", "SpeakerNotes",
        "source_data_modified = false", "project_saved = false", ".Take(1000)", ".Take(500)",
    }
    missing = sorted(value for value in required_source if value not in source)
    if missing:
        raise RuntimeError(f"Advanced document contract is incomplete: {missing}")
    if "SaveAsync" in source or "SaveEdits" in source or "Geodatabase" in source:
        raise RuntimeError("Advanced document authoring unexpectedly references persistent data mutation")
    print("advanced document contract ok: supplemental pages, subreports, expressions, transitions, and notes are bounded and unsaved")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
