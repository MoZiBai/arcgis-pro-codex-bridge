from __future__ import annotations

import json
import math
import os
import sys
import traceback
from pathlib import Path
from typing import Any


def json_safe(value: Any) -> Any:
    if value is None or isinstance(value, (str, int, bool)):
        return value
    if isinstance(value, float):
        return value if math.isfinite(value) else None
    if isinstance(value, dict):
        return {str(key): json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple, set)):
        return [json_safe(item) for item in value]
    return str(value)


def describe_spatial_reference(sr: Any) -> dict[str, Any] | None:
    if sr is None:
        return None
    return {
        "name": getattr(sr, "name", None),
        "factoryCode": getattr(sr, "factoryCode", None),
        "type": getattr(sr, "type", None),
        "linearUnitName": getattr(sr, "linearUnitName", None),
        "angularUnitName": getattr(sr, "angularUnitName", None),
    }


def action_status(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    raw_install = arcpy.GetInstallInfo() or {}
    public_install_keys = (
        "ProductName",
        "Version",
        "BuildNumber",
        "LicenseLevel",
        "LicenseType",
        "InstallType",
    )
    return {
        "install": {
            key: json_safe(raw_install.get(key))
            for key in public_install_keys
            if raw_install.get(key) not in (None, "")
        },
        "product_info": arcpy.ProductInfo(),
        "python": ".".join(str(value) for value in sys.version_info[:3]),
    }


def action_scan(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    folder = payload["folder"]
    recursive = bool(payload.get("recursive", True))
    max_items = int(payload.get("max_items", 500))
    items: list[dict[str, Any]] = []
    truncated = False

    for dirpath, dirnames, filenames in arcpy.da.Walk(folder, topdown=True):
        for filename in filenames:
            dataset = os.path.join(dirpath, filename)
            try:
                desc = arcpy.Describe(dataset)
                items.append(
                    {
                        "path": dataset,
                        "name": getattr(desc, "name", filename),
                        "dataType": getattr(desc, "dataType", None),
                        "shapeType": getattr(desc, "shapeType", None),
                    }
                )
            except Exception:
                items.append({"path": dataset, "name": filename, "dataType": None})
            if len(items) >= max_items:
                truncated = True
                break
        if truncated:
            break
        if not recursive:
            dirnames[:] = []

    return {"folder": folder, "count": len(items), "truncated": truncated, "items": items}


def action_describe(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    path = payload["path"]
    desc = arcpy.Describe(path)
    result: dict[str, Any] = {
        "path": path,
        "name": getattr(desc, "name", None),
        "baseName": getattr(desc, "baseName", None),
        "dataType": getattr(desc, "dataType", None),
        "catalogPath": getattr(desc, "catalogPath", None),
        "shapeType": getattr(desc, "shapeType", None),
        "hasZ": getattr(desc, "hasZ", None),
        "hasM": getattr(desc, "hasM", None),
        "spatialReference": describe_spatial_reference(getattr(desc, "spatialReference", None)),
    }
    extent = getattr(desc, "extent", None)
    if extent is not None:
        result["extent"] = {
            "xmin": extent.XMin,
            "ymin": extent.YMin,
            "xmax": extent.XMax,
            "ymax": extent.YMax,
        }
    try:
        result["fields"] = [
            {
                "name": field.name,
                "aliasName": field.aliasName,
                "type": field.type,
                "length": field.length,
                "nullable": field.isNullable,
                "required": field.required,
            }
            for field in arcpy.ListFields(path)
        ]
    except Exception:
        result["fields"] = []
    try:
        result["rowCount"] = int(arcpy.management.GetCount(path)[0])
    except Exception:
        result["rowCount"] = None
    if str(result.get("dataType") or "").lower() in {
        "rasterdataset",
        "rasterband",
        "mosaicdataset",
    }:
        raster_info: dict[str, Any] = {
            "width": getattr(desc, "width", None),
            "height": getattr(desc, "height", None),
            "bandCount": getattr(desc, "bandCount", None),
            "pixelType": getattr(desc, "pixelType", None),
            "meanCellWidth": getattr(desc, "meanCellWidth", None),
            "meanCellHeight": getattr(desc, "meanCellHeight", None),
            "format": getattr(desc, "format", None),
            "compressionType": getattr(desc, "compressionType", None),
        }
        for property_name, result_name in (
            ("MINIMUM", "minimum"),
            ("MAXIMUM", "maximum"),
            ("MEAN", "mean"),
            ("STD", "standardDeviation"),
        ):
            try:
                value = arcpy.management.GetRasterProperties(path, property_name).getOutput(0)
                raster_info[result_name] = float(value)
            except Exception:
                raster_info[result_name] = None
        result["raster"] = raster_info
    return result


def action_field_values(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    path = payload["path"]
    field = payload["field"]
    max_values = int(payload.get("max_values", 200))
    field_names = {item.name.lower(): item.name for item in arcpy.ListFields(path)}
    actual_field = field_names.get(field.lower())
    if not actual_field:
        raise ValueError(f"Field not found: {field}")
    values = []
    seen = set()
    truncated = False
    with arcpy.da.SearchCursor(path, [actual_field]) as cursor:
        for (value,) in cursor:
            marker = json.dumps(json_safe(value), ensure_ascii=True, sort_keys=True)
            if marker in seen:
                continue
            seen.add(marker)
            values.append(json_safe(value))
            if len(values) >= max_values:
                truncated = True
                break
    values.sort(key=lambda item: "" if item is None else str(item))
    return {"path": path, "field": actual_field, "count": len(values), "truncated": truncated, "values": values}


TOOLBOX_SUFFIX_TO_MODULE = {
    "analysis": "analysis",
    "management": "management",
    "conversion": "conversion",
    "cartography": "cartography",
    "edit": "edit",
    "lr": "lr",
    "na": "na",
    "stats": "stats",
    "3d": "ddd",
    "sa": "sa",
}


def _resolve_gp_tool(arcpy: Any, requested: str) -> tuple[str, str, Any]:
    text = str(requested or "").strip()
    if "." in text:
        module_name, tool_name = text.split(".", 1)
        if not module_name.isidentifier() or not tool_name.isidentifier():
            raise ValueError(f"Invalid ArcPy tool name: {requested}")
        module = getattr(arcpy, module_name, None)
        tool = getattr(module, tool_name, None) if module is not None else None
        if not callable(tool):
            raise ValueError(f"ArcPy tool is not installed: {requested}")
        legacy_name = str(getattr(tool, "__esri_toolname__", "") or "")
        if not legacy_name:
            legacy_name = f"{tool_name}_{module_name}"
        return f"{module_name}.{tool_name}", legacy_name, tool
    if "_" not in text:
        raise ValueError("tool must be canonical (management.AddJoin) or legacy (AddJoin_management)")
    tool_name, suffix = text.rsplit("_", 1)
    module_name = TOOLBOX_SUFFIX_TO_MODULE.get(suffix.lower(), suffix.lower())
    if not module_name.isidentifier() or not tool_name.isidentifier():
        raise ValueError(f"Invalid ArcPy tool name: {requested}")
    module = getattr(arcpy, module_name, None)
    tool = getattr(module, tool_name, None) if module is not None else None
    if not callable(tool):
        raise ValueError(f"ArcPy tool is not installed: {requested}")
    return f"{module_name}.{tool_name}", text, tool


def _tool_doc(tool: Any) -> tuple[str | None, str | None, str | None]:
    doc = str(getattr(tool, "__doc__", "") or "").replace("\r\n", "\n").strip()
    if not doc:
        return None, None, None
    lines = doc.splitlines()
    signature = lines[0].strip() or None
    paragraphs: list[str] = []
    current: list[str] = []
    for line in lines[1:]:
        stripped = line.strip()
        if stripped.upper() in {"INPUTS:", "OUTPUTS:", "RETURN VALUE:"}:
            break
        if not stripped:
            if current:
                paragraphs.append(" ".join(current))
                current = []
            continue
        current.append(stripped)
    if current:
        paragraphs.append(" ".join(current))
    summary = next((item for item in paragraphs if item), None)
    return signature, summary, doc


def action_search_gp_tools(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    query = str(payload.get("query") or "").strip()
    if not query:
        raise ValueError("query is required")
    requested_toolboxes = {
        str(item).strip().lower() for item in (payload.get("toolboxes") or []) if str(item).strip()
    }
    maximum = min(max(int(payload.get("max_results", 50)), 1), 500)
    terms = [item for item in query.casefold().split() if item]
    matches: list[tuple[int, str, str]] = []
    installed = arcpy.ListTools("*") or []
    for legacy_name in installed:
        if "_" not in legacy_name:
            continue
        tool_name, suffix = legacy_name.rsplit("_", 1)
        module_name = TOOLBOX_SUFFIX_TO_MODULE.get(suffix.lower(), suffix.lower())
        if requested_toolboxes and module_name.lower() not in requested_toolboxes and suffix.lower() not in requested_toolboxes:
            continue
        canonical = f"{module_name}.{tool_name}"
        haystack = f"{canonical} {legacy_name}".casefold()
        if not all(term in haystack for term in terms):
            continue
        query_folded = query.casefold()
        canonical_folded = canonical.casefold()
        legacy_folded = legacy_name.casefold()
        score = 0
        if canonical_folded == query_folded or legacy_folded == query_folded:
            score += 1000
        if canonical_folded.startswith(query_folded) or tool_name.casefold().startswith(query_folded):
            score += 300
        score += max(0, 100 - canonical_folded.find(terms[0])) if terms else 0
        matches.append((score, canonical, legacy_name))
    matches.sort(key=lambda item: (-item[0], item[1].casefold()))
    results: list[dict[str, Any]] = []
    for _, canonical, legacy_name in matches[:maximum]:
        _, _, tool = _resolve_gp_tool(arcpy, canonical)
        signature, summary, _ = _tool_doc(tool)
        results.append(
            {
                "tool": canonical,
                "legacy_name": legacy_name,
                "toolbox": canonical.split(".", 1)[0],
                "signature": signature,
                "summary": summary,
            }
        )
    return {
        "query": query,
        "installed_tool_count": len(installed),
        "matched_count": len(matches),
        "returned_count": len(results),
        "truncated": len(matches) > maximum,
        "results": results,
        "source_data_modified": False,
    }


def action_describe_gp_tool(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    canonical, legacy_name, tool = _resolve_gp_tool(arcpy, str(payload.get("tool") or ""))
    signature, summary, doc = _tool_doc(tool)
    parameters: list[dict[str, Any]] = []
    for index, parameter in enumerate(arcpy.GetParameterInfo(legacy_name) or []):
        filter_object = getattr(parameter, "filter", None)
        filter_values = getattr(filter_object, "list", None) if filter_object is not None else None
        parameters.append(
            {
                "index": index,
                "name": getattr(parameter, "name", None),
                "display_name": getattr(parameter, "displayName", None),
                "direction": getattr(parameter, "direction", None),
                "parameter_type": getattr(parameter, "parameterType", None),
                "datatype": json_safe(getattr(parameter, "datatype", None)),
                "multi_value": bool(getattr(parameter, "multiValue", False)),
                "category": getattr(parameter, "category", None),
                "enabled": bool(getattr(parameter, "enabled", True)),
                "default_environment": getattr(parameter, "defaultEnvironmentName", None),
                "dependencies": json_safe(getattr(parameter, "parameterDependencies", None) or []),
                "default_value": json_safe(getattr(parameter, "valueAsText", None)),
                "filter_type": getattr(filter_object, "type", None) if filter_object is not None else None,
                "filter_values": json_safe(filter_values or []),
            }
        )
    include_doc = bool(payload.get("include_documentation", False))
    return {
        "tool": canonical,
        "legacy_name": legacy_name,
        "toolbox": canonical.split(".", 1)[0],
        "signature": signature,
        "summary": summary,
        "parameters": parameters,
        "documentation": doc if include_doc else None,
        "source_data_modified": False,
    }


def action_run_gp(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    toolbox_name, tool_name = payload["tool"].split(".", 1)
    toolbox = getattr(arcpy, toolbox_name)
    tool = getattr(toolbox, tool_name)
    workspace = payload.get("workspace")
    if workspace:
        arcpy.env.workspace = workspace
    arcpy.env.overwriteOutput = bool(payload.get("overwrite_output", False))
    result = tool(*(payload.get("args") or []), **(payload.get("kwargs") or {}))
    outputs: list[str] = []
    output_count = getattr(result, "outputCount", 0)
    for index in range(output_count):
        try:
            outputs.append(result.getOutput(index))
        except Exception:
            pass
    messages = result.getMessages() if hasattr(result, "getMessages") else None
    return {"tool": payload["tool"], "status": getattr(result, "status", None), "outputs": outputs, "messages": messages, "result": str(result)}


def layer_info(layer: Any) -> dict[str, Any]:
    item = {
        "name": layer.name,
        "isGroupLayer": bool(getattr(layer, "isGroupLayer", False)),
        "visible": getattr(layer, "visible", None),
    }
    try:
        if layer.supports("DATASOURCE"):
            item["dataSource"] = layer.dataSource
    except Exception:
        pass
    return item


def action_project_info(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    project = arcpy.mp.ArcGISProject(payload["aprx_path"])
    maps = []
    for map_obj in project.listMaps():
        maps.append(
            {
                "name": map_obj.name,
                "mapType": map_obj.mapType,
                "layers": [layer_info(layer) for layer in map_obj.listLayers()],
                "tables": [{"name": table.name, "dataSource": getattr(table, "dataSource", None)} for table in map_obj.listTables()],
            }
        )
    layouts = []
    for layout in project.listLayouts():
        layouts.append(
            {
                "name": layout.name,
                "pageHeight": layout.pageHeight,
                "pageWidth": layout.pageWidth,
                "elements": [{"name": element.name, "type": element.type} for element in layout.listElements()],
            }
        )
    return {"filePath": project.filePath, "maps": maps, "layouts": layouts}


def action_add_data(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    source = payload["source_aprx"]
    output = payload["output_aprx"]
    project = arcpy.mp.ArcGISProject(source)
    maps = project.listMaps(payload["map_name"]) if payload.get("map_name") else project.listMaps()
    if not maps:
        raise ValueError("No matching map found in project")
    map_obj = maps[0]
    added = []
    for path in payload["data_paths"]:
        layer = map_obj.addDataFromPath(path)
        added.append({"path": path, "layer": getattr(layer, "name", None)})
    Path(output).parent.mkdir(parents=True, exist_ok=True)
    project.saveACopy(output)
    return {"source_aprx": source, "output_aprx": output, "map": map_obj.name, "added": added}


def action_export_layout(arcpy: Any, payload: dict[str, Any]) -> dict[str, Any]:
    project = arcpy.mp.ArcGISProject(payload["aprx_path"])
    layouts = project.listLayouts(payload["layout_name"])
    if not layouts:
        raise ValueError(f"Layout not found: {payload['layout_name']}")
    layout = layouts[0]
    output = payload["output_file"]
    dpi = int(payload.get("dpi", 300))
    Path(output).parent.mkdir(parents=True, exist_ok=True)
    if output.lower().endswith(".pdf"):
        layout.exportToPDF(output, resolution=dpi)
    elif output.lower().endswith(".png"):
        layout.exportToPNG(output, resolution=dpi)
    else:
        raise ValueError("Only PDF and PNG exports are supported")
    return {"aprx_path": payload["aprx_path"], "layout": layout.name, "output_file": output, "dpi": dpi}


ACTIONS = {
    "status": action_status,
    "scan": action_scan,
    "describe": action_describe,
    "field_values": action_field_values,
    "search_gp_tools": action_search_gp_tools,
    "describe_gp_tool": action_describe_gp_tool,
    "run_gp": action_run_gp,
    "project_info": action_project_info,
    "add_data": action_add_data,
    "export_layout": action_export_layout,
}


def main() -> int:
    try:
        request = json.loads(sys.stdin.read())
        import arcpy

        action = request["action"]
        if action not in ACTIONS:
            raise KeyError(f"Unknown action: {action}")
        result = ACTIONS[action](arcpy, request.get("payload") or {})
        print(json.dumps({"ok": True, "result": json_safe(result)}, ensure_ascii=True))
        return 0
    except Exception as exc:
        print(
            json.dumps(
                {
                    "ok": False,
                    "error": str(exc),
                    "type": type(exc).__name__,
                    "traceback": traceback.format_exc(),
                },
                ensure_ascii=True,
            )
        )
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
