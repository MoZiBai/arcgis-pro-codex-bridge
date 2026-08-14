from __future__ import annotations

import json
from pathlib import Path

import arcpy
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "outputs" / "bridge_runtime_tests" / "raster_inputs_v016"


def save_array(array: np.ndarray, path: Path) -> None:
    raster = arcpy.NumPyArrayToRaster(
        array,
        arcpy.Point(110.0, 30.0),
        0.01,
        0.01,
    )
    raster.save(str(path))
    arcpy.management.DefineProjection(str(path), arcpy.SpatialReference(4490))


def main() -> int:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    arcpy.env.overwriteOutput = True

    height = 96
    width = 128
    yy, xx = np.mgrid[0:height, 0:width]
    red = np.clip(xx / (width - 1) * 255, 0, 255).astype(np.uint8)
    green = np.clip(yy / (height - 1) * 255, 0, 255).astype(np.uint8)
    blue = np.clip((np.sin(xx / 10.0) + 1) * 110 + 20, 0, 255).astype(np.uint8)

    red_path = OUTPUT / "v016_red.tif"
    green_path = OUTPUT / "v016_green.tif"
    blue_path = OUTPUT / "v016_blue.tif"
    save_array(red, red_path)
    save_array(green, green_path)
    save_array(blue, blue_path)

    rgb_path = OUTPUT / "v016_rgb.tif"
    arcpy.management.CompositeBands(
        [str(red_path), str(green_path), str(blue_path)], str(rgb_path)
    )

    categorical = (
        1
        + (xx >= width // 2).astype(np.int16)
        + 2 * (yy >= height // 2).astype(np.int16)
    ).astype(np.int16)
    categorical_path = OUTPUT / "v016_categories.tif"
    save_array(categorical, categorical_path)
    arcpy.management.BuildRasterAttributeTable(str(categorical_path), "Overwrite")

    magnitude = np.sqrt((xx - width / 2) ** 2 + (yy - height / 2) ** 2).astype(
        np.float32
    )
    direction = (
        (np.degrees(np.arctan2(yy - height / 2, xx - width / 2)) + 360) % 360
    ).astype(np.float32)
    magnitude_path = OUTPUT / "v016_magnitude.tif"
    direction_path = OUTPUT / "v016_direction.tif"
    save_array(magnitude, magnitude_path)
    save_array(direction, direction_path)
    vector_path = OUTPUT / "v016_vector_field.tif"
    arcpy.management.CompositeBands(
        [str(magnitude_path), str(direction_path)], str(vector_path)
    )

    elevation = (
        300
        + 4.5 * xx
        + 2.0 * yy
        + 160 * np.sin(xx / 12.0) * np.cos(yy / 15.0)
    ).astype(np.float32)
    elevation_path = OUTPUT / "v016_elevation.tif"
    save_array(elevation, elevation_path)

    for raster_path in (rgb_path, categorical_path, vector_path, elevation_path):
        arcpy.management.CalculateStatistics(str(raster_path))
        arcpy.management.BuildPyramids(str(raster_path))

    colormap_path = OUTPUT / "v016_categories.clr"
    colormap_path.write_text(
        "1 38 70 83\n"
        "2 42 157 143\n"
        "3 233 196 106\n"
        "4 231 111 81\n",
        encoding="ascii",
    )

    manifest = {
        "rgb": str(rgb_path),
        "categorical": str(categorical_path),
        "vector_field": str(vector_path),
        "elevation": str(elevation_path),
        "colormap": str(colormap_path),
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
