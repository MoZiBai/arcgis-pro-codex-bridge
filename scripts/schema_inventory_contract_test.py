from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "addin" / "ArcGISCodexBridgeAddin" / "QueryActions.cs"


def main() -> int:
    text = SOURCE.read_text(encoding="utf-8")
    required = {
        "has_joins = layer.HasJoins",
        "has_relates = layer.HasRelates",
        "select_related_data = layer.IsSelectRelatedData",
        "definition.GetSubtypes()",
        "definition.GetIndexes()",
        "definition.GetAttributeRules(AttributeRuleType.All)",
        "definition.GetContingencies()",
        "table.GetControllerDatasets()",
        "validation_status_field",
        "editor_tracking_fields",
        "archive_enabled",
        "attachments_enabled",
        "source_data_modified = false",
    }
    missing = sorted(value for value in required if value not in text)
    if missing:
        raise RuntimeError(f"Advanced schema inventory is incomplete: {missing}")
    if "Modify(" in text or "Delete(" in text or "CreateRow(" in text:
        raise RuntimeError("Read-only schema inventory contains a data mutation primitive")
    print("schema inventory contract ok: advanced geodatabase metadata is read-only and source-safe")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
