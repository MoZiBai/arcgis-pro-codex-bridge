from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import TOOLS


def main() -> int:
    tools = {item["name"]: item for item in TOOLS}
    required_tools = {"live_manage_report_elements", "live_manage_presentation_elements"}
    if not required_tools.issubset(tools):
        raise RuntimeError(f"Document element factory tools are missing: {required_tools - set(tools)}")
    report_types = set(
        value for value in tools["live_manage_report_elements"]["inputSchema"]["properties"]
        ["element_type"]["enum"] if value is not None
    )
    required_report_types = {
        "paragraph", "field_value", "field_statistic", "dynamic_picture",
        "attachment_frame", "chart_frame", "map_frame",
    }
    if not required_report_types.issubset(report_types):
        raise RuntimeError(f"Report element factory surface is incomplete: {required_report_types - report_types}")
    presentation_types = set(
        value for value in tools["live_manage_presentation_elements"]["inputSchema"]
        ["properties"]["element_type"]["enum"] if value is not None
    )
    if presentation_types != {"text", "picture", "shape", "arrow"}:
        raise RuntimeError(f"Presentation element factory surface is incomplete: {presentation_types}")

    source = (
        ROOT / "addin" / "ArcGISCodexBridgeAddin" / "DocumentElementFactoryActions.cs"
    ).read_text(encoding="utf-8")
    required_source = {
        "CreateRectangleParagraphGraphicElement",
        "ReportElementFactory.Instance.CreateFieldValueTextElement",
        "ReportElementFactory.Instance.CreateFieldStatisticTextElement",
        "ReportElementFactory.Instance.CreateDynamicPictureGraphicElement",
        "ReportElementFactory.Instance.CreateAttachmentFrame",
        "ReportElementFactory.Instance.CreateChartFrame",
        "ReportElementFactory.Instance.CreateMapFrame",
        "PresentationElementFactory.Instance.CreateTextGraphicElement",
        "PresentationElementFactory.Instance.CreatePictureGraphicElement",
        "PresentationElementFactory.Instance.CreatePredefinedShapeGraphicElement",
        "PresentationElementFactory.Instance.CreateArrowGraphicElement",
        "PresentationElementFactory.Instance.CreateGroupElement",
        "confirm_remove=true is required",
        "source_data_modified = false",
        "project_saved = false",
        "double.IsFinite",
        "section.GetType() != typeof(ReportSection)",
        "WritableReportSections(report)",
        "Writable sections:",
        "CheckedReportBounds",
        "bounds must fit inside report section",
        "var sectionX = section.GetX()",
        "var sectionY = section.GetY()",
    }
    missing = sorted(fragment for fragment in required_source if fragment not in source)
    if missing:
        raise RuntimeError(f"Document element factory contract is incomplete: {missing}")
    if "SaveAsync" in source or "SaveEdits" in source or "Geodatabase" in source:
        raise RuntimeError("Document element factories unexpectedly reference persistent data mutation APIs")
    print("document element factory contract ok: native report and presentation authoring is bounded, unsaved, and dataset-safe")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
