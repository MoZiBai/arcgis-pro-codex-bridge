from __future__ import annotations

import json
from pathlib import Path

import arcpy


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "outputs" / "bridge_runtime_tests" / "table_inputs_v018"
GDB = OUTPUT / "table_inputs_v018.gdb"
TABLE = GDB / "validation_table"


def main() -> int:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    if not arcpy.Exists(str(GDB)):
        arcpy.management.CreateFileGDB(str(OUTPUT), GDB.name)
    if not arcpy.Exists(str(TABLE)):
        arcpy.management.CreateTable(str(GDB), TABLE.name)
        arcpy.management.AddFields(
            str(TABLE),
            [
                ["NAME", "TEXT", "Name", 80],
                ["VALUE", "DOUBLE", "Value"],
            ],
        )
        with arcpy.da.InsertCursor(str(TABLE), ["NAME", "VALUE"]) as cursor:
            cursor.insertRow(["alpha", 1.0])
            cursor.insertRow(["beta", 2.5])
            cursor.insertRow(["gamma", 4.0])
    manifest = {
        "geodatabase": str(GDB.resolve()),
        "table": str(TABLE.resolve()),
        "source_data_modified": False,
    }
    (OUTPUT / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(manifest, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
