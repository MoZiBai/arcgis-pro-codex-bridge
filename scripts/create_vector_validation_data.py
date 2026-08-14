from __future__ import annotations

import json
from pathlib import Path

import arcpy

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015"
GDB = OUTPUT / "vector_inputs_v015.gdb"


def main() -> int:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    arcpy.env.overwriteOutput = True
    spatial_reference = arcpy.SpatialReference(4490)

    if not GDB.exists():
        arcpy.management.CreateFileGDB(str(OUTPUT), GDB.name)

    districts = GDB / "validation_districts"
    residents = GDB / "validation_residents"
    for dataset in (districts, residents):
        if arcpy.Exists(str(dataset)):
            arcpy.management.Delete(str(dataset))

    arcpy.management.CreateFeatureclass(
        str(GDB), districts.name, "POLYGON", spatial_reference=spatial_reference
    )
    arcpy.management.AddField(str(districts), "NAME", "TEXT", field_length=40)
    arcpy.management.AddField(str(districts), "METRIC_A", "DOUBLE")
    arcpy.management.AddField(str(districts), "METRIC_B", "DOUBLE")

    polygon_rows = []
    index = 0
    for row in range(3):
        for column in range(4):
            xmin = 110.1 + column * 0.42
            ymin = 30.0 + row * 0.42
            xmax = xmin + 0.34
            ymax = ymin + 0.34
            ring = arcpy.Array(
                [
                    arcpy.Point(xmin, ymin),
                    arcpy.Point(xmax, ymin),
                    arcpy.Point(xmax, ymax),
                    arcpy.Point(xmin, ymax),
                    arcpy.Point(xmin, ymin),
                ]
            )
            polygon_rows.append(
                (
                    arcpy.Polygon(ring, spatial_reference),
                    f"District {index + 1:02d}",
                    20.0 + index * 7.5,
                    10.0 + (index % 4) * 12.0 + row * 4.0,
                )
            )
            index += 1
    with arcpy.da.InsertCursor(
        str(districts), ["SHAPE@", "NAME", "METRIC_A", "METRIC_B"]
    ) as cursor:
        for record in polygon_rows:
            cursor.insertRow(record)

    arcpy.management.CreateFeatureclass(
        str(GDB), residents.name, "POINT", spatial_reference=spatial_reference
    )
    arcpy.management.AddField(str(residents), "NAME", "TEXT", field_length=40)
    arcpy.management.AddField(str(residents), "TYPE", "TEXT", field_length=20)
    arcpy.management.AddField(str(residents), "POP", "LONG")
    with arcpy.da.InsertCursor(
        str(residents), ["SHAPE@XY", "NAME", "TYPE", "POP"]
    ) as cursor:
        for index in range(120):
            column = index % 15
            row = index // 15
            x = 110.13 + column * 0.11 + (row % 2) * 0.025
            y = 30.03 + row * 0.16 + (column % 3) * 0.018
            cursor.insertRow(
                (
                    (x, y),
                    f"Settlement {index + 1:03d}",
                    ("Town", "Village", "Site")[index % 3],
                    500 + ((index * 7919) % 95000),
                )
            )

    manifest = {
        "districts": str(districts),
        "residents": str(residents),
        "source_data_modified": False,
        "generated_under_output_root": True,
    }
    manifest_path = OUTPUT / "manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(manifest, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
