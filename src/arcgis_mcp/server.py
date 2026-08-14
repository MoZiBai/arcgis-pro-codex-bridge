from __future__ import annotations

import argparse
import concurrent.futures
import json
import logging
import os
import re
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


VERSION = "0.28.1"
ROOT = Path(__file__).resolve().parents[2]
WORKER = Path(__file__).with_name("worker.py")
LOG = logging.getLogger("arcgis_mcp")
EMIT_LOCK = threading.Lock()


class RequestCancelled(RuntimeError):
    pass

SERVER_INSTRUCTIONS = (
    "Use this ArcGIS Pro server only when the user's request clearly involves "
    "ArcGIS Pro, ArcPy, GIS datasets, spatial analysis, maps, layers, layouts, "
    "cartography, geoprocessing, or the currently open ArcGIS Pro project. Do "
    "not call any tool for unrelated coding, ordinary files, web research, "
    "office documents, or general questions. Before the first GIS operation "
    "call arcgis_status; inspect requested data with scan_data_folder and "
    "describe_dataset. Preserve source data and write derived outputs only "
    "under the configured output root. Use live_* tools only when the request "
    "requires the open ArcGIS Pro project. This build targets ArcGIS Pro 3.6 "
    "only; ArcMap and ArcGIS Desktop 10.2-10.8 are unsupported."
)

ALLOWED_TOOLBOXES = {
    "analysis",
    "management",
    "conversion",
    "cartography",
    "edit",
    "lr",
    "na",
    "stats",
    "ddd",
    "sa",
}

DESTRUCTIVE_TOOL_NAMES = {
    "delete",
    "deletefeatures",
    "deletefield",
    "deleterows",
    "deletedomain",
    "deletecodedvaluefromdomain",
    "truncate",
    "truncatetable",
    "removeindex",
}

SCHEMA_TOOL_PATHS = {
    "management.addfield",
    "management.alterfield",
    "management.deletefield",
    "management.addindex",
    "management.removeindex",
    "management.createdomain",
    "management.deletedomain",
    "management.addcodedvaluetodomain",
    "management.deletecodedvaluefromdomain",
    "management.assigndomaintofield",
    "management.removedomainfromfield",
    "management.setsubtypefield",
    "management.addsubtype",
    "management.removesubtype",
    "management.setdefaultsubtype",
    "management.enableeditortracking",
    "management.disableeditortracking",
}

DESTRUCTIVE_SCHEMA_TOOL_PATHS = {
    "management.deletefield",
    "management.removeindex",
    "management.deletedomain",
    "management.deletecodedvaluefromdomain",
    "management.removedomainfromfield",
    "management.removesubtype",
    "management.disableeditortracking",
}

SOURCE_MUTATING_TOOL_PATHS = {
    "management.append",
    "management.calculatefield",
    "management.calculatefields",
    "management.repairgeometry",
    "management.integrate",
    "edit.snap",
    "management.addattachments",
    "management.removeattachments",
    "management.enableattachments",
    "management.disableattachments",
    "management.addglobalids",
    "management.registerwithgeodatabase",
    "management.unregisterasversioned",
    "management.registerasversioned",
}

PUBLISHING_TOOL_PATHS = {
    "server.stageservice",
    "server.uploadservicedefinition",
    "management.packagemap",
    "management.packageproject",
    "management.packagelayer",
    "management.createmaptilepackage",
    "management.createvectortilepackage",
}


TOOLS = [
    {
        "name": "arcgis_status",
        "description": "Check the configured ArcGIS Pro Python/ArcPy runtime and report its version.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "search_arcgis_framework_registry",
        "description": "Search the locally installed ArcGIS Pro DAML and command-search registries for exact command, tool, pane, tab, gallery, menu, and property-page IDs. Read-only; every returned ID still requires side-effect review before generic Framework execution is allowlisted.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "query": {"type": "string"},
                "kinds": {
                    "type": "array",
                    "items": {
                        "type": "string",
                        "enum": [
                            "button", "tool", "dockPane", "pane", "tab",
                            "backstageTab", "propertyPage", "gallery", "comboBox",
                            "menu", "splitButton"
                        ],
                    },
                    "maxItems": 20,
                },
                "extension": {"type": ["string", "null"]},
                "max_results": {"type": "integer", "minimum": 1, "maximum": 500, "default": 50},
            },
            "required": ["query"],
            "additionalProperties": False,
        },
    },
    {
        "name": "search_arcgis_geoprocessing_registry",
        "description": "Search the 1,000+ geoprocessing tools installed with the local ArcGIS Pro runtime. Returns canonical ArcPy paths, signatures, summaries, safety hints, and whether the generic runner currently permits the toolbox.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "query": {"type": "string"},
                "toolboxes": {
                    "type": "array",
                    "items": {"type": "string"},
                    "maxItems": 50,
                },
                "max_results": {"type": "integer", "minimum": 1, "maximum": 500, "default": 50},
            },
            "required": ["query"],
            "additionalProperties": False,
        },
    },
    {
        "name": "describe_arcgis_geoprocessing_tool",
        "description": "Inspect one installed ArcGIS Pro geoprocessing tool, including localized parameter names, input/output directions, datatypes, required/optional status, defaults, filters, dependencies, documentation, and safety gates.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "tool": {"type": "string", "description": "Canonical path such as management.AddJoin or legacy name AddJoin_management"},
                "include_documentation": {"type": "boolean", "default": False},
            },
            "required": ["tool"],
            "additionalProperties": False,
        },
    },
    {
        "name": "search_arcgis_sdk_registry",
        "description": "Search public types, methods, constructors, properties, and enum values in the locally installed ArcGIS Pro SDK assemblies. This is a read-only discovery catalog, not an unrestricted reflection executor; returned APIs still require a reviewed bridge implementation before invocation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "query": {
                    "type": "string",
                    "description": "Literal text to match in a public type name or member signature, such as MapToClient, Legend, AttributeRule, or ArcGIS.Desktop.Layouts.",
                },
                "assemblies": {
                    "type": "array",
                    "items": {"type": "string"},
                    "maxItems": 30,
                    "description": "Optional case-insensitive assembly-name filters, such as ArcGIS.Desktop.Mapping.",
                },
                "max_results": {"type": "integer", "minimum": 1, "maximum": 200, "default": 50},
            },
            "required": ["query"],
            "additionalProperties": False,
        },
    },
    {
        "name": "reload_arcgis_mcp_policy",
        "description": "Explicitly reload only safety gates and exact Framework allowlists from the configured config.json without restarting ArcGIS Pro or the MCP process. Paths and runtime locations are intentionally not hot-reloaded.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "confirm_reload": {"type": "boolean", "const": True},
            },
            "required": ["confirm_reload"],
            "additionalProperties": False,
        },
    },
    {
        "name": "scan_data_folder",
        "description": "Inventory GIS datasets in an allowed folder using arcpy.da.Walk.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "folder": {"type": "string"},
                "recursive": {"type": "boolean", "default": True},
                "max_items": {"type": "integer", "minimum": 1, "maximum": 5000, "default": 500},
            },
            "required": ["folder"],
            "additionalProperties": False,
        },
    },
    {
        "name": "describe_dataset",
        "description": "Describe a GIS dataset, including fields, extent, spatial reference, and row count when available.",
        "inputSchema": {
            "type": "object",
            "properties": {"path": {"type": "string"}},
            "required": ["path"],
            "additionalProperties": False,
        },
    },
    {
        "name": "list_field_values",
        "description": "List distinct values from one field of an allowed GIS dataset, with a bounded result size.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string"},
                "field": {"type": "string"},
                "max_values": {"type": "integer", "minimum": 1, "maximum": 2000, "default": 200},
            },
            "required": ["path", "field"],
            "additionalProperties": False,
        },
    },
    {
        "name": "run_geoprocessing_tool",
        "description": "Run an ArcPy geoprocessing tool such as analysis.Buffer or management.Project. Destructive tools are blocked by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "tool": {"type": "string", "description": "ArcPy toolbox and tool, for example analysis.Buffer"},
                "args": {"type": "array", "items": {}, "default": []},
                "kwargs": {"type": "object", "default": {}},
                "workspace": {"type": ["string", "null"]},
                "overwrite_output": {"type": "boolean", "default": False},
            },
            "required": ["tool"],
            "additionalProperties": False,
        },
    },
    {
        "name": "project_info",
        "description": "List maps, layers, tables, layouts, and layout elements in an ArcGIS Pro project (.aprx).",
        "inputSchema": {
            "type": "object",
            "properties": {"aprx_path": {"type": "string"}},
            "required": ["aprx_path"],
            "additionalProperties": False,
        },
    },
    {
        "name": "add_data_to_project_copy",
        "description": "Create a copy of an .aprx and add datasets to one map, preserving the source project.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "source_aprx": {"type": "string"},
                "output_aprx": {"type": "string"},
                "data_paths": {"type": "array", "items": {"type": "string"}, "minItems": 1},
                "map_name": {"type": ["string", "null"]},
            },
            "required": ["source_aprx", "output_aprx", "data_paths"],
            "additionalProperties": False,
        },
    },
    {
        "name": "export_layout",
        "description": "Export a named layout from an .aprx to PDF or PNG under the configured output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "aprx_path": {"type": "string"},
                "layout_name": {"type": "string"},
                "output_file": {"type": "string"},
                "dpi": {"type": "integer", "minimum": 72, "maximum": 1200, "default": 300},
            },
            "required": ["aprx_path", "layout_name", "output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_bridge_status",
        "description": "Check the ArcGIS Pro in-app bridge and return detailed maps, layers, layouts, elements, protocol version, and capabilities.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "live_capabilities",
        "description": "Return the exact action set implemented by the installed ArcGIS Pro add-in bridge.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "live_project_inventory",
        "description": "Inspect the open ArcGIS Pro project, maps, layouts, dirty state, editing state, and default paths.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "live_save_project",
        "description": "Explicitly save the currently open project at its existing path. Source datasets are not changed by the save itself.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "live_get_metadata",
        "description": "Read bounded metadata XML from the current project, map, layer, layout, or report.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "target_type": {"type": "string", "enum": ["project", "map", "layer", "layout", "report"]},
                "target_name": {"type": ["string", "null"]},
                "map_name": {"type": ["string", "null"]},
                "max_characters": {"type": "integer", "minimum": 0, "maximum": 1000000, "default": 100000},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_metadata",
        "description": "Set project-item metadata only after explicit confirmation; layer/source metadata writes are blocked.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "target_type": {"type": "string", "enum": ["project", "map", "layout", "report"]},
                "target_name": {"type": ["string", "null"]},
                "metadata_xml": {"type": "string"},
                "confirm_write": {"type": "boolean", "const": True},
            },
            "required": ["metadata_xml", "confirm_write"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_data",
        "description": "Add datasets to a map in the currently open ArcGIS Pro project through the in-app bridge.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "data_paths": {"type": "array", "items": {"type": "string"}, "minItems": 1},
                "map_name": {"type": ["string", "null"]},
            },
            "required": ["data_paths"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_create_map",
        "description": "Create an empty editable 2D map, local scene, or global scene in the currently open ArcGIS Pro project.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": "string"},
                "map_type": {"type": "string", "enum": ["Map", "Scene"], "default": "Map"},
                "viewing_mode": {"type": "string", "enum": ["Map", "SceneLocal", "SceneGlobal"]},
                "basemap": {
                    "type": "string",
                    "enum": ["None", "ProjectDefault", "Satellite", "Hybrid", "Streets", "Topographic", "DarkGray", "Gray", "NationalGeographic", "Oceans", "Terrain", "OpenStreetMap", "NavigationVector", "StreetsNightVector", "StreetsWithReliefVector", "GrayVector", "StreetsVector", "TopographicVector", "DarkGrayVector"],
                    "default": "None",
                },
                "open_map": {"type": "boolean", "default": False},
            },
            "required": ["map_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_rename_map",
        "description": "Rename a map project item without changing source datasets.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": "string"}, "new_name": {"type": "string"}},
            "required": ["map_name", "new_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_duplicate_map",
        "description": "Duplicate a map and its layer configuration without copying source datasets.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": "string"}, "new_name": {"type": "string"}},
            "required": ["map_name", "new_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_map",
        "description": "Remove a map project item only after an explicit confirmation flag. Source datasets are never deleted.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": "string"}, "confirm_remove": {"type": "boolean", "const": True}},
            "required": ["map_name", "confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_open_map",
        "description": "Open a named map in an ArcGIS Pro map pane.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": "string"}},
            "required": ["map_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_bookmarks",
        "description": "List bookmarks in a map.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_create_bookmark",
        "description": "Create a bookmark from the active map view camera.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "bookmark_name": {"type": "string"},
                "description": {"type": "string"},
            },
            "required": ["bookmark_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_bookmark",
        "description": "Remove a named bookmark from a map.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}, "bookmark_name": {"type": "string"}},
            "required": ["bookmark_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_zoom_to_bookmark",
        "description": "Zoom the active map view to a named bookmark.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}, "bookmark_name": {"type": "string"}},
            "required": ["bookmark_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_bookmark",
        "description": "Rename or update a bookmark description, folder and optionally its extent from the active map view.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "bookmark_name": {"type": "string"},
                "new_name": {"type": ["string", "null"]},
                "description": {"type": ["string", "null"]},
                "folder_name": {"type": ["string", "null"]},
                "use_active_view_extent": {"type": "boolean"},
            },
            "required": ["bookmark_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_move_bookmark",
        "description": "Move a bookmark to a specified zero-based position in the map bookmark collection.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "bookmark_name": {"type": "string"},
                "index": {"type": "integer", "minimum": 0},
            },
            "required": ["bookmark_name", "index"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_import_bookmarks",
        "description": "Import native ArcGIS bookmark definitions from an allowed .bkmx file into a map.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "input_file": {"type": "string"},
            },
            "required": ["input_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_export_bookmarks",
        "description": "Export a map's native bookmark collection to a .bkmx file under the output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "output_file": {"type": "string"},
            },
            "required": ["output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_camera",
        "description": "Inspect the active map view camera, scale, rotation, and spatial reference.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_camera",
        "description": "Set numeric properties of the active map view camera without mouse or keyboard automation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "x": {"type": "number"},
                "y": {"type": "number"},
                "z": {"type": "number"},
                "scale": {"type": "number", "minimum": 1},
                "heading": {"type": "number"},
                "pitch": {"type": "number"},
                "roll": {"type": "number"},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_zoom_full_extent",
        "description": "Zoom the active map view to its full extent.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_map_view_state",
        "description": "Inspect the active map view's 2D/3D mode, field of view, visual effects, post-processing effects, and range state.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_map_view",
        "description": "Configure active 2D/3D viewing mode, scene drawing mode, field of view, camera effects, visual effects, and post-processing effects.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "viewing_mode": {"type": "string", "enum": ["Map", "SceneLocal", "SceneGlobal", "MapStereo"]},
                "scene_drawing_mode": {"type": "string", "enum": ["Perspective", "Isometric"]},
                "field_of_view": {"type": "number", "minimum": 1, "maximum": 170},
                "camera_effect_preset": {"type": "string", "enum": ["depth_of_field", "tilt_shift"]},
                "visual_effect_preset": {"type": "string", "enum": ["black_and_white", "blueprint", "cross_mosaic", "hex_mosaic", "pixelated", "halftone", "outline", "pencil_sketch", "toon", "watercolor", "monochromatic"]},
                "postprocessing_presets": {
                    "type": "array",
                    "items": {"type": "string", "enum": ["bloom", "vignette", "grain", "color_grading"]},
                    "maxItems": 4,
                },
                "effect_parameters": {"type": "object", "additionalProperties": True},
                "camera_effect": {
                    "type": "object",
                    "properties": {"cim_type": {"type": "string"}, "cim_json": {"type": "string"}},
                    "required": ["cim_type", "cim_json"],
                    "additionalProperties": False,
                },
                "visual_effect": {
                    "type": "object",
                    "properties": {"cim_type": {"type": "string"}, "cim_json": {"type": "string"}},
                    "required": ["cim_type", "cim_json"],
                    "additionalProperties": False,
                },
                "postprocessing_effects": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {"cim_type": {"type": "string"}, "cim_json": {"type": "string"}},
                        "required": ["cim_type", "cim_json"],
                        "additionalProperties": False,
                    },
                    "maxItems": 32,
                },
                "clear_camera_effect": {"type": "boolean", "default": False},
                "clear_visual_effect": {"type": "boolean", "default": False},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_view_filter_state",
        "description": "Inspect the active ArcGIS Pro view's time extent, numeric range, active range name, and floor-filter state.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_view_filters",
        "description": "Configure active-view time, numeric range, named range, and floor filtering without changing layer data or definitions.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "clear_time": {"type": "boolean", "default": False},
                "time": {
                    "type": "object",
                    "properties": {
                        "start": {"type": ["string", "null"]},
                        "end": {"type": ["string", "null"]},
                        "exclude_start": {"type": "boolean"},
                        "exclude_end": {"type": "boolean"},
                        "time_reference": {
                            "type": ["object", "null"],
                            "properties": {
                                "time_zone_iana_id": {"type": ["string", "null"]},
                                "time_zone_name_id": {"type": ["string", "null"]},
                                "respects_daylight_saving_time": {"type": "boolean"},
                                "respects_dynamic_adjustment_rules": {"type": "boolean"},
                            },
                            "additionalProperties": False,
                        },
                    },
                    "additionalProperties": False,
                },
                "clear_range": {"type": "boolean", "default": False},
                "range": {
                    "type": "object",
                    "properties": {
                        "minimum": {"type": ["number", "null"]},
                        "maximum": {"type": ["number", "null"]},
                        "exclude_minimum": {"type": "boolean"},
                        "exclude_maximum": {"type": "boolean"},
                        "exclusion": {"type": "boolean"},
                    },
                    "additionalProperties": False,
                },
                "active_range_name": {"type": ["string", "null"]},
                "clear_floor_filter": {"type": "boolean", "default": False},
                "floor_filter": {
                    "type": "object",
                    "properties": {
                        "enabled": {"type": "boolean"},
                        "minimized": {"type": "boolean"},
                        "long_names": {"type": "boolean"},
                        "pinned_levels": {"type": "boolean"},
                        "focus_selected_facility": {"type": "boolean"},
                        "selected_site_id": {"type": ["string", "null"]},
                        "selected_facility_id": {"type": ["string", "null"]},
                        "selected_level_id": {"type": ["string", "null"]},
                        "selected_vertical_order": {"type": "integer"},
                        "use_vertical_order": {"type": "boolean"},
                        "site_facility_ids": {"type": "array", "items": {"type": "string"}, "maxItems": 10000},
                        "site_level_ids": {"type": "array", "items": {"type": "string"}, "maxItems": 10000},
                        "floor_visibility_mode": {"type": "string"},
                    },
                    "additionalProperties": False,
                },
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_active_view_context",
        "description": "Inspect the active map view's readiness, drawing/TOC mode, size, camera history, and selected layers, tables, elevation members, legend classes, and data source.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_active_view_context",
        "description": "Pause/resume drawing, switch TOC content, select map/elevation members, clear TOC selection, cancel drawing, or request redraw in the active view.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "drawing_paused": {"type": "boolean"},
                "toc_content": {"type": "string", "enum": ["DrawingOrder", "DataSource", "Selection", "Editing", "Snapping", "Labeling", "PerspectiveImagery", "OrthoMapping", "RealityMapping"]},
                "selected_layer_names": {"type": "array", "items": {"type": "string"}, "maxItems": 10000},
                "selected_table_names": {"type": "array", "items": {"type": "string"}, "maxItems": 10000},
                "selected_elevation_surface_names": {"type": "array", "items": {"type": "string"}, "maxItems": 1000},
                "selected_elevation_source_layer_names": {"type": "array", "items": {"type": "string"}, "maxItems": 10000},
                "clear_toc_selection": {"type": "boolean", "default": False},
                "cancel_drawing": {"type": "boolean", "default": False},
                "redraw": {"type": "boolean"},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_navigate_map_view",
        "description": "Use native ArcGIS Pro fixed zoom, camera history, or selection navigation in the active map view.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "operation": {"type": "string", "enum": ["zoom_in_fixed", "zoom_out_fixed", "next_camera", "previous_camera", "zoom_selected", "pan_selected"]},
                "duration_ms": {"type": "integer", "minimum": 0, "maximum": 60000, "default": 0},
                "maintain_view_direction": {"type": "boolean", "default": False},
            },
            "required": ["operation"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_transform_view_coordinates",
        "description": "Convert a coordinate between map, ArcGIS Pro view-client, and desktop-screen coordinate spaces in the active map view.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "operation": {"type": "string", "enum": ["map_to_client", "map_to_screen", "client_to_map", "screen_to_map", "client_to_screen", "screen_to_client"]},
                "x": {"type": "number"},
                "y": {"type": "number"},
                "wkid": {"type": ["integer", "null"]},
            },
            "required": ["operation", "x", "y"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_flash_features",
        "description": "Flash up to 1000 feature object IDs in the active map view, optionally showing their popups.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "object_ids": {"type": "array", "items": {"type": "integer"}, "minItems": 1, "maxItems": 1000},
                "show_popups": {"type": "boolean", "default": False},
            },
            "required": ["layer_name", "object_ids"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_show_feature_popup",
        "description": "Open the native ArcGIS Pro popup for a feature or table row in the active view.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "object_id": {"type": "integer"},
                "append": {"type": "boolean", "default": False},
                "dockable": {"type": "boolean", "default": True},
            },
            "required": ["map_member_name", "object_id"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_close_feature_popups",
        "description": "Close all native feature popups in the active ArcGIS Pro map view.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_feature_cache",
        "description": "Inspect, fill, clear, or cancel the native ArcGIS Pro feature cache for all or selected layers, and configure automatic cache filling.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "operation": {"type": "string", "enum": ["inspect", "fill", "clear", "cancel"], "default": "inspect"},
                "layer_names": {"type": "array", "items": {"type": "string"}, "maxItems": 10000},
                "auto_fill": {"type": "boolean"},
                "continue_on_failure": {"type": "boolean", "default": False},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_capture_view_thumbnail",
        "description": "Capture the active map view to a PNG or JPEG under the configured output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "output_file": {"type": "string"},
                "width": {"type": "integer", "minimum": 32, "maximum": 8192, "default": 800},
                "height": {"type": "integer", "minimum": 32, "maximum": 8192, "default": 600},
                "quality": {"type": "integer", "minimum": 1, "maximum": 100, "default": 90},
            },
            "required": ["output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_map_cim",
        "description": "Read the full native ArcGIS CIM definition for a map as an advanced compatibility escape hatch.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_map_cim",
        "description": "Replace a map's full native CIM definition only after explicit confirmation. This can configure long-tail map properties not covered by typed tools.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "cim_json": {"type": "string"},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_map_properties",
        "description": "Configure common typed map properties including reference scale, background, wraparound, scale snapping, display scales, overprint and defaults.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "reference_scale": {"type": "number", "minimum": 0},
                "background_color": {},
                "surface_color": {},
                "enable_wraparound": {"type": "boolean"},
                "snap_to_scales": {"type": "boolean"},
                "simulate_overprint": {"type": "boolean"},
                "auto_fill_feature_cache": {"type": "boolean"},
                "use_service_layer_ids": {"type": "boolean"},
                "include_maximum_in_scale_ranges": {"type": "boolean"},
                "default_scale": {"type": "number", "minimum": 0},
                "default_rotation": {"type": "number"},
                "scales": {"type": "array", "items": {}, "maxItems": 1000},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_save_map_file",
        "description": "Save a map and its layer configuration as a reusable native .mapx file under the output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "output_file": {"type": "string"},
                "overwrite": {"type": "boolean", "default": False},
            },
            "required": ["output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_describe_layer",
        "description": "Inspect a layer in the open ArcGIS Pro map, including source path, visibility, blending mode, renderer/colorizer, and optional CIM JSON.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "include_cim": {"type": "boolean", "default": False},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_layer_cim",
        "description": "Return the complete CIM JSON definition for a named layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"}
            },
            "required": ["layer_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_set_layer_cim",
        "description": "Replace a layer's same-type CIM definition while preserving its identity, source data connection, and group/graphics storage structure.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "cim_json": {"type": "string"},
                "confirm_replace_definition": {"type": "boolean"}
            },
            "required": ["layer_name", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_inspect_las_layer",
        "description": "Inspect a LAS dataset layer's eye-dome lighting, display/selection filters, surface constraints, dataset size, classes, returns, files, and extent.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_las_layer",
        "description": "Configure LAS eye-dome lighting, display filters, selectable classes, visible-point selection behavior, and active surface constraints.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "eye_dome_lighting_enabled": {"type": "boolean"},
                "eye_dome_lighting_strength": {"type": "number", "minimum": 0, "maximum": 100},
                "eye_dome_lighting_radius": {"type": "number", "minimum": 0},
                "selectable_class_codes": {"type": "array", "items": {"type": "integer", "minimum": 0}, "maxItems": 256},
                "select_visible_points": {"type": "boolean"},
                "active_surface_constraints": {"type": "array", "items": {"type": "string"}, "maxItems": 10000},
                "clear_selection": {"type": "boolean", "default": False},
                "display_filter_type": {"type": "string", "enum": ["AllPoints", "Ground", "NonGround", "FirstReturnPoints"]},
                "display_filter": {
                    "type": "object",
                    "properties": {
                        "class_codes": {"type": "array", "items": {"type": "integer", "minimum": 0}, "maxItems": 256},
                        "returns": {"type": "array", "items": {"type": "string"}, "maxItems": 32},
                        "surface_constraints": {"type": "array", "items": {"type": "string"}, "maxItems": 10000},
                        "key_points": {"type": "boolean"},
                        "synthetic_points": {"type": "boolean"},
                        "withheld_points": {"type": "boolean"},
                        "overlap_points": {"type": "boolean"},
                        "not_flagged": {"type": "boolean"},
                    },
                    "additionalProperties": False,
                },
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_inspect_voxel_layer",
        "description": "Inspect voxel variables, renderers, statistics, isosurfaces, volumes, sections, slices, lighting, time values, and container state.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "distribution_limit": {"type": "integer", "minimum": 0, "maximum": 4096, "default": 256},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_voxel_layer",
        "description": "Configure voxel visualization, vertical/lighting settings, selected variable, native CIM renderer, containers, and isosurface lifecycle.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "distribution_limit": {"type": "integer", "minimum": 0, "maximum": 4096, "default": 256},
                "vertical_exaggeration": {"type": "number", "minimum": 0},
                "cartographic_offset": {"type": "number"},
                "snappable": {"type": "boolean"},
                "diffuse_lighting_enabled": {"type": "boolean"},
                "diffuse_lighting": {"type": "number", "minimum": 0, "maximum": 1},
                "specular_lighting_enabled": {"type": "boolean"},
                "specular_lighting": {"type": "number", "minimum": 0, "maximum": 1},
                "visualization": {"type": "string", "enum": ["Volume", "Surface"]},
                "surface_container_expanded": {"type": "boolean"},
                "isosurface_container_visible": {"type": "boolean"},
                "isosurface_container_expanded": {"type": "boolean"},
                "section_container_visible": {"type": "boolean"},
                "section_container_expanded": {"type": "boolean"},
                "locked_section_container_visible": {"type": "boolean"},
                "locked_section_container_expanded": {"type": "boolean"},
                "slice_container_visible": {"type": "boolean"},
                "slice_container_expanded": {"type": "boolean"},
                "selected_variable": {"type": ["string", "null"]},
                "variable_name": {"type": ["string", "null"]},
                "renderer": {
                    "type": "object",
                    "properties": {"cim_type": {"type": "string"}, "cim_json": {"type": "string"}},
                    "required": ["cim_type", "cim_json"],
                    "additionalProperties": False,
                },
                "isosurfaces": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "operation": {"type": "string", "enum": ["create", "update", "delete"]},
                            "id": {"type": ["string", "null"]},
                            "name": {"type": ["string", "null"]},
                            "value": {"type": ["number", "null"]},
                            "visible": {"type": "boolean"},
                            "color": {},
                            "use_automatic_color": {"type": "boolean"},
                        },
                        "additionalProperties": False,
                    },
                    "maxItems": 1000,
                },
                "confirm_remove_isosurfaces": {"type": "boolean", "default": False},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_layer_schema",
        "description": "Return the field schema and key field roles for a feature layer in the open map.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}, "layer_name": {"type": "string"}},
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_query_features",
        "description": "Run a bounded read-only attribute query against a feature layer, with optional geometry summaries.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "where_clause": {"type": "string"},
                "fields": {"type": "array", "items": {"type": "string"}},
                "object_ids": {"type": "array", "items": {"type": "integer"}},
                "prefix_clause": {"type": "string"},
                "postfix_clause": {"type": "string"},
                "max_rows": {"type": "integer", "minimum": 1, "maximum": 5000, "default": 100},
                "include_geometry": {"type": "boolean", "default": False},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_summarize_field",
        "description": "Compute bounded read-only field statistics for a feature layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "field": {"type": "string"},
                "where_clause": {"type": "string"},
                "max_rows": {"type": "integer", "minimum": 1, "maximum": 1000000, "default": 100000},
            },
            "required": ["layer_name", "field"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_table_schema",
        "description": "Return the field schema and key field roles for a standalone table in the open map.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}, "table_name": {"type": "string"}},
            "required": ["table_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_query_table",
        "description": "Run a bounded read-only query against a standalone table in the open map.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "table_name": {"type": "string"},
                "where_clause": {"type": "string"},
                "fields": {"type": "array", "items": {"type": "string"}},
                "object_ids": {"type": "array", "items": {"type": "integer"}},
                "prefix_clause": {"type": "string"},
                "postfix_clause": {"type": "string"},
                "max_rows": {"type": "integer", "minimum": 1, "maximum": 5000, "default": 100},
            },
            "required": ["table_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_select_features",
        "description": "Select features by a bounded attribute/OID query in the open map.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "where_clause": {"type": "string"},
                "object_ids": {"type": "array", "items": {"type": "integer"}},
                "combination": {"type": "string", "enum": ["New", "Add", "Subtract", "And", "Xor"]},
                "max_ids": {"type": "integer", "minimum": 0, "maximum": 10000, "default": 1000},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_selection",
        "description": "Inspect selected object IDs in feature layers with bounded result sizes.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_names": {"type": "array", "items": {"type": "string"}},
                "max_ids": {"type": "integer", "minimum": 0, "maximum": 10000, "default": 1000},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_clear_selection",
        "description": "Clear selection from named feature layers or from the whole map.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_names": {"type": "array", "items": {"type": "string"}},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_update_feature_attributes",
        "description": "Stage attribute updates through an ArcGIS Pro EditOperation. Disabled unless allow_source_editing=true and explicitly confirmed.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "updates": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "object_id": {"type": "integer"},
                            "attributes": {"type": "object", "additionalProperties": True},
                        },
                        "required": ["object_id", "attributes"],
                        "additionalProperties": False,
                    },
                    "minItems": 1,
                },
                "operation_name": {"type": "string"},
                "select_modified": {"type": "boolean"},
                "confirm_source_edit": {"type": "boolean", "const": True},
            },
            "required": ["layer_name", "updates", "confirm_source_edit"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_create_features",
        "description": "Stage new point, line, or polygon features through an EditOperation. Disabled unless source editing is enabled and confirmed.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "features": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "geometry": {
                                "type": "object",
                                "properties": {
                                    "type": {"type": "string", "enum": ["point", "line", "polyline", "polygon"]},
                                    "coordinates": {"type": "array"},
                                },
                                "required": ["type", "coordinates"],
                                "additionalProperties": False,
                            },
                            "attributes": {"type": "object", "additionalProperties": True},
                        },
                        "required": ["geometry"],
                        "additionalProperties": False,
                    },
                    "minItems": 1,
                },
                "operation_name": {"type": "string"},
                "select_new": {"type": "boolean"},
                "confirm_source_edit": {"type": "boolean", "const": True},
            },
            "required": ["layer_name", "features", "confirm_source_edit"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_delete_features",
        "description": "Stage feature deletion. Requires both source-editing and destructive-tool config plus two explicit confirmation flags.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "object_ids": {"type": "array", "items": {"type": "integer"}, "minItems": 1},
                "operation_name": {"type": "string"},
                "confirm_source_edit": {"type": "boolean", "const": True},
                "confirm_delete": {"type": "boolean", "const": True},
            },
            "required": ["layer_name", "object_ids", "confirm_source_edit", "confirm_delete"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_save_edits",
        "description": "Save currently staged ArcGIS Pro edits. Disabled unless source editing is enabled and explicitly confirmed.",
        "inputSchema": {
            "type": "object",
            "properties": {"confirm_source_edit": {"type": "boolean", "const": True}},
            "required": ["confirm_source_edit"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_discard_edits",
        "description": "Discard currently staged ArcGIS Pro edits after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {"confirm_discard": {"type": "boolean", "const": True}},
            "required": ["confirm_discard"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_undo_edit",
        "description": "Undo the latest ArcGIS Pro edit operation. Disabled unless source editing is enabled and confirmed.",
        "inputSchema": {
            "type": "object",
            "properties": {"confirm_source_edit": {"type": "boolean", "const": True}},
            "required": ["confirm_source_edit"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_redo_edit",
        "description": "Redo the latest ArcGIS Pro edit operation. Disabled unless source editing is enabled and confirmed.",
        "inputSchema": {
            "type": "object",
            "properties": {"confirm_source_edit": {"type": "boolean", "const": True}},
            "required": ["confirm_source_edit"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_duplicate_layer",
        "description": "Duplicate a layer inside the current map without copying or changing its source dataset.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "new_name": {"type": ["string", "null"]},
                "group_name": {"type": ["string", "null"]},
                "index": {"type": "integer", "minimum": 0},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_create_group_layer",
        "description": "Create a root or nested group layer in the current ArcGIS Pro map.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "group_name": {"type": "string"},
                "parent_group_name": {"type": ["string", "null"]},
                "index": {"type": "integer", "minimum": 0},
            },
            "required": ["group_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_standalone_table",
        "description": "Add a local table dataset to a map as a standalone table without changing source data.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "table_path": {"type": "string"},
                "table_name": {"type": ["string", "null"]},
                "index": {"type": "integer", "minimum": 0},
            },
            "required": ["table_path"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_standalone_table",
        "description": "Rename or configure definition query, selection, and popups for a standalone table.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "table_name": {"type": "string"},
                "new_name": {"type": ["string", "null"]},
                "definition_query": {"type": "string"},
                "selectable": {"type": "boolean"},
                "show_popups": {"type": "boolean"},
            },
            "required": ["table_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_standalone_table",
        "description": "Remove a standalone table from a map without deleting its source dataset.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}, "table_name": {"type": "string"}},
            "required": ["table_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_standalone_table_cim",
        "description": "Read the full native CIM definition of a standalone table without reading or changing source rows.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "table_name": {"type": "string"},
            },
            "required": ["table_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_standalone_table_cim",
        "description": "Replace a same-type standalone-table CIM definition while preserving its source connection and identity by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "table_name": {"type": "string"},
                "cim_json": {"type": "string"},
                "preserve_name": {"type": "boolean", "default": True},
                "preserve_source_connection": {"type": "boolean", "default": True},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["table_name", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_run_geoprocessing_tool",
        "description": "Run an ArcPy tool inside the open ArcGIS Pro session and optionally add output datasets to the visible map.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "tool": {"type": "string"},
                "args": {"type": "array", "items": {}, "default": []},
                "kwargs": {"type": "object", "default": {}},
                "map_name": {"type": ["string", "null"]},
                "add_outputs_to_map": {"type": "boolean", "default": True},
                "overwrite_output": {"type": "boolean", "default": False},
            },
            "required": ["tool"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_run_schema_tool",
        "description": "Run an allowlisted geodatabase schema tool. Implemented but disabled unless allow_schema_changes=true; destructive schema tools also require allow_destructive_tools=true.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "tool": {"type": "string"},
                "args": {"type": "array", "items": {}, "default": []},
                "add_outputs_to_map": {"type": "boolean", "default": False},
                "overwrite_output": {"type": "boolean", "default": False},
                "confirm_schema_change": {"type": "boolean", "const": True},
            },
            "required": ["tool", "confirm_schema_change"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_run_publishing_tool",
        "description": "Run an allowlisted packaging, staging, or service-upload tool. Implemented but disabled unless allow_external_publishing=true and explicitly confirmed.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "tool": {"type": "string"},
                "args": {"type": "array", "items": {}, "default": []},
                "add_outputs_to_map": {"type": "boolean", "default": False},
                "overwrite_output": {"type": "boolean", "default": False},
                "confirm_publish": {"type": "boolean", "const": True},
            },
            "required": ["tool", "confirm_publish"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_import_layout_template",
        "description": "Import a .pagx layout template into the current ArcGIS Pro project.",
        "inputSchema": {
            "type": "object",
            "properties": {"pagx_path": {"type": "string"}},
            "required": ["pagx_path"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_layout_text",
        "description": "Set a named text element in a layout in the current ArcGIS Pro project.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "element_name": {"type": "string"},
                "text": {"type": "string"},
            },
            "required": ["layout_name", "element_name", "text"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_apply_simple_symbology",
        "description": "Apply a simple preset renderer to a named feature layer in the current ArcGIS Pro map.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer_name": {"type": "string"},
                "preset": {
                    "type": "string",
                    "enum": ["boundary", "districts", "roads", "rivers", "points", "buffer", "impact"]
                },
                "map_name": {"type": ["string", "null"]}
            },
            "required": ["layer_name", "preset"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_manage_layers",
        "description": "Set visibility, transparency, scale thresholds, or definition query for named layers or all layers in the current map. This does not alter source data.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_names": {"type": "array", "items": {"type": "string"}},
                "except_layer_names": {"type": "array", "items": {"type": "string"}},
                "all": {"type": "boolean"},
                "visible": {"type": "boolean"},
                "transparency": {"type": "number", "minimum": 0, "maximum": 100},
                "min_scale": {"type": "number", "minimum": 0},
                "max_scale": {"type": "number", "minimum": 0},
                "definition_query": {"type": "string"}
                ,"name": {"type": "string"}
                ,"blend_mode": {"type": "string"}
            },
            "additionalProperties": False
        }
    },
    {
        "name": "live_remove_layers_from_map",
        "description": "Remove named layers from the current map only. Source datasets are never deleted.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_names": {"type": "array", "items": {"type": "string"}, "minItems": 1}
            },
            "required": ["layer_names"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_reorder_layer",
        "description": "Move a layer to the top, bottom, or a specified root map index.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "position": {"type": "string", "enum": ["top", "bottom", "index"]},
                "index": {"type": "integer", "minimum": 0}
            },
            "required": ["layer_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_zoom_to_layer",
        "description": "Zoom the active ArcGIS Pro map view to a named layer with optional padding.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "scale_factor": {"type": "number", "minimum": 1, "maximum": 10, "default": 1.08}
            },
            "required": ["layer_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_apply_vector_symbology",
        "description": "Apply native simple, unique-value, graduated-color, graduated-symbol, proportional, unclassed-color, heat-map, dot-density, pie, bar, or stacked-chart symbology to a feature layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "renderer": {"type": "string", "enum": [
                    "simple", "unique", "unique_value",
                    "graduated", "graduated_colors", "class_breaks",
                    "graduated_symbol", "graduated_symbols",
                    "proportional", "proportional_symbols",
                    "unclassed", "unclassed_colors",
                    "heatmap", "heat_map", "dot_density",
                    "pie", "pie_chart", "bar", "bar_chart",
                    "stacked", "stacked_chart"
                ]},
                "field": {"type": ["string", "null"]},
                "fields": {"type": "array", "items": {"type": "string"}},
                "chart_fields": {"type": "array", "items": {"type": "string"}},
                "value_fields": {"type": "array", "items": {"type": "string"}},
                "arcade_expressions": {"type": "array", "items": {"type": "string"}},
                "classification": {"type": "string"},
                "class_count": {"type": "integer", "minimum": 2, "maximum": 32},
                "values_limit": {"type": "integer", "minimum": 0},
                "color_stops": {"type": "array", "items": {}},
                "symbol": {"type": "object", "additionalProperties": True},
                "background_symbol": {"type": "object", "additionalProperties": True},
                "normalization_field": {"type": ["string", "null"]},
                "normalization_type": {"type": "string", "enum": ["nothing", "field", "log", "percent_of_total"]},
                "exclusion_clause": {"type": ["string", "null"]},
                "minimum_size": {"type": "number", "exclusiveMinimum": 0},
                "maximum_size": {"type": "number", "exclusiveMinimum": 0},
                "lower_size_stop": {"type": "number"},
                "upper_size_stop": {"type": "number"},
                "lower_color_stop": {"type": "number"},
                "upper_color_stop": {"type": "number"},
                "lower_label": {"type": "string"},
                "upper_label": {"type": "string"},
                "upper_bounded": {"type": "boolean"},
                "flannery_compensation": {"type": "boolean"},
                "legend_symbol_count": {"type": "integer", "minimum": 1, "maximum": 10},
                "show_null_values": {"type": "boolean"},
                "weight_field": {"type": ["string", "null"]},
                "radius": {"type": "integer", "minimum": 1, "maximum": 200},
                "rendering_quality": {"type": "integer", "minimum": 1, "maximum": 3},
                "dot_size": {"type": "number", "exclusiveMinimum": 0},
                "dot_value": {"type": "number", "exclusiveMinimum": 0},
                "symbol_label": {"type": "string"},
                "unit_label": {"type": "string"},
                "orientation": {"type": "string"},
                "size_option": {"type": "string"},
                "field_name": {"type": ["string", "null"]},
                "fixed_size": {"type": "number", "exclusiveMinimum": 0},
                "show_outline": {"type": "boolean"},
                "display_in_3d": {"type": "boolean"},
                "show_axes": {"type": "boolean"},
                "bar_spacing": {"type": "number", "minimum": 0},
                "bar_width": {"type": "number", "exclusiveMinimum": 0},
                "maximum_bar_length": {"type": "number", "exclusiveMinimum": 0},
                "stack_length": {"type": "number", "exclusiveMinimum": 0},
                "stack_width": {"type": "number", "exclusiveMinimum": 0},
                "transparency": {"type": "number", "minimum": 0, "maximum": 100},
                "label": {"type": "string"},
                "description": {"type": "string"}
            },
            "required": ["layer_name", "renderer"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_configure_visual_variables",
        "description": "Inspect, replace, append, remove, or clear size, color, transparency, and rotation visual variables on a supported feature renderer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "operation": {"type": "string", "enum": ["replace", "append", "remove", "clear"], "default": "replace"},
                "remove_types": {
                    "type": "array",
                    "items": {"type": "string", "enum": ["size", "color", "transparency", "rotation"]},
                    "uniqueItems": True
                },
                "variables": {
                    "type": "array",
                    "maxItems": 4,
                    "items": {
                        "type": "object",
                        "properties": {
                            "type": {"type": "string", "enum": ["size", "color", "transparency", "rotation"]},
                            "field": {"type": ["string", "null"]},
                            "expression": {"type": ["string", "null"]},
                            "heading": {"type": "string"},
                            "show_legend": {"type": "boolean"},
                            "histogram_bins": {"type": "integer", "minimum": 2, "maximum": 256},
                            "normalization_field": {"type": ["string", "null"]},
                            "normalization_type": {"type": "string", "enum": ["nothing", "field", "log", "percent_of_total"]},
                            "variable_type": {"type": "string", "enum": ["proportional", "graduated", "expression", "random", "none"]},
                            "min_value": {"type": "number"},
                            "max_value": {"type": "number"},
                            "min_size": {"type": "number", "exclusiveMinimum": 0},
                            "max_size": {"type": "number", "exclusiveMinimum": 0},
                            "data_values": {"type": "array", "items": {"type": "number"}},
                            "size_values": {"type": "array", "items": {"type": "number"}},
                            "transparency_values": {"type": "array", "items": {"type": "number", "minimum": 0, "maximum": 100}},
                            "color_stops": {"type": "array", "items": {}},
                            "polygon_color_target": {"type": "string", "enum": ["fill", "outline", "fill_outline"]},
                            "rotation_type": {"type": "string", "enum": ["geographic", "arithmetic"]},
                            "normal_to_surface": {"type": "boolean"}
                        },
                        "required": ["type"],
                        "additionalProperties": False
                    }
                }
            },
            "required": ["layer_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_apply_raster_symbology",
        "description": "Apply native stretch, classified, discrete, shaded-relief, RGB, unique-value, colormap, or vector-field colorization to a raster layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "colorizer": {"type": "string", "enum": [
                    "stretch", "classify", "classified", "discrete",
                    "shaded_relief", "relief", "rgb", "rgb_composite",
                    "unique", "unique_value", "colormap", "color_map",
                    "vector_field", "vector"
                ]},
                "field": {"type": "string"},
                "band_index": {"type": "integer", "minimum": 0},
                "red_band_index": {"type": "integer", "minimum": 0},
                "green_band_index": {"type": "integer", "minimum": 0},
                "blue_band_index": {"type": "integer", "minimum": 0},
                "magnitude_band_index": {"type": "integer", "minimum": 0},
                "direction_band_index": {"type": "integer", "minimum": 0},
                "is_uv_components": {"type": "boolean"},
                "symbolization_type": {"type": "string"},
                "stretch_type": {"type": "string"},
                "classification": {"type": "string"},
                "class_count": {"type": "integer", "minimum": 2, "maximum": 32},
                "color_count": {"type": "integer", "minimum": 2, "maximum": 64},
                "color_stops": {"type": "array", "items": {}},
                "colormap_file": {"type": ["string", "null"]},
                "colormap_entries": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": 4096,
                    "items": {
                        "type": "object",
                        "properties": {
                            "value": {"type": "integer"},
                            "color": {},
                            "label": {"type": "string"}
                        },
                        "required": ["value", "color"],
                        "additionalProperties": False
                    }
                },
                "class_labels": {"type": "array", "items": {"type": "string"}},
                "nodata_color": {},
                "background_color": {},
                "display_background_value": {"type": "boolean"},
                "background_value": {"type": "number"},
                "background_value_red": {"type": "number"},
                "background_value_green": {"type": "number"},
                "background_value_blue": {"type": "number"},
                "minimum": {"type": "number"},
                "maximum": {"type": "number"},
                "minimums": {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
                "maximums": {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3},
                "min_percent": {"type": "number"},
                "max_percent": {"type": "number"},
                "gamma": {"type": "number"},
                "gamma_r": {"type": "number", "exclusiveMinimum": 0},
                "gamma_g": {"type": "number", "exclusiveMinimum": 0},
                "gamma_b": {"type": "number", "exclusiveMinimum": 0},
                "standard_deviations": {"type": "number", "exclusiveMinimum": 0},
                "z_factor": {"type": "number"},
                "altitude": {"type": "number"},
                "azimuth": {"type": "number"},
                "hillshade_type": {"type": "string", "enum": ["traditional", "multidirectional"]},
                "scaling_type": {"type": "string", "enum": ["none", "adjusted"]},
                "remove_edge_effect": {"type": "boolean"},
                "use_map_illumination": {"type": "boolean"},
                "pixel_size_factor": {"type": "number"},
                "pixel_size_power": {"type": "number"},
                "resampling_type": {"type": "string"},
                "brightness": {"type": "integer", "minimum": -100, "maximum": 100},
                "contrast": {"type": "integer", "minimum": -100, "maximum": 100},
                "heading": {"type": "string"},
                "transparency": {"type": "number", "minimum": 0, "maximum": 100}
            },
            "required": ["layer_name", "colorizer"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_configure_raster_rendering_rule",
        "description": "Inspect or remove a raster rendering rule, or replace it from a complete previously inspected CIMRenderingRule JSON. Ad-hoc function-name construction is rejected because incomplete native rules can destabilize ArcGIS Pro.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "operation": {"type": "string", "enum": ["inspect", "set", "remove"], "default": "inspect"},
                "cim_json": {"type": ["string", "null"], "maxLength": 1048576},
                "confirm_replace_definition": {"type": "boolean", "default": False}
            },
            "required": ["layer_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_configure_multidimensional_raster",
        "description": "Inspect, set, or clear the project-only active variable and dimensional slice of a multidimensional raster layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "operation": {"type": "string", "enum": ["inspect", "set", "clear"], "default": "inspect"},
                "variable_name": {"type": ["string", "null"]},
                "range_dimension_name": {"type": ["string", "null"]},
                "range_min": {"type": "number"},
                "range_max": {"type": "number"},
                "time_start": {"type": ["string", "null"], "format": "date-time"},
                "time_end": {"type": ["string", "null"], "format": "date-time"},
                "additional_dimensions": {
                    "type": "array",
                    "maxItems": 32,
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "min": {"type": "number"},
                            "max": {"type": "number"}
                        },
                        "required": ["name", "min", "max"],
                        "additionalProperties": False
                    }
                }
            },
            "required": ["layer_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_create_terrain_stack",
        "description": "Create a non-destructive terrain stack in the open map: an elevation-tint DEM plus a derived physical hillshade using Multiply blending.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "dem_layer_name": {"type": "string"},
                "hillshade_output": {"type": "string"},
                "hillshade_layer_name": {"type": "string", "default": "DEM 山体阴影"},
                "color_stops": {"type": "array", "items": {}, "minItems": 2},
                "minimum": {"type": "number"},
                "maximum": {"type": "number"},
                "stretch_type": {"type": "string", "default": "minimum_maximum"},
                "gamma": {"type": "number", "default": 1.0},
                "azimuth": {"type": "number", "default": 315},
                "altitude": {"type": "number", "default": 45},
                "z_factor": {"type": "number", "default": 1.0},
                "model_shadows": {"type": "boolean", "default": False},
                "hillshade_transparency": {"type": "number", "minimum": 0, "maximum": 100, "default": 35},
                "hillshade_blend_mode": {"type": "string", "default": "Multiply"},
                "overwrite_output": {"type": "boolean", "default": False},
            },
            "required": ["dem_layer_name", "hillshade_output", "color_stops", "minimum", "maximum"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_elevation_surfaces",
        "description": "List ground and custom elevation surfaces in a map or scene, optionally including their complete CIM definitions.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "include_cim": {"type": "boolean", "default": False},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_elevation_surface",
        "description": "Configure a ground or custom elevation surface's visibility, shading, transparency, color, and vertical exaggeration.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "surface_name": {"type": ["string", "null"]},
                "visible": {"type": "boolean"},
                "transparency": {"type": "number", "minimum": 0, "maximum": 100},
                "vertical_exaggeration": {"type": "number", "minimum": 0, "maximum": 10000},
                "enable_surface_shading": {"type": "boolean"},
                "tin_shading_mode": {"type": "string"},
                "navigate_below_ground": {"type": "boolean"},
                "color": {},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_layer_elevation",
        "description": "Inspect or configure a layer's on-ground, relative, absolute, or custom-surface elevation behavior without editing source geometry.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "operation": {"type": "string", "enum": ["inspect", "set"], "default": "set"},
                "elevation_type": {"type": "string", "enum": ["None", "OnGround", "OnCustomSurface", "RelativeToGround", "RelativeToCustomSurface", "AtAbsoluteHeight", "RelativeToScene"]},
                "custom_surface_name": {"type": ["string", "null"]},
                "vertical_exaggeration": {"type": "number", "minimum": 0},
                "cartographic_offset": {"type": "number"},
                "offset_unit": {"type": "string", "enum": ["meters", "kilometers", "feet", "miles"]},
                "numeric_field_name": {"type": ["string", "null"]},
                "feature_elevation_expression": {"type": ["string", "null"]},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_sample_elevation_surface",
        "description": "Sample Z values from a named elevation surface at bounded map coordinates without changing any dataset.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "surface_name": {"type": ["string", "null"]},
                "points": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {"x": {"type": "number"}, "y": {"type": "number"}},
                        "required": ["x", "y"],
                        "additionalProperties": False,
                    },
                    "minItems": 1,
                    "maxItems": 10000,
                },
            },
            "required": ["points"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_elevation_profile",
        "description": "Sample an elevation profile between two map coordinates from a ground or custom elevation surface.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "surface_name": {"type": ["string", "null"]},
                "start": {"type": "object", "properties": {"x": {"type": "number"}, "y": {"type": "number"}}, "required": ["x", "y"], "additionalProperties": False},
                "end": {"type": "object", "properties": {"x": {"type": "number"}, "y": {"type": "number"}}, "required": ["x", "y"], "additionalProperties": False},
                "num_points": {"type": "integer", "minimum": 2, "maximum": 10000, "default": 100},
            },
            "required": ["start", "end"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_animation_state",
        "description": "Inspect animation names, tracks, keyframes, duration, frame position, and export state for the active map view.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_control_animation",
        "description": "Play, stop, step through, duration-scale, or stop exporting the active map animation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "operation": {"type": "string", "enum": ["play", "stop", "next_keyframe", "previous_keyframe", "scale_duration", "stop_export"]},
                "factor": {"type": "number", "exclusiveMinimum": 0},
            },
            "required": ["operation"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_begin_animation_export",
        "description": "Begin exporting a named map animation to MP4, AVI, or GIF under the configured output root; monitor with live_get_animation_state.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "animation_name": {"type": ["string", "null"]},
                "output_file": {"type": "string"},
                "width": {"type": "integer", "minimum": 64, "maximum": 16384, "default": 1920},
                "height": {"type": "integer", "minimum": 64, "maximum": 16384, "default": 1080},
                "frame_rate": {"type": "number", "minimum": 1, "maximum": 240, "default": 30},
                "quality": {"type": "number", "minimum": 1, "maximum": 100, "default": 75},
                "start_frame": {"type": "integer", "minimum": 0, "default": 0},
                "end_frame": {"type": "integer", "minimum": 0},
            },
            "required": ["output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_labels",
        "description": "Enable or disable labels and configure field/expression, filter, and text symbol for a feature layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "visible": {"type": "boolean"},
                "field": {"type": ["string", "null"]},
                "expression": {"type": ["string", "null"]},
                "where_clause": {"type": ["string", "null"]},
                "text_symbol": {"type": "object", "additionalProperties": True}
            },
            "required": ["layer_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_list_label_classes",
        "description": "List every native label class on a feature layer, including expression, filter, priority, scales, and placement engine.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "include_cim": {"type": "boolean", "default": False},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_label_class",
        "description": "Create or configure one native label class, including placement style, priorities, scales, filters, and Standard or Maplex placement properties.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "label_class_name": {"type": "string"},
                "create_if_missing": {"type": "boolean", "default": True},
                "new_name": {"type": ["string", "null"]},
                "layer_labels_visible": {"type": "boolean"},
                "visible": {"type": "boolean"},
                "field": {"type": ["string", "null"]},
                "expression": {"type": ["string", "null"]},
                "expression_engine": {"type": "string", "enum": ["Arcade", "Python", "VBScript", "JScript"]},
                "where_clause": {"type": ["string", "null"]},
                "priority": {"type": "integer", "minimum": 0, "maximum": 1000},
                "minimum_scale": {"type": "number", "minimum": 0},
                "maximum_scale": {"type": "number", "minimum": 0},
                "use_coded_value": {"type": "boolean"},
                "text_symbol": {"type": "object", "additionalProperties": True},
                "style_name": {"type": ["string", "null"]},
                "placement_engine": {"type": "string", "enum": ["standard", "maplex"]},
                "placement_style_item_name": {"type": ["string", "null"]},
                "placement_style_search": {"type": ["string", "null"]},
                "standard_placement": {"type": "object", "additionalProperties": True},
                "maplex_placement": {"type": "object", "additionalProperties": True},
            },
            "required": ["layer_name", "label_class_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_label_classes",
        "description": "Remove selected project label-class definitions after explicit confirmation; source features are not changed.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "label_class_names": {"type": "array", "items": {"type": "string"}, "minItems": 1, "maxItems": 1000},
                "confirm_remove": {"type": "boolean", "const": True},
            },
            "required": ["layer_name", "label_class_names", "confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_popup",
        "description": "Enable/disable popups, set a display field, or import/export ArcGIS popup configuration for a feature or raster layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "show_popups": {"type": "boolean"},
                "display_field": {"type": ["string", "null"]},
                "import_popup_path": {"type": ["string", "null"]},
                "export_popup_path": {"type": ["string", "null"]},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_fields",
        "description": "Configure field aliases, visibility, highlighting, and read-only presentation for a feature layer or standalone table.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": ["string", "null"]},
                "table_name": {"type": ["string", "null"]},
                "fields": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "alias": {"type": ["string", "null"]},
                            "visible": {"type": "boolean"},
                            "highlighted": {"type": "boolean"},
                            "read_only": {"type": "boolean"},
                        },
                        "required": ["name"],
                        "additionalProperties": False,
                    },
                    "minItems": 1,
                },
            },
            "required": ["fields"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_display_filters",
        "description": "Configure native ArcGIS Pro display filters for a feature layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "enabled": {"type": "boolean"},
                "active_filter_name": {"type": ["string", "null"]},
                "filter_type": {"type": ["string", "null"]},
                "filters": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "where_clause": {"type": "string"},
                            "min_scale": {"type": "number", "minimum": 0},
                            "max_scale": {"type": "number", "minimum": 0},
                        },
                        "required": ["name", "where_clause"],
                        "additionalProperties": False,
                    },
                },
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_time",
        "description": "Configure or clear native time properties on a feature layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "clear": {"type": "boolean"},
                "start_time_field": {"type": ["string", "null"]},
                "end_time_field": {"type": ["string", "null"]},
                "cumulative": {"type": "boolean"},
                "live_feed": {"type": "boolean"},
                "time_format": {"type": ["string", "null"]},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_charts",
        "description": "Read native CIM chart definitions attached to a feature layer.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}, "layer_name": {"type": "string"}},
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_charts",
        "description": "Replace native layer chart definitions using ArcGIS CIM chart JSON, covering the full chart type surface.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "charts": {"type": "array", "items": {"type": "object", "additionalProperties": True}},
            },
            "required": ["layer_name", "charts"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_draw_graphics",
        "description": "Create editable point, line, polygon, rectangle, or text graphics in a graphics layer in the current map.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "graphics_layer_name": {"type": "string"},
                "clear_existing": {"type": "boolean"},
                "graphics": {"type": "array", "items": {"type": "object", "additionalProperties": True}, "minItems": 1}
            },
            "required": ["graphics"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_list_map_graphics",
        "description": "List graphics layers and editable map graphic elements, including transforms, bounds, selection settings, and optional CIM JSON.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "graphics_layer_name": {"type": ["string", "null"]},
                "include_cim": {"type": "boolean", "default": False},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_map_graphics",
        "description": "Update, reorder, select, unselect, remove, or clear editable map graphics without changing source datasets.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "graphics_layer_name": {"type": "string", "default": "Codex Graphics"},
                "operation": {"type": "string", "enum": ["update", "remove", "clear", "move", "select", "unselect"]},
                "element_name": {"type": ["string", "null"]},
                "element_names": {"type": "array", "items": {"type": "string"}, "maxItems": 10000},
                "new_name": {"type": ["string", "null"]},
                "visible": {"type": "boolean"},
                "locked": {"type": "boolean"},
                "locked_aspect_ratio": {"type": "boolean"},
                "x": {"type": "number"},
                "y": {"type": "number"},
                "width": {"type": "number", "minimum": 0},
                "height": {"type": "number", "minimum": 0},
                "rotation": {"type": "number"},
                "anchor": {"type": "string"},
                "index": {"type": "integer", "minimum": 0},
                "clear_selection": {"type": "boolean", "default": False},
                "confirm_remove": {"type": "boolean", "default": False},
                "confirm_clear": {"type": "boolean", "default": False},
            },
            "required": ["operation"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_map_graphic_cim",
        "description": "Read a named map graphic element's complete native CIM definition.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "graphics_layer_name": {"type": "string", "default": "Codex Graphics"},
                "element_name": {"type": "string"},
            },
            "required": ["element_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_map_graphic_cim",
        "description": "Replace a map graphic's same-type CIM definition while preserving its name and structural links by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "graphics_layer_name": {"type": "string", "default": "Codex Graphics"},
                "element_name": {"type": "string"},
                "cim_json": {"type": "string"},
                "preserve_name": {"type": "boolean", "default": True},
                "preserve_structural_links": {"type": "boolean", "default": True},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["element_name", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_create_layout",
        "description": "Create and optionally open a parameterized editable layout with map frame, title, legend, north arrow, scale bar, external coordinate ticks, and color bar.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_name": {"type": ["string", "null"]},
                "page_width": {"type": "number"},
                "page_height": {"type": "number"},
                "title": {"type": ["string", "null"]},
                "title_x": {"type": "number"},
                "title_y": {"type": "number"},
                "title_symbol": {"type": "object", "additionalProperties": True},
                "map_frame_name": {"type": "string"},
                "map_frame_bounds": {"type": "object", "additionalProperties": True},
                "map_frame_border": {"type": "boolean"},
                "map_frame_border_color": {},
                "map_frame_border_width": {"type": "number"},
                "extent_layer_name": {"type": ["string", "null"]},
                "scale_factor": {"type": "number"},
                "legend": {"type": ["object", "boolean"], "additionalProperties": True},
                "north_arrow": {"type": ["object", "boolean"], "additionalProperties": True},
                "scale_bar": {"type": ["object", "boolean"], "additionalProperties": True},
                "ticks": {"type": "object", "additionalProperties": True},
                "colorbar": {"type": "object", "additionalProperties": True},
                "open_layout": {"type": "boolean", "default": True}
            },
            "required": ["layout_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_duplicate_layout",
        "description": "Duplicate an editable layout without changing its source maps or datasets.",
        "inputSchema": {
            "type": "object",
            "properties": {"layout_name": {"type": "string"}, "new_name": {"type": "string"}},
            "required": ["layout_name", "new_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_rename_layout",
        "description": "Rename an existing layout project item.",
        "inputSchema": {
            "type": "object",
            "properties": {"layout_name": {"type": "string"}, "new_name": {"type": "string"}},
            "required": ["layout_name", "new_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_layout",
        "description": "Remove a layout project item only after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {"layout_name": {"type": "string"}, "confirm_remove": {"type": "boolean", "const": True}},
            "required": ["layout_name", "confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_open_layout",
        "description": "Open a named layout in the ArcGIS Pro interface.",
        "inputSchema": {
            "type": "object",
            "properties": {"layout_name": {"type": "string"}},
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_layout_page",
        "description": "Configure native layout page size, units, rulers, guides and resizing behavior.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "width": {"type": "number", "exclusiveMinimum": 0},
                "height": {"type": "number", "exclusiveMinimum": 0},
                "units": {"type": "string", "enum": ["centimeters", "millimeters", "inches", "points"]},
                "show_guides": {"type": "boolean"},
                "show_margin": {"type": "boolean"},
                "show_rulers": {"type": "boolean"},
                "smallest_ruler_division": {"type": "number", "exclusiveMinimum": 0},
                "stretch_elements": {"type": "boolean"},
                "resize_elements": {"type": "boolean"},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_layout_guides",
        "description": "Inspect, add, replace, remove, or clear native horizontal/vertical ArcGIS Pro layout guides without saving the project.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "operation": {"type": "string", "enum": ["inspect", "add", "replace", "remove", "clear"], "default": "inspect"},
                "guides": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "orientation": {"type": "string", "enum": ["Horizontal", "Vertical"]},
                            "position": {"type": "number"},
                        },
                        "required": ["orientation", "position"],
                        "additionalProperties": False,
                    },
                    "maxItems": 500,
                },
                "guide_indices": {"type": "array", "items": {"type": "integer", "minimum": 0}, "maxItems": 500},
                "confirm_clear": {"type": "boolean", "default": False},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_save_layout_file",
        "description": "Save a layout as a reusable native .pagx layout file under the output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "output_file": {"type": "string"},
                "overwrite": {"type": "boolean", "default": False},
            },
            "required": ["layout_name", "output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_update_layout_element",
        "description": "Move, resize, rotate, show/hide, or explicitly delete a named layout element.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "element_name": {"type": "string"},
                "x": {"type": "number"},
                "y": {"type": "number"},
                "width": {"type": "number"},
                "height": {"type": "number"},
                "rotation": {"type": "number"},
                "visible": {"type": "boolean"},
                "delete": {"type": "boolean", "default": False}
            },
            "required": ["layout_name", "element_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_organize_layout_elements",
        "description": "Group, ungroup, or change the drawing order of named layout elements.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "element_names": {"type": "array", "items": {"type": "string"}, "minItems": 1},
                "operation": {"type": "string", "enum": ["group", "ungroup", "bring_to_front", "send_to_back", "bring_forward", "send_backward"]},
                "group_name": {"type": ["string", "null"]},
            },
            "required": ["layout_name", "element_names", "operation"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_layout_picture",
        "description": "Add a local image as an editable picture element in a layout.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "picture_path": {"type": "string"},
                "element_name": {"type": "string"},
                "bounds": {"type": "object", "additionalProperties": True},
            },
            "required": ["layout_name", "picture_path"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_map_frame",
        "description": "Add a second or subsequent editable map frame to an existing ArcGIS Pro layout.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_name": {"type": "string"},
                "map_frame_name": {"type": "string"},
                "bounds": {"type": "object", "additionalProperties": True},
                "extent_layer_name": {"type": ["string", "null"]},
                "scale_factor": {"type": "number", "minimum": 1.0, "maximum": 10.0},
                "map_frame_border": {"type": "boolean"},
                "map_frame_border_color": {},
                "map_frame_border_width": {"type": "number"},
            },
            "required": ["layout_name", "map_name", "map_frame_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_dynamic_frames",
        "description": "List native table frames and chart frames in a layout, including their map-member bindings.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "include_cim": {"type": "boolean", "default": False},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_table_frame",
        "description": "Add a native dynamic table frame linked to a layer or standalone table in a layout map frame.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "element_name": {"type": ["string", "null"]},
                "bounds": {"type": "object", "additionalProperties": True},
                "field_names": {"type": "array", "items": {"type": "string"}, "maxItems": 500},
                "style_name": {"type": ["string", "null"]},
                "style_item_name": {"type": ["string", "null"]},
                "style_search": {"type": ["string", "null"]},
                "show_headings": {"type": "boolean"},
                "row_limit": {"type": "integer", "minimum": 1, "maximum": 100000},
                "columns": {"type": "integer", "minimum": 1, "maximum": 100},
                "balance_columns": {"type": "boolean"},
                "where_clause": {"type": ["string", "null"]},
                "filling_strategy": {"type": "string", "enum": ["ShowAllRows", "ShowVisibleRows", "ShowMapSeriesRows", "CustomWhereClause"]},
                "fitting_strategy": {"type": "string", "enum": ["AdjustSize", "AdjustColumns", "AdjustColumnsAndSize", "AdjustFrame"]},
            },
            "required": ["layout_name", "map_member_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_chart_frame",
        "description": "Add a native dynamic chart frame linked to an existing chart on a map member.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "chart_name": {"type": "string"},
                "element_name": {"type": ["string", "null"]},
                "bounds": {"type": "object", "additionalProperties": True},
            },
            "required": ["layout_name", "map_member_name", "chart_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_map_frame",
        "description": "Change a layout map frame's linked map, camera source, rotation, aspect lock, and border.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": "string"},
                "map_name": {"type": ["string", "null"]},
                "extent_layer_name": {"type": ["string", "null"]},
                "bookmark_name": {"type": ["string", "null"]},
                "use_selected_features": {"type": "boolean"},
                "scale_factor": {"type": "number", "minimum": 1.0, "maximum": 10.0},
                "rotation": {"type": "number"},
                "locked_aspect_ratio": {"type": "boolean"},
                "map_frame_border": {"type": "boolean"},
                "map_frame_border_color": {},
                "map_frame_border_width": {"type": "number"},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_extent_indicators",
        "description": "List native extent indicators on a layout map frame and resolve their source map frames.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "include_cim": {"type": "boolean", "default": False},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_extent_indicator",
        "description": "Add a native editable extent indicator showing one layout map frame inside another.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"], "description": "Target overview frame that will display the indicator."},
                "source_map_frame_name": {"type": "string", "description": "Source/detail frame whose current extent is indicated."},
                "indicator_name": {"type": "string"},
                "visible": {"type": "boolean"},
                "indicator_type": {"type": "string", "enum": ["Frame", "Rectangle", "IndexFeatureFromDDP"]},
                "symbolize_exterior": {"type": "boolean"},
                "avoid_label_conflict": {"type": "boolean"},
                "collapse_size": {"type": "number", "minimum": 0},
                "leader_type": {"type": "string", "enum": ["None", "LineToNearestPoint", "LineToMidpoint", "LineThroughCenters", "CalloutToCenter", "CalloutToEdge", "CalloutToEdges"]},
                "frame_color": {},
                "fill_color": {},
                "frame_width": {"type": "number", "minimum": 0},
                "point_color": {},
                "point_size": {"type": "number", "exclusiveMinimum": 0},
                "leader_color": {},
                "leader_width": {"type": "number", "minimum": 0},
            },
            "required": ["layout_name", "source_map_frame_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_extent_indicator",
        "description": "Relink, rename, restyle, show/hide, or configure a native extent indicator.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "indicator_name": {"type": "string"},
                "new_name": {"type": ["string", "null"]},
                "source_map_frame_name": {"type": ["string", "null"]},
                "visible": {"type": "boolean"},
                "indicator_type": {"type": "string", "enum": ["Frame", "Rectangle", "IndexFeatureFromDDP"]},
                "symbolize_exterior": {"type": "boolean"},
                "avoid_label_conflict": {"type": "boolean"},
                "collapse_size": {"type": "number", "minimum": 0},
                "leader_type": {"type": "string", "enum": ["None", "LineToNearestPoint", "LineToMidpoint", "LineThroughCenters", "CalloutToCenter", "CalloutToEdge", "CalloutToEdges"]},
                "frame_color": {},
                "fill_color": {},
                "frame_width": {"type": "number", "minimum": 0},
                "point_color": {},
                "point_size": {"type": "number", "exclusiveMinimum": 0},
                "leader_color": {},
                "leader_width": {"type": "number", "minimum": 0},
            },
            "required": ["layout_name", "indicator_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_extent_indicators",
        "description": "Remove selected native extent indicators after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "indicator_names": {"type": "array", "items": {"type": "string"}, "minItems": 1, "maxItems": 100},
                "confirm_remove": {"type": "boolean", "const": True},
            },
            "required": ["layout_name", "indicator_names", "confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_map_clipping",
        "description": "Configure native map clipping by map extent, custom polygon layer, or map series; optionally exclude layers and draw the clipping border.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "mode": {"type": "string", "enum": ["None", "MapExtent", "CustomShape", "MapSeries"]},
                "clipping_layer_name": {"type": ["string", "null"]},
                "excluded_layer_names": {"type": "array", "items": {"type": "string"}, "maxItems": 5000},
                "clip_to_full_extent": {"type": "boolean"},
                "border_visible": {"type": "boolean"},
                "border_color": {},
                "border_width": {"type": "number", "minimum": 0},
                "clear_custom_shape": {"type": "boolean"},
                "clear_exclusions": {"type": "boolean"},
            },
            "required": ["mode"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_dynamic_text",
        "description": "Add an editable native ArcGIS layout text element whose text may contain dynamic-text tags such as <dyn type=\"date\" format=\"yyyy-MM-dd\"/>.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "element_name": {"type": "string"},
                "text": {"type": "string", "description": "Static text, dynamic <dyn .../> markup, or a mixture of both."},
                "x": {"type": "number"},
                "y": {"type": "number"},
                "rotation": {"type": "number"},
                "visible": {"type": "boolean"},
                "locked": {"type": "boolean"},
                "text_symbol": {"type": "object", "additionalProperties": True},
            },
            "required": ["layout_name", "text"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_create_map_series",
        "description": "Create a spatial, bookmark, or thematic map series for an editable layout.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": "string"},
                "series_type": {"type": "string", "enum": ["spatial", "bookmark", "thematic"]},
                "index_layer_name": {"type": ["string", "null"]},
                "name_field": {"type": ["string", "null"]},
                "enabled": {"type": "boolean", "default": True},
                "current_page_number": {"type": ["string", "null"]},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_map_series",
        "description": "List native map-series state for one or every layout, including typed spatial-series fields and page navigation state.",
        "inputSchema": {
            "type": "object",
            "properties": {"layout_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_map_series",
        "description": "Configure an existing spatial, bookmark, or thematic map series and its current page; spatial series support index, sort, extent, margin, scale, rotation and category fields.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "enabled": {"type": "boolean"},
                "starting_page_number": {"type": "integer", "minimum": 1},
                "current_page_number": {"type": ["string", "null"]},
                "index_layer_name": {"type": ["string", "null"]},
                "page_name_field": {"type": ["string", "null"]},
                "page_number_field": {"type": ["string", "null"]},
                "sort_field": {"type": ["string", "null"]},
                "sort_ascending": {"type": "boolean"},
                "scale_field": {"type": ["string", "null"]},
                "rotation_field": {"type": ["string", "null"]},
                "spatial_reference_field": {"type": ["string", "null"]},
                "category_field": {"type": ["string", "null"]},
                "extent_options": {"type": "string", "enum": ["BestFit", "ExtentCenter", "DataDriven"]},
                "margin": {"type": "number", "minimum": 0},
                "margin_type": {"type": "string", "enum": ["Percent", "MapUnits", "PageUnits"]},
                "margin_units": {"type": "string"},
                "scale_rounding": {"type": "number", "minimum": 0},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_map_series",
        "description": "Remove a layout map-series definition after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "confirm_remove": {"type": "boolean", "const": True},
            },
            "required": ["layout_name", "confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_export_map_series",
        "description": "Export all, current, selected, or custom map-series pages to PDF or supported image/vector formats under the output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "output_file": {"type": "string"},
                "dpi": {"type": "integer", "minimum": 72, "maximum": 1200},
                "export_pages": {"type": "string", "enum": ["All", "Current", "SelectedIndexFeatures", "Custom", "SelectedPages"]},
                "custom_pages": {"type": ["string", "null"]},
                "file_options": {"type": "string", "enum": ["ExportAsSinglePDF", "ExportMultipleNames", "ExportMultipleNumbers"]},
                "show_selected_symbology": {"type": "boolean"},
                "order_pages_by_grouping": {"type": "boolean"},
                "embed_fonts": {"type": "boolean"},
                "georeference": {"type": "boolean"},
                "transparent_background": {"type": "boolean"},
                "world_file": {"type": "boolean"},
                "geotiff_tags": {"type": "boolean"},
                "image_quality": {"type": "integer", "minimum": 1, "maximum": 100},
                "show_selection_symbology": {"type": "boolean"},
            },
            "required": ["layout_name", "output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_arrange_layout_elements",
        "description": "Align, distribute, or make selected editable layout elements the same size using their actual page geometry.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "element_names": {"type": "array", "items": {"type": "string"}, "minItems": 2, "maxItems": 1000},
                "reference_element_name": {"type": ["string", "null"]},
                "operation": {
                    "type": "string",
                    "enum": ["align_left", "align_horizontal_center", "align_right", "align_bottom", "align_vertical_center", "align_top", "distribute_horizontal", "distribute_vertical", "same_width", "same_height", "same_size"]
                },
            },
            "required": ["layout_name", "element_names", "operation"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_export_map_view",
        "description": "Export the active ArcGIS Pro map view directly to PDF, PNG, JPEG, TIFF, SVG, SVGZ, EPS, or AIX under the output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "output_file": {"type": "string"},
                "dpi": {"type": "integer", "minimum": 72, "maximum": 1200},
                "width": {"type": "number", "exclusiveMinimum": 0},
                "height": {"type": "number", "exclusiveMinimum": 0},
                "embed_fonts": {"type": "boolean"},
                "georeference": {"type": "boolean"},
                "transparent_background": {"type": "boolean"},
                "world_file": {"type": "boolean"},
                "geotiff_tags": {"type": "boolean"},
                "image_quality": {"type": "integer", "minimum": 1, "maximum": 100},
                "show_selection_symbology": {"type": "boolean"},
                "full_rasterization": {"type": "boolean"},
                "convert_character_markers": {"type": "boolean"},
                "compress_vector_graphics": {"type": "boolean"},
                "include_nonvisible_map_layers": {"type": "boolean"},
            },
            "required": ["output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_layout_cim",
        "description": "Read the full native ArcGIS CIM definition for a layout.",
        "inputSchema": {
            "type": "object",
            "properties": {"layout_name": {"type": "string"}},
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_layout_cim",
        "description": "Replace a layout's full native CIM definition only after explicit confirmation, covering long-tail layout properties.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "cim_json": {"type": "string"},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["layout_name", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_layout_element_cim",
        "description": "Read the complete native CIM definition for one layout element.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "element_name": {"type": "string"},
            },
            "required": ["layout_name", "element_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_layout_element_cim",
        "description": "Replace one same-type layout-element CIM definition, preserving its name and structural map/data links by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "element_name": {"type": "string"},
                "cim_json": {"type": "string"},
                "preserve_name": {"type": "boolean", "default": True},
                "preserve_structural_links": {"type": "boolean", "default": True},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["layout_name", "element_name", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_create_layout_element_from_cim",
        "description": "Create any concrete ArcGIS CIM layout element from validated same-process CIM JSON, keeping it editable in ArcGIS Pro.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "cim_type": {"type": "string"},
                "cim_json": {"type": "string"},
                "element_name": {"type": ["string", "null"]},
            },
            "required": ["layout_name", "cim_type", "cim_json"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_layout_graphics",
        "description": "Add editable text, line, and rectangle graphics to a layout.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "graphics": {"type": "array", "items": {"type": "object", "additionalProperties": True}, "minItems": 1}
            },
            "required": ["layout_name", "graphics"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_add_layout_ticks",
        "description": "Add external coordinate ticks and labels around a named layout map frame without drawing internal grid lines.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "x_intervals": {"type": "integer", "minimum": 2, "maximum": 12},
                "y_intervals": {"type": "integer", "minimum": 2, "maximum": 12},
                "tick_length": {"type": "number"},
                "label_offset": {"type": "number"},
                "color": {},
                "width": {"type": "number"},
                "text_symbol": {"type": "object", "additionalProperties": True}
            },
            "required": ["layout_name"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_add_layout_colorbar",
        "description": "Add a manually specified editable horizontal or vertical static color-bar snapshot.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "bounds": {"type": "object", "additionalProperties": True},
                "orientation": {"type": "string", "enum": ["horizontal", "vertical"]},
                "color_stops": {"type": "array", "items": {}, "minItems": 2},
                "minimum": {"type": "number"},
                "maximum": {"type": "number"},
                "labels": {"type": "array", "items": {"type": "string"}},
                "title": {"type": ["string", "null"]},
                "text_symbol": {"type": "object", "additionalProperties": True},
                "border_color": {},
                "border_width": {"type": "number"}
            },
            "required": ["layout_name", "color_stops"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_add_raster_colorbar_from_layer",
        "description": "Create an editable static snapshot derived from the current raster colorizer. It is renderer-accurate when created but is not dynamically linked; use a native layout legend when automatic updates are required.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
                "bounds": {"type": "object", "additionalProperties": True},
                "orientation": {"type": "string", "enum": ["horizontal", "vertical"]},
                "minimum": {"type": "number"},
                "maximum": {"type": "number"},
                "label_values": {"type": "array", "items": {"type": "number"}},
                "labels": {"type": "array", "items": {"type": "string"}},
                "label_count": {"type": "integer", "minimum": 2, "maximum": 12},
                "number_format": {"type": "string"},
                "samples": {"type": "integer", "minimum": 16, "maximum": 256},
                "dynamic_linked": {"type": "boolean", "default": False},
                "element_prefix": {"type": "string"},
                "title": {"type": ["string", "null"]},
                "text_symbol": {"type": "object", "additionalProperties": True},
                "border_color": {},
                "border_width": {"type": "number"},
            },
            "required": ["layout_name", "layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_execute_batch",
        "description": "Execute a sequence of supported safe live bridge actions in one queue job, reducing round trips and keeping cartographic workflows ordered.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "commands": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "action": {"type": "string"},
                            "payload": {"type": "object"}
                        },
                        "required": ["action"],
                        "additionalProperties": False
                    },
                    "minItems": 1,
                    "maxItems": 100
                },
                "continue_on_error": {"type": "boolean", "default": False}
            },
            "required": ["commands"],
            "additionalProperties": False
        }
    },
    {
        "name": "live_patch_cim_object",
        "description": "Apply an RFC 7396-style JSON merge patch to a supported ArcGIS CIM object. Structural/data-connection keys are blocked by default, allowing long-tail display properties to be changed without new Add-in actions.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "target_type": {
                    "type": "string",
                    "enum": ["map", "layer", "map_graphic", "layout", "layout_element", "standalone_table", "report", "report_element", "presentation", "presentation_page", "presentation_page_element"],
                },
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": ["string", "null"]},
                "graphics_layer_name": {"type": ["string", "null"]},
                "layout_name": {"type": ["string", "null"]},
                "element_name": {"type": ["string", "null"]},
                "table_name": {"type": ["string", "null"]},
                "report_name": {"type": ["string", "null"]},
                "presentation_name": {"type": ["string", "null"]},
                "page_index": {"type": ["integer", "null"], "minimum": 0},
                "patch": {"type": "object", "additionalProperties": True},
                "expected_cim_type": {"type": ["string", "null"]},
                "dry_run": {"type": "boolean", "default": False},
                "include_cim_json": {"type": "boolean", "default": False},
                "allow_structural_changes": {"type": "boolean", "default": False},
                "confirm_apply": {"type": "boolean", "default": False},
            },
            "required": ["target_type", "patch"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_zoom_layout_to_layer",
        "description": "Set a layout map frame extent to a named layer in the current ArcGIS Pro project.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "layer_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "scale_factor": {"type": "number", "minimum": 1.0, "maximum": 10.0, "default": 1.1},
            },
            "required": ["layout_name", "layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_export_layout",
        "description": "Export a layout from the current ArcGIS Pro project to PDF, PNG, JPEG, TIFF, SVG, SVGZ, EPS, or AIX under the output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "output_file": {"type": "string"},
                "dpi": {"type": "integer", "minimum": 72, "maximum": 1200, "default": 300},
                "embed_fonts": {"type": "boolean"},
                "full_rasterization": {"type": "boolean"},
                "convert_character_markers": {"type": "boolean"},
                "compress_vector_graphics": {"type": "boolean"},
                "include_nonvisible_map_layers": {"type": "boolean"},
            },
            "required": ["layout_name", "output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_reports",
        "description": "List reports in the currently open ArcGIS Pro project.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "live_create_report",
        "description": "Create a native ArcGIS Pro report from a layer or standalone table, with selected fields, grouping, statistics, template, styling, and page size.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "report_name": {"type": "string"},
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "field_names": {"type": "array", "items": {"type": "string"}, "maxItems": 500},
                "definition_query": {"type": ["string", "null"]},
                "use_selection": {"type": "boolean", "default": False},
                "group_fields": {"type": "array", "items": {"type": "string"}, "maxItems": 50},
                "statistics": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "field": {"type": "string"},
                            "statistic": {"type": "string", "enum": ["Count", "Minimum", "Maximum", "Range", "Sum", "Mean", "Median", "Mode", "StandardDeviation"]},
                        },
                        "required": ["field", "statistic"],
                        "additionalProperties": False,
                    },
                    "maxItems": 100,
                },
                "template_name": {"type": ["string", "null"]},
                "styling_name": {"type": ["string", "null"]},
                "page_width": {"type": "number", "exclusiveMinimum": 0},
                "page_height": {"type": "number", "exclusiveMinimum": 0},
                "open_report": {"type": "boolean", "default": False},
            },
            "required": ["report_name", "map_member_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_report",
        "description": "Configure report fields, query, groups, page size, and report sections, with confirmed section removal.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "report_name": {"type": "string"},
                "definition_query": {"type": ["string", "null"]},
                "field_names": {"type": "array", "items": {"type": "string"}, "maxItems": 500},
                "add_group_fields": {"type": "array", "items": {"type": "string"}, "maxItems": 50},
                "remove_group_fields": {"type": "array", "items": {"type": "string"}, "maxItems": 50},
                "group_sort_ascending": {"type": "boolean"},
                "group_case_sensitive": {"type": "boolean"},
                "page_width": {"type": "number", "exclusiveMinimum": 0},
                "page_height": {"type": "number", "exclusiveMinimum": 0},
                "sections": {"type": "array", "items": {"type": "object", "additionalProperties": True}, "maxItems": 100},
                "remove_sections": {"type": "array", "items": {"type": "string"}, "maxItems": 100},
                "confirm_remove_sections": {"type": "boolean"},
            },
            "required": ["report_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_report_cim",
        "description": "Read a report's complete native ArcGIS CIM definition.",
        "inputSchema": {
            "type": "object",
            "properties": {"report_name": {"type": "string"}},
            "required": ["report_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_report_cim",
        "description": "Safely apply supported top-level report CIM properties. ArcGIS Pro 3.6.3 full Report.SetDefinition is disabled because it hangs; use report-element CIM tools for sections.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "report_name": {"type": "string"},
                "cim_json": {"type": "string"},
                "preserve_identity": {"type": "boolean", "default": True},
                "preserve_sections": {"type": "boolean", "default": True},
                "preserve_map_series": {"type": "boolean", "default": True},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["report_name", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_report_element_cim",
        "description": "Read the complete native CIM definition for one report element or section.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "report_name": {"type": "string"},
                "element_name": {"type": "string"},
            },
            "required": ["report_name", "element_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_report_element_cim",
        "description": "Replace one same-type report-element CIM definition while preserving structural links by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "report_name": {"type": "string"},
                "element_name": {"type": "string"},
                "cim_json": {"type": "string"},
                "preserve_name": {"type": "boolean", "default": True},
                "preserve_structural_links": {"type": "boolean", "default": True},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["report_name", "element_name", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_duplicate_report",
        "description": "Duplicate an existing report definition.",
        "inputSchema": {
            "type": "object",
            "properties": {"report_name": {"type": "string"}, "new_name": {"type": "string"}},
            "required": ["report_name", "new_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_rename_report",
        "description": "Rename an existing report.",
        "inputSchema": {
            "type": "object",
            "properties": {"report_name": {"type": "string"}, "new_name": {"type": "string"}},
            "required": ["report_name", "new_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_report",
        "description": "Remove a report project item only after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {"report_name": {"type": "string"}, "confirm_remove": {"type": "boolean", "const": True}},
            "required": ["report_name", "confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_export_report_pdf",
        "description": "Export an existing report to PDF under the configured output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "report_name": {"type": "string"},
                "output_file": {"type": "string"},
                "dpi": {"type": "integer", "minimum": 72, "maximum": 1200, "default": 300},
                "use_selection": {"type": "boolean", "default": False},
            },
            "required": ["report_name", "output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_catalog_items",
        "description": "List project catalog items, connections, defaults, and ArcGIS Pro favorites.",
        "inputSchema": {
            "type": "object",
            "properties": {"max_items": {"type": "integer", "minimum": 1, "maximum": 5000}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_project_item",
        "description": "Add an allowed folder, geodatabase, toolbox, style, server-connection file, or other recognized path item to the current project.",
        "inputSchema": {
            "type": "object",
            "properties": {"path": {"type": "string"}, "alias": {"type": ["string", "null"]}},
            "required": ["path"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_project_item",
        "description": "Remove a catalog item reference from the project without deleting the referenced data.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": ["string", "null"]},
                "name": {"type": ["string", "null"]},
                "confirm_remove": {"type": "boolean", "const": True},
            },
            "required": ["confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_repair_project_item",
        "description": "Repair a broken project-item reference to an allowed replacement path.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": ["string", "null"]},
                "name": {"type": ["string", "null"]},
                "new_path": {"type": "string"},
                "confirm_repair": {"type": "boolean", "const": True},
            },
            "required": ["new_path", "confirm_repair"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_favorite",
        "description": "List, add, update, or remove ArcGIS Pro catalog favorites.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation": {"type": "string", "enum": ["list", "add", "update", "remove"]},
                "path": {"type": ["string", "null"]},
                "add_to_all_new_projects": {"type": "boolean"},
            },
            "required": ["operation"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_presentations",
        "description": "List ArcGIS Pro presentations, aspect ratios, pages, and page properties.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "live_create_presentation",
        "description": "Create an editable ArcGIS Pro presentation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "aspect_width": {"type": "integer", "minimum": 1, "maximum": 100},
                "aspect_height": {"type": "integer", "minimum": 1, "maximum": 100},
            },
            "required": ["presentation_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_presentation_cim",
        "description": "Read an ArcGIS Pro presentation's complete native CIM definition.",
        "inputSchema": {
            "type": "object",
            "properties": {"presentation_name": {"type": "string"}},
            "required": ["presentation_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_presentation_cim",
        "description": "Replace a presentation CIM definition while preserving identity and page membership by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "cim_json": {"type": "string"},
                "preserve_identity": {"type": "boolean", "default": True},
                "preserve_pages": {"type": "boolean", "default": True},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["presentation_name", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_presentation_page_cim",
        "description": "Read the native CIM definition for one zero-based presentation page.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "page_index": {"type": "integer", "minimum": 0},
            },
            "required": ["presentation_name", "page_index"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_presentation_page_cim",
        "description": "Replace one same-type presentation-page CIM definition while preserving its map/media storage links by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "page_index": {"type": "integer", "minimum": 0},
                "cim_json": {"type": "string"},
                "preserve_structural_links": {"type": "boolean", "default": True},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["presentation_name", "page_index", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_presentation_page_element_cim",
        "description": "Read the native CIM definition for an element on a zero-based presentation page.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "page_index": {"type": "integer", "minimum": 0},
                "element_name": {"type": "string"},
            },
            "required": ["presentation_name", "page_index", "element_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_presentation_page_element_cim",
        "description": "Replace one same-type presentation-page element CIM definition while preserving structural links by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "page_index": {"type": "integer", "minimum": 0},
                "element_name": {"type": "string"},
                "cim_json": {"type": "string"},
                "preserve_name": {"type": "boolean", "default": True},
                "preserve_structural_links": {"type": "boolean", "default": True},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["presentation_name", "page_index", "element_name", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_duplicate_presentation",
        "description": "Duplicate an ArcGIS Pro presentation and all of its pages.",
        "inputSchema": {
            "type": "object",
            "properties": {"presentation_name": {"type": "string"}, "new_name": {"type": "string"}},
            "required": ["presentation_name", "new_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_rename_presentation",
        "description": "Rename an existing ArcGIS Pro presentation.",
        "inputSchema": {
            "type": "object",
            "properties": {"presentation_name": {"type": "string"}, "new_name": {"type": "string"}},
            "required": ["presentation_name", "new_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_presentation",
        "description": "Remove a presentation project item after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "confirm_remove": {"type": "boolean", "const": True},
            },
            "required": ["presentation_name", "confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_open_presentation",
        "description": "Open an ArcGIS Pro presentation in the visible application.",
        "inputSchema": {
            "type": "object",
            "properties": {"presentation_name": {"type": "string"}},
            "required": ["presentation_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_presentation",
        "description": "Configure presentation-level properties such as aspect ratio.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "aspect_width": {"type": "integer", "minimum": 1, "maximum": 100},
                "aspect_height": {"type": "integer", "minimum": 1, "maximum": 100},
            },
            "required": ["presentation_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_presentation_page",
        "description": "Add a blank, map, image, or video page to an ArcGIS Pro presentation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "page_type": {"type": "string", "enum": ["blank", "map", "image", "video"]},
                "insert_index": {"type": "integer", "minimum": 0},
                "blank_template": {"type": "string", "enum": ["Blank", "Title", "TitleAndParagraph"]},
                "map_name": {"type": ["string", "null"]},
                "extent_layer_name": {"type": ["string", "null"]},
                "media_path": {"type": ["string", "null"]},
                "visible": {"type": "boolean"},
                "locked": {"type": "boolean"},
                "automatic_advancement": {"type": "boolean"},
                "hold_time": {"type": "number", "minimum": 0},
                "background_color": {},
            },
            "required": ["presentation_name", "page_type"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_presentation_page",
        "description": "Configure an existing presentation page, including map camera or image/video source.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "page_index": {"type": "integer", "minimum": 0},
                "map_name": {"type": ["string", "null"]},
                "extent_layer_name": {"type": ["string", "null"]},
                "media_path": {"type": ["string", "null"]},
                "visible": {"type": "boolean"},
                "locked": {"type": "boolean"},
                "automatic_advancement": {"type": "boolean"},
                "hold_time": {"type": "number", "minimum": 0},
                "start_time": {"type": "number", "minimum": 0},
                "end_time": {"type": "number", "minimum": 0},
                "background_color": {},
            },
            "required": ["presentation_name", "page_index"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_presentation_pages",
        "description": "Delete selected presentation pages after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "page_indices": {
                    "type": "array",
                    "items": {"type": "integer", "minimum": 0},
                    "minItems": 1,
                    "maxItems": 1000,
                },
                "confirm_remove": {"type": "boolean", "const": True},
            },
            "required": ["presentation_name", "page_indices", "confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_export_presentation",
        "description": "Export presentation pages to PDF, PNG, JPEG, TIFF, SVG, or SVGZ under the output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "output_file": {"type": "string"},
                "dpi": {"type": "integer", "minimum": 72, "maximum": 1200},
                "custom_pages": {"type": ["string", "null"]},
            },
            "required": ["presentation_name", "output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_save_presentation_file",
        "description": "Save an editable ArcGIS Pro presentation file (.prsx) under the output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string"},
                "output_file": {"type": "string"},
                "overwrite": {"type": "boolean", "default": False},
            },
            "required": ["presentation_name", "output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_attachments",
        "description": "List attachment metadata for bounded object IDs without loading attachment bodies.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "object_ids": {
                    "type": "array",
                    "items": {"type": "integer"},
                    "minItems": 1,
                    "maxItems": 1000,
                },
                "max_attachments": {"type": "integer", "minimum": 1, "maximum": 10000},
            },
            "required": ["map_member_name", "object_ids"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_export_attachment",
        "description": "Export one source attachment to a new file under the configured output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "object_id": {"type": "integer"},
                "attachment_id": {"type": "integer"},
                "output_file": {"type": "string"},
                "overwrite": {"type": "boolean", "default": False},
            },
            "required": ["map_member_name", "object_id", "attachment_id", "output_file"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_attachment",
        "description": "Stage adding a file attachment to a source row; disabled by default and requires explicit source-edit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "object_id": {"type": "integer"},
                "file_path": {"type": "string"},
                "operation_name": {"type": ["string", "null"]},
                "confirm_source_edit": {"type": "boolean", "const": True},
            },
            "required": ["map_member_name", "object_id", "file_path", "confirm_source_edit"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_update_attachment",
        "description": "Stage replacing an existing source attachment; disabled by default and requires explicit source-edit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "object_id": {"type": "integer"},
                "attachment_id": {"type": "integer"},
                "file_path": {"type": "string"},
                "operation_name": {"type": ["string", "null"]},
                "confirm_source_edit": {"type": "boolean", "const": True},
            },
            "required": ["map_member_name", "object_id", "attachment_id", "file_path", "confirm_source_edit"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_attachments",
        "description": "Stage removal of selected source attachments; disabled by default and requires source-edit and delete confirmations.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "object_id": {"type": "integer"},
                "attachment_ids": {
                    "type": "array",
                    "items": {"type": "integer"},
                    "minItems": 1,
                    "maxItems": 1000,
                },
                "operation_name": {"type": ["string", "null"]},
                "confirm_source_edit": {"type": "boolean", "const": True},
                "confirm_delete": {"type": "boolean", "const": True},
            },
            "required": ["map_member_name", "object_id", "attachment_ids", "confirm_source_edit", "confirm_delete"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_relationships",
        "description": "Inspect relationship classes connected to a geodatabase-backed feature layer or standalone table.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "include_all_geodatabase_relationships": {"type": "boolean", "default": False},
            },
            "required": ["map_member_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_query_related_records",
        "description": "Traverse a named geodatabase relationship class and return a bounded set of related rows.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "relationship_name": {"type": "string"},
                "direction": {
                    "type": "string",
                    "enum": ["origin_to_destination", "destination_to_origin"],
                },
                "object_ids": {
                    "type": "array",
                    "items": {"type": "integer"},
                    "minItems": 1,
                    "maxItems": 1000,
                },
                "fields": {
                    "type": "array",
                    "items": {"type": "string"},
                    "maxItems": 500,
                },
                "max_rows": {"type": "integer", "minimum": 1, "maximum": 5000},
            },
            "required": ["map_member_name", "relationship_name", "object_ids"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_style_items",
        "description": "Search referenced ArcGIS Pro styles for grids, map surrounds, table frames, or Standard/Maplex label placements.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "item_type": {
                    "type": "string",
                    "enum": ["grid", "legend", "north_arrow", "scale_bar", "table_frame", "table_frame_field", "standard_label_placement", "maplex_label_placement"],
                },
                "search": {"type": "string"},
                "style_name": {"type": ["string", "null"]},
                "max_items": {"type": "integer", "minimum": 1, "maximum": 5000},
            },
            "required": ["item_type"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_map_grids",
        "description": "List native ArcGIS map grids attached to a layout map frame, optionally with CIM JSON.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "include_cim": {"type": "boolean", "default": False},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_map_grid",
        "description": "Add a native graticule, measured grid, or reference grid from a referenced ArcGIS style.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "style_name": {"type": ["string", "null"]},
                "style_item_name": {"type": ["string", "null"]},
                "search": {"type": "string"},
                "grid_name": {"type": ["string", "null"]},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_map_grid_cim",
        "description": "Replace one native graticule or measured-grid CIM definition after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "grid_name": {"type": "string"},
                "grid_type": {
                    "type": "string",
                    "enum": ["graticule", "measured_grid"],
                },
                "cim_json": {"type": "string"},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["layout_name", "grid_name", "grid_type", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_map_grid",
        "description": "Safely configure a native graticule or measured grid without replacing the full CIM, including X/Y intervals, line visibility, labels, and ticks.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "grid_name": {"type": "string"},
                "new_name": {"type": ["string", "null"]},
                "visible": {"type": "boolean"},
                "minimum_scale": {"type": "number", "minimum": 0},
                "maximum_scale": {"type": "number", "minimum": 0},
                "interval": {"type": "number", "exclusiveMinimum": 0},
                "north_south_interval": {"type": "number", "exclusiveMinimum": 0},
                "east_west_interval": {"type": "number", "exclusiveMinimum": 0},
                "draw_grid_lines": {"type": "boolean"},
                "labels_visible": {"type": "boolean"},
                "from_labels_visible": {"type": "boolean"},
                "to_labels_visible": {"type": "boolean"},
                "exterior_ticks_visible": {"type": "boolean"},
                "from_ticks_visible": {"type": "boolean"},
                "to_ticks_visible": {"type": "boolean"},
                "exterior_tick_length": {"type": "number", "minimum": 0},
                "exterior_tick_offset": {"type": "number"},
                "interior_ticks_visible": {"type": "boolean"},
                "interior_tick_length": {"type": "number", "minimum": 0},
                "interior_tick_offset": {"type": "number"},
            },
            "required": ["layout_name", "grid_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_remove_map_grids",
        "description": "Remove named map grids from a layout map frame after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "grid_names": {
                    "type": "array",
                    "items": {"type": "string"},
                    "minItems": 1,
                    "maxItems": 100,
                },
                "confirm_remove": {"type": "boolean", "const": True},
            },
            "required": ["layout_name", "grid_names", "confirm_remove"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_map_surrounds",
        "description": "List legends, north arrows, scale bars, and other map surrounds in a layout.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "include_cim": {"type": "boolean", "default": False},
            },
            "required": ["layout_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_add_map_surround",
        "description": "Add an editable legend, north arrow, or scale bar linked to a selected map frame.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "surround_type": {
                    "type": "string",
                    "enum": ["legend", "north_arrow", "scale_bar"],
                },
                "element_name": {"type": ["string", "null"]},
                "bounds": {"type": "object", "additionalProperties": True},
                "style_name": {"type": ["string", "null"]},
                "style_item_name": {"type": ["string", "null"]},
                "style_search": {"type": ["string", "null"]},
                "title": {"type": ["string", "null"]},
                "show_title": {"type": "boolean"},
                "auto_add": {"type": "boolean"},
                "units": {"type": "string"},
                "unit_label": {"type": "string"},
                "divisions": {"type": "integer", "minimum": 1, "maximum": 20},
                "subdivisions": {"type": "integer", "minimum": 0, "maximum": 20},
                "division_value": {"type": "number", "exclusiveMinimum": 0},
            },
            "required": ["layout_name", "surround_type"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_map_surround",
        "description": "Relink, restyle, move, resize, show/hide, lock, or configure a named map surround.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "element_name": {"type": "string"},
                "map_frame_name": {"type": ["string", "null"]},
                "x": {"type": "number"},
                "y": {"type": "number"},
                "width": {"type": "number", "exclusiveMinimum": 0},
                "height": {"type": "number", "exclusiveMinimum": 0},
                "rotation": {"type": "number"},
                "visible": {"type": "boolean"},
                "locked": {"type": "boolean"},
                "style_name": {"type": ["string", "null"]},
                "style_item_name": {"type": ["string", "null"]},
                "style_search": {"type": ["string", "null"]},
                "title": {"type": ["string", "null"]},
                "show_title": {"type": "boolean"},
                "auto_add": {"type": "boolean"},
                "units": {"type": "string"},
                "unit_label": {"type": "string"},
                "divisions": {"type": "integer", "minimum": 1, "maximum": 20},
                "subdivisions": {"type": "integer", "minimum": 0, "maximum": 20},
                "division_value": {"type": "number", "exclusiveMinimum": 0},
            },
            "required": ["layout_name", "element_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_map_surround_cim",
        "description": "Replace one legend, north-arrow, or scale-bar CIM definition after explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": "string"},
                "element_name": {"type": "string"},
                "surround_type": {
                    "type": "string",
                    "enum": ["legend", "north_arrow", "scale_bar"],
                },
                "cim_json": {"type": "string"},
                "confirm_replace_definition": {"type": "boolean", "const": True},
            },
            "required": ["layout_name", "element_name", "surround_type", "cim_json", "confirm_replace_definition"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_data_connection",
        "description": "Inspect the native CIM data connection and connection status of a layer or standalone table.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
            },
            "required": ["map_member_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_set_data_connection",
        "description": "Replace a common standard or feature-dataset CIM connection after explicit confirmation; source data is not modified.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "connection_type": {
                    "type": "string",
                    "enum": ["standard", "feature_dataset"],
                },
                "cim_json": {"type": "string"},
                "validate_connection": {"type": "boolean", "default": True},
                "confirm_replace_connection": {"type": "boolean", "const": True},
            },
            "required": ["map_member_name", "connection_type", "cim_json", "confirm_replace_connection"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_replace_data_source",
        "description": "Replace a layer or standalone-table source with an allowed compatible dataset while preserving project-layer configuration.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": "string"},
                "data_path": {"type": "string"},
                "confirm_replace_connection": {"type": "boolean", "const": True},
            },
            "required": ["map_member_name", "data_path", "confirm_replace_connection"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_spatial_query_features",
        "description": "Run a bounded spatial query using an inline geometry or geometry collected from a source feature layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "target_layer_name": {"type": "string"},
                "geometry": {"type": ["object", "null"], "additionalProperties": True},
                "source_layer_name": {"type": ["string", "null"]},
                "source_where_clause": {"type": ["string", "null"]},
                "source_object_ids": {
                    "type": "array",
                    "items": {"type": "integer"},
                    "maxItems": 10000,
                },
                "max_source_features": {"type": "integer", "minimum": 1, "maximum": 100000},
                "spatial_relationship": {
                    "type": "string",
                    "enum": ["Intersects", "EnvelopeIntersects", "IndexIntersects", "Touches", "Overlaps", "Crosses", "Within", "Contains", "Relation"],
                },
                "spatial_relationship_description": {"type": ["string", "null"]},
                "search_order": {"type": "string", "enum": ["Spatial", "Attribute"]},
                "where_clause": {"type": ["string", "null"]},
                "fields": {
                    "type": "array",
                    "items": {"type": "string"},
                    "maxItems": 500,
                },
                "max_rows": {"type": "integer", "minimum": 1, "maximum": 5000},
                "include_geometry": {"type": "boolean", "default": False},
            },
            "required": ["target_layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_spatial_select_features",
        "description": "Select target features by location using an inline geometry or a source feature layer.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "target_layer_name": {"type": "string"},
                "geometry": {"type": ["object", "null"], "additionalProperties": True},
                "source_layer_name": {"type": ["string", "null"]},
                "source_where_clause": {"type": ["string", "null"]},
                "source_object_ids": {
                    "type": "array",
                    "items": {"type": "integer"},
                    "maxItems": 10000,
                },
                "max_source_features": {"type": "integer", "minimum": 1, "maximum": 100000},
                "spatial_relationship": {
                    "type": "string",
                    "enum": ["Intersects", "EnvelopeIntersects", "IndexIntersects", "Touches", "Overlaps", "Crosses", "Within", "Contains", "Relation"],
                },
                "spatial_relationship_description": {"type": ["string", "null"]},
                "search_order": {"type": "string", "enum": ["Spatial", "Attribute"]},
                "where_clause": {"type": ["string", "null"]},
                "combination": {"type": "string", "enum": ["New", "Add", "Subtract", "And", "XOR"]},
                "max_ids": {"type": "integer", "minimum": 0, "maximum": 10000},
            },
            "required": ["target_layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_task_items",
        "description": "List ArcGIS Pro Task Assistant project items and their task/group inventory.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "live_manage_task",
        "description": "Open or close Task Assistant items/tasks, import or open an .esriTasks file, or export a project task item under the output root.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation": {"type": "string", "enum": ["open_item", "open_task", "close_item", "import_file", "open_file", "export_item"]},
                "task_item_name": {"type": ["string", "null"]},
                "task_item_guid": {"type": ["string", "null"]},
                "task_guid": {"type": ["string", "null"]},
                "task_file": {"type": ["string", "null"]},
                "output_file": {"type": ["string", "null"]},
            },
            "required": ["operation"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_workflow_status",
        "description": "Inspect whether ArcGIS Workflow Manager is available and connected, including service item and current job context.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "live_search_workflow_jobs",
        "description": "Run a bounded read-only Workflow Manager job search against the currently connected workflow item.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "q": {"type": ["string", "null"]},
                "search": {"type": ["string", "null"]},
                "start": {"type": "integer", "minimum": 0, "default": 0},
                "num": {"type": "integer", "minimum": 1, "maximum": 200, "default": 50},
                "fields": {"type": "array", "items": {"type": "string"}, "maxItems": 200},
                "sort_fields": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "field": {"type": "string"},
                            "order": {"type": "string", "enum": ["Asc", "Desc"]},
                        },
                        "required": ["field"],
                        "additionalProperties": False,
                    },
                    "maxItems": 20,
                },
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_workflow_job",
        "description": "Read one Workflow Manager job, its current steps, holds, versions, and optionally extended/related properties.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "job_id": {"type": "string"},
                "include_extended_properties": {"type": "boolean", "default": False},
                "include_related_properties": {"type": "boolean", "default": False},
            },
            "required": ["job_id"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_control_workflow_job",
        "description": "Perform an explicitly confirmed Workflow Manager step mutation. Disabled unless allow_external_workflow_actions=true.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation": {"type": "string", "enum": ["set_current_step", "run_steps", "stop_steps", "finish_steps", "assign_step"]},
                "job_id": {"type": "string"},
                "step_id": {"type": ["string", "null"]},
                "step_ids": {"type": "array", "items": {"type": "string"}, "maxItems": 1000},
                "assigned_type": {"type": ["string", "null"]},
                "assigned_to": {"type": ["string", "null"]},
                "confirm_workflow_action": {"type": "boolean", "const": True},
            },
            "required": ["operation", "job_id", "confirm_workflow_action"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_list_knowledge_graphs",
        "description": "List Knowledge Graph investigations and optionally inspect their entity, relationship, property, search, and spatial-reference data model.",
        "inputSchema": {
            "type": "object",
            "properties": {"include_data_model": {"type": "boolean", "default": True}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_knowledge_graph_investigation",
        "description": "Open or rename an existing Knowledge Graph investigation in the current ArcGIS Pro project.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation": {"type": "string", "enum": ["open", "rename"]},
                "investigation_name": {"type": "string"},
                "new_name": {"type": ["string", "null"]},
            },
            "required": ["operation", "investigation_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_query_knowledge_graph",
        "description": "Run a bounded read-only openCypher query or full-text search against an existing Knowledge Graph investigation, materializing graph values safely.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "investigation_name": {"type": "string"},
                "operation": {"type": "string", "enum": ["query", "search"], "default": "query"},
                "query_text": {"type": ["string", "null"]},
                "search_text": {"type": ["string", "null"]},
                "search_target": {"type": "string", "enum": ["Unspecified", "Entity", "Relationship", "BothEntityRelationship", "Provenance"], "default": "BothEntityRelationship"},
                "provenance_behavior": {"type": "string", "enum": ["Exclude", "Include"], "default": "Exclude"},
                "return_search_context": {"type": "boolean", "default": False},
                "offset": {"type": "integer", "minimum": 0, "default": 0},
                "max_rows": {"type": "integer", "minimum": 1, "maximum": 1000, "default": 100},
                "max_value_items": {"type": "integer", "minimum": 1, "maximum": 1000, "default": 100},
            },
            "required": ["investigation_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_link_chart_state",
        "description": "Inspect the active Knowledge Graph link chart's layout, root nodes, and spatial/nonspatial display state.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_link_chart",
        "description": "Configure native Knowledge Graph link-chart layout and root/nonspatial display state without editing graph records.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layout": {"type": ["string", "null"]},
                "apply_layout_to_selection": {"type": "boolean", "default": False},
                "show_nonspatial_data": {"type": "boolean"},
                "show_root_nodes": {"type": "boolean"},
                "clear_root_nodes": {"type": "boolean", "default": False},
                "root_nodes": {"type": "object", "additionalProperties": {"type": "array", "items": {"type": "integer"}}},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_inspect_network_topology_layer",
        "description": "Inspect geodatabase topology, utility network, trace network, Network Analyst, or network dataset layer state and definitions without validation or edits.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string"},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_framework_state",
        "description": "Inspect ArcGIS Pro application, active tab/tool, pane, dock-pane, and optional installed add-in state through the public Framework SDK.",
        "inputSchema": {
            "type": "object",
            "properties": {"include_addins": {"type": "boolean", "default": False}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_inspect_framework_command",
        "description": "Inspect an ArcGIS Pro framework command ID, including type, caption, enabled/relevant/checked state, tooltip, keytip, and children.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "command_id": {"type": "string"},
                "load_on_demand": {"type": "boolean", "default": False},
            },
            "required": ["command_id"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_execute_framework_command",
        "description": "Execute an exact allowlisted ArcGIS Pro framework command through the SDK. Disabled by default and requires explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "command_id": {"type": "string"},
                "arguments": {"type": "array", "items": {}, "maxItems": 100},
                "confirm_execute": {"type": "boolean", "const": True},
            },
            "required": ["command_id", "confirm_execute"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_framework_application",
        "description": "Activate an allowlisted ArcGIS Pro tab/tool/backstage page or change basic ribbon/backstage UI state. Disabled by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "active_tab": {"type": ["string", "null"]},
                "current_tool": {"type": ["string", "null"]},
                "ribbon_minimized": {"type": "boolean"},
                "close_backstage": {"type": "boolean", "default": False},
                "open_backstage_page": {"type": ["string", "null"]},
                "confirm_ui_change": {"type": "boolean", "const": True},
            },
            "required": ["confirm_ui_change"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_framework_pane",
        "description": "Create, activate, hide, pin, unpin, close, or flash an exact allowlisted ArcGIS Pro pane/dock pane. Disabled by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation": {"type": "string", "enum": ["create", "activate", "hide", "pin", "unpin", "close", "flash"]},
                "pane_id": {"type": ["string", "null"]},
                "instance_id": {"type": ["integer", "null"], "minimum": 0},
                "force_activate": {"type": "boolean", "default": False},
                "confirm_pane_action": {"type": "boolean", "const": True},
            },
            "required": ["operation", "confirm_pane_action"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_portal_state",
        "description": "Inspect configured ArcGIS portals, active portal, availability, sign-in state, and username without returning credentials or tokens and without changing portal state.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "live_inspect_time_zones",
        "description": "Search ArcGIS Pro's IANA time-zone catalog and convert between IANA and Windows IDs. Read-only and project-independent.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "query": {"type": ["string", "null"]},
                "time_zone_id": {"type": ["string", "null"]},
                "respect_dst": {"type": "boolean", "default": True},
                "include_local": {"type": "boolean", "default": True},
                "include_unknown": {"type": "boolean", "default": False},
                "include_empty": {"type": "boolean", "default": False},
                "max_results": {"type": "integer", "minimum": 1, "maximum": 1000, "default": 200},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_inspect_locators",
        "description": "Inspect locator providers configured for the active ArcGIS Pro map view, including path, order, validity, and enabled state. Does not geocode or change providers.",
        "inputSchema": {
            "type": "object",
            "properties": {"map_name": {"type": ["string", "null"]}},
            "additionalProperties": False,
        },
    },
    {
        "name": "live_geocode_location",
        "description": "Run a read-only geocoding lookup through the active ArcGIS Pro locator providers without adding results to the map or changing the view. May contact configured external locator services.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "location": {"type": "string", "minLength": 1, "maxLength": 2000},
                "max_results": {"type": "integer", "minimum": 1, "maximum": 200, "default": 20},
            },
            "required": ["location"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_get_operation_history",
        "description": "Inspect bounded undo/redo operation history for a map, layout, report, or presentation without changing project or source data.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "scope": {"type": "string", "enum": ["map", "layout", "report", "presentation"], "default": "map"},
                "item_name": {"type": ["string", "null"]},
                "max_operations": {"type": "integer", "minimum": 1, "maximum": 500, "default": 100},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_control_operation_history",
        "description": "Undo or redo bounded operation history for a map, layout, report, or presentation. Conservatively treated as a source-editing-capable action and disabled by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "scope": {"type": "string", "enum": ["map", "layout", "report", "presentation"], "default": "map"},
                "item_name": {"type": ["string", "null"]},
                "operation": {"type": "string", "enum": ["undo", "redo"]},
                "count": {"type": "integer", "minimum": 1, "maximum": 100, "default": 1},
                "confirm_operation": {"type": "boolean", "const": True},
            },
            "required": ["operation", "confirm_operation"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_geometry_engine",
        "description": "Run bounded in-memory ArcGIS GeometryEngine operations without reading or modifying datasets: projection, buffers, overlays, predicates, measurements, simplify/generalize/densify, transforms, nearest point, and multipart splitting.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation": {
                    "type": "string",
                    "enum": [
                        "describe", "project", "buffer", "geodesic_buffer", "boundary",
                        "convex_hull", "centroid", "label_point", "simplify", "simplify_ogc",
                        "generalize", "densify_by_length", "move", "rotate", "scale",
                        "intersection", "union", "difference", "symmetric_difference", "clip",
                        "contains", "within", "intersects", "disjoint", "touches", "overlaps",
                        "crosses", "equals", "relate", "distance", "distance_3d",
                        "geodesic_distance", "area", "geodesic_area", "length", "length_3d",
                        "geodesic_length", "nearest_point", "multipart_to_singlepart"
                    ],
                },
                "geometry": {"oneOf": [{"type": "object"}, {"type": "string"}]},
                "geometry_format": {"type": "string", "enum": ["json", "wkt"], "default": "json"},
                "geometry_wkid": {"type": "integer"},
                "geometry2": {"oneOf": [{"type": "object"}, {"type": "string"}]},
                "geometry2_format": {"type": "string", "enum": ["json", "wkt"], "default": "json"},
                "geometry2_wkid": {"type": "integer"},
                "origin": {"oneOf": [{"type": "object"}, {"type": "string"}]},
                "origin_format": {"type": "string", "enum": ["json", "wkt"], "default": "json"},
                "origin_wkid": {"type": "integer"},
                "output_wkid": {"type": "integer"},
                "distance": {"type": "number"},
                "max_deviation": {"type": "number", "minimum": 0},
                "max_segment_length": {"type": "number", "exclusiveMinimum": 0},
                "dx": {"type": "number"},
                "dy": {"type": "number"},
                "dz": {"type": "number", "default": 0},
                "rotation_radians": {"type": "number"},
                "scale_x": {"type": "number"},
                "scale_y": {"type": "number"},
                "scale_z": {"type": "number", "default": 1},
                "relation": {"type": "string"},
                "force": {"type": "boolean", "default": True},
                "remove_degenerate_parts": {"type": "boolean", "default": True},
                "preserve_curves": {"type": "boolean", "default": True},
                "include_wkt": {"type": "boolean", "default": False},
                "max_results": {"type": "integer", "minimum": 1, "maximum": 5000, "default": 1000},
            },
            "required": ["operation", "geometry"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_exploratory_analysis",
        "description": "Inspect, add, remove, or clear ArcGIS Pro exploratory Viewshed, ViewDome, and LineOfSight analyses in the active scene view. This changes only unsaved project state and never edits source datasets; remove and clear require explicit confirmation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation": {
                    "type": "string",
                    "enum": ["inspect", "add", "remove", "clear"],
                    "default": "inspect",
                },
                "map_name": {"type": ["string", "null"]},
                "analysis_type": {
                    "type": ["string", "null"],
                    "enum": ["viewshed", "view_dome", "line_of_sight", None],
                },
                "observer": {"type": ["object", "null"]},
                "targets": {
                    "type": ["array", "null"],
                    "items": {"type": "object"},
                    "minItems": 1,
                    "maxItems": 1000,
                },
                "vertical_angle": {"type": "number", "exclusiveMinimum": 0, "maximum": 180, "default": 60},
                "horizontal_angle": {"type": "number", "exclusiveMinimum": 0, "maximum": 360, "default": 90},
                "minimum_distance": {"type": "number", "minimum": 0, "default": 0},
                "maximum_distance": {"type": "number", "exclusiveMinimum": 0, "default": 1000},
                "analysis_id": {"type": ["integer", "null"], "minimum": 0},
                "confirm_remove": {"type": "boolean", "default": False},
                "confirm_clear": {"type": "boolean", "default": False},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_report_elements",
        "description": "Inspect, create, or explicitly remove native report elements through ArcGIS Pro ReportElementFactory: paragraph text, field values/statistics, dynamic pictures, attachment frames, chart frames, and map frames. Project-only mutation; source datasets are unchanged and the project is not saved.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "report_name": {"type": "string", "minLength": 1},
                "operation": {"type": "string", "enum": ["inspect", "add", "remove"], "default": "inspect"},
                "section_name": {"type": ["string", "null"]},
                "element_type": {
                    "type": ["string", "null"],
                    "enum": ["paragraph", "field_value", "field_statistic", "dynamic_picture", "attachment_frame", "chart_frame", "map_frame", None],
                },
                "element_name": {"type": ["string", "null"]},
                "element_names": {"type": "array", "items": {"type": "string"}, "maxItems": 1000},
                "bounds": {"type": ["object", "null"]},
                "text": {"type": ["string", "null"], "maxLength": 1000000},
                "field": {"type": ["string", "null"]},
                "statistic": {"type": ["string", "null"]},
                "expression": {"type": ["string", "null"], "maxLength": 1000000},
                "map_name": {"type": ["string", "null"]},
                "map_member_name": {"type": ["string", "null"]},
                "chart_name": {"type": ["string", "null"]},
                "color": {},
                "size": {"type": "number", "exclusiveMinimum": 0, "maximum": 1000},
                "font_family": {"type": ["string", "null"]},
                "font_style": {"type": ["string", "null"]},
                "horizontal_alignment": {"type": ["string", "null"]},
                "vertical_alignment": {"type": ["string", "null"]},
                "confirm_remove": {"type": "boolean", "default": False},
            },
            "required": ["report_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_presentation_elements",
        "description": "Inspect, create, group, ungroup, or explicitly remove native elements on an ArcGIS Pro presentation page through PresentationElementFactory. Supports text, pictures, predefined shapes, and arrows without saving the project or changing source data.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string", "minLength": 1},
                "page_index": {"type": "integer", "minimum": 0, "default": 0},
                "operation": {"type": "string", "enum": ["inspect", "add", "remove", "group", "ungroup"], "default": "inspect"},
                "element_type": {"type": ["string", "null"], "enum": ["text", "picture", "shape", "arrow", None]},
                "element_name": {"type": ["string", "null"]},
                "element_names": {"type": "array", "items": {"type": "string"}, "maxItems": 1000},
                "bounds": {"type": ["object", "null"]},
                "text": {"type": ["string", "null"], "maxLength": 1000000},
                "media_path": {"type": ["string", "null"]},
                "shape": {"type": ["string", "null"], "enum": ["Rectangle", "Circle", "Ellipse", "Triangle", "RightTriangle", "Cross", "X", "RoundedRectangle", "HalfCircle", "Cloud", None]},
                "start": {"type": ["object", "null"]},
                "end": {"type": ["object", "null"]},
                "arrow_on_both_ends": {"type": "boolean", "default": False},
                "arrow_size": {"type": "number", "exclusiveMinimum": 0, "maximum": 100},
                "line_width": {"type": "number", "exclusiveMinimum": 0, "maximum": 100},
                "arrow_head_key": {"type": ["string", "null"]},
                "fill_color": {},
                "outline_color": {},
                "outline_width": {"type": "number", "minimum": 0, "maximum": 100},
                "color": {},
                "size": {"type": "number", "exclusiveMinimum": 0, "maximum": 1000},
                "font_family": {"type": ["string", "null"]},
                "font_style": {"type": ["string", "null"]},
                "horizontal_alignment": {"type": ["string", "null"]},
                "vertical_alignment": {"type": ["string", "null"]},
                "confirm_remove": {"type": "boolean", "default": False},
            },
            "required": ["presentation_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_specialized_renderer",
        "description": "Inspect, apply, or explicitly remove native ArcGIS Pro renderers for TIN, terrain, and point-cloud scene layers. Supports TIN faces/edges/nodes/contours/elevation/aspect/breaklines, terrain points/dirty areas, point-cloud stretch/class-breaks/unique/RGB, and eye-dome lighting. Changes project display state only.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "layer_name": {"type": "string", "minLength": 1},
                "operation": {"type": "string", "enum": ["inspect", "apply", "remove"], "default": "inspect"},
                "renderer_type": {
                    "type": ["string", "null"],
                    "enum": [
                        "face", "edge", "node", "contour", "face_elevation", "face_aspect",
                        "node_elevation", "terrain_points", "dirty_area", "breakline",
                        "StretchRenderer", "ClassBreaksRenderer", "UniqueValueRenderer", "RGBRenderer",
                        "stretch", "class_breaks", "unique_value", "rgb", None
                    ],
                },
                "target": {"type": ["string", "null"], "enum": ["Surface", "Contours", "Edges", "Points", "DirtyArea", None]},
                "field": {"type": ["string", "null"]},
                "class_count": {"type": "integer", "minimum": 2, "maximum": 100, "default": 10},
                "classification": {"type": ["string", "null"]},
                "color_stops": {"type": "array", "items": {}, "maxItems": 256},
                "color": {},
                "outline_color": {},
                "index_color": {},
                "width": {"type": "number", "minimum": 0, "maximum": 100},
                "index_width": {"type": "number", "minimum": 0, "maximum": 100},
                "size": {"type": "number", "exclusiveMinimum": 0, "maximum": 1000},
                "label": {"type": ["string", "null"]},
                "description": {"type": ["string", "null"]},
                "index_label": {"type": ["string", "null"]},
                "index_description": {"type": ["string", "null"]},
                "contour_interval": {"type": "number", "exclusiveMinimum": 0},
                "reference_height": {"type": "number"},
                "index_contour_factor": {"type": "integer", "minimum": 1, "maximum": 100},
                "eye_dome_lighting": {"type": "boolean"},
                "eye_dome_radius": {"type": "number", "minimum": 0, "maximum": 100},
                "eye_dome_strength": {"type": "number", "minimum": 0, "maximum": 100},
                "include_cim": {"type": "boolean", "default": False},
                "confirm_remove": {"type": "boolean", "default": False},
            },
            "required": ["layer_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_inspect_printers",
        "description": "Inspect read-only ArcGIS Pro print readiness and printer preferences configured on layouts. Does not open a print dialog, send a job, save the project, or modify external state.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layout_name": {"type": ["string", "null"]},
                "max_results": {"type": "integer", "minimum": 1, "maximum": 1000, "default": 200},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "live_manage_report_composition",
        "description": "Inspect or manage native ArcGIS Pro report composition: supplemental layout pages, subreports, section expressions, layout-page replacement, and explicitly confirmed supplemental/related-report removal. Changes only unsaved project state.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "report_name": {"type": "string", "minLength": 1},
                "operation": {
                    "type": "string",
                    "enum": ["inspect", "add_layout_page", "replace_layout_page", "add_subreport", "set_expressions", "remove_supplemental_page", "remove_related_report"],
                    "default": "inspect",
                },
                "layout_name": {"type": ["string", "null"]},
                "section_name": {"type": ["string", "null"]},
                "supplemental_page_name": {"type": ["string", "null"]},
                "subreport_name": {"type": ["string", "null"]},
                "related_report_name": {"type": ["string", "null"]},
                "insert_index": {"type": "integer", "minimum": 0, "default": 0},
                "adjust_page_size": {"type": "boolean", "default": True},
                "expressions": {
                    "type": "array",
                    "maxItems": 500,
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "title": {"type": ["string", "null"]},
                            "expression": {"type": "string", "maxLength": 1000000},
                            "return_type": {"type": ["string", "null"]},
                        },
                        "required": ["name", "expression"],
                        "additionalProperties": False,
                    },
                },
                "old_expression_names": {"type": "array", "items": {"type": "string"}, "maxItems": 500},
                "renamed_expression_names": {"type": "array", "items": {"type": "string"}, "maxItems": 500},
                "confirm_remove": {"type": "boolean", "default": False},
            },
            "required": ["report_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_configure_presentation_transition",
        "description": "Inspect, apply, or clear native ArcGIS Pro page transitions and optionally set speaker notes on one or more presentation pages. Supports None, Swipe, Fade, and Fly without saving the project.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "presentation_name": {"type": "string", "minLength": 1},
                "operation": {"type": "string", "enum": ["inspect", "apply", "clear"], "default": "inspect"},
                "page_index": {"type": "integer", "minimum": 0, "default": 0},
                "page_indices": {"type": "array", "items": {"type": "integer", "minimum": 0}, "maxItems": 1000},
                "transition_type": {"type": ["string", "null"], "enum": ["None", "Swipe", "Fade", "Fly", None]},
                "swipe_direction": {"type": ["string", "null"], "enum": ["None", "Top", "Bottom", "Left", "Right", None]},
                "duration": {"type": "number", "minimum": 0, "maximum": 120, "default": 1},
                "speaker_notes": {"type": ["string", "null"], "maxLength": 1000000},
            },
            "required": ["presentation_name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_evaluate_attribute_rules",
        "description": "Run ArcGIS geodatabase attribute-rule evaluation for a feature layer or standalone table. Evaluation can update validation error tables, so it is disabled by the source-editing gate and requires explicit confirmation. It is never used during non-destructive validation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "map_name": {"type": ["string", "null"]},
                "member_name": {"type": "string", "minLength": 1},
                "rule_type": {"type": "string", "enum": ["Calculation", "Constraint", "Validation", "All"], "default": "Validation"},
                "version_scope": {"type": "string", "enum": ["EntireVersion", "ChangesInVersion"], "default": "EntireVersion"},
                "service_synchronization": {"type": "string", "enum": ["Synchronous", "Asynchronous"], "default": "Synchronous"},
                "extent": {"type": ["object", "null"]},
                "confirm_evaluate": {"type": "boolean", "const": True},
            },
            "required": ["member_name", "confirm_evaluate"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_save_project_copy",
        "description": "Deprecated alias for Save As. Saves the current ArcGIS Pro project under the output root and switches the open project to that copy.",
        "inputSchema": {
            "type": "object",
            "properties": {"output_aprx": {"type": "string"}},
            "required": ["output_aprx"],
            "additionalProperties": False,
        },
    },
    {
        "name": "live_save_project_as",
        "description": "Explicitly save the current project to a new .aprx under the output root. ArcGIS Pro switches to the new project copy.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "output_aprx": {"type": "string"},
                "overwrite": {"type": "boolean", "default": False}
            },
            "required": ["output_aprx"],
            "additionalProperties": False
        }
    },
]


class ArcGISMCP:
    def __init__(self, config_path: Path):
        self.config_path = config_path.resolve()
        self.config = self._load_config(self.config_path)
        self.arcgis_python = Path(self.config["arcgis_python"])
        self.allowed_roots = [Path(p).resolve() for p in self.config.get("allowed_data_roots", [])]
        self.output_root = Path(self.config["output_root"]).resolve()
        self.output_root.mkdir(parents=True, exist_ok=True)
        self.timeout = int(self.config.get("timeout_seconds", 1800))
        self.allow_destructive = bool(self.config.get("allow_destructive_tools", False))
        self.allow_source_editing = bool(self.config.get("allow_source_editing", False))
        self.allow_schema_changes = bool(self.config.get("allow_schema_changes", False))
        self.allow_external_publishing = bool(self.config.get("allow_external_publishing", False))
        self.allow_external_workflow_actions = bool(
            self.config.get("allow_external_workflow_actions", False)
        )
        self.allow_framework_ui_control = bool(
            self.config.get("allow_framework_ui_control", False)
        )
        self.allowed_framework_commands = {
            str(value).casefold() for value in self.config.get("allowed_framework_commands", [])
        }
        self.allowed_framework_panes = {
            str(value).casefold() for value in self.config.get("allowed_framework_panes", [])
        }
        self.allowed_framework_tabs = {
            str(value).casefold() for value in self.config.get("allowed_framework_tabs", [])
        }
        self.allowed_framework_tools = {
            str(value).casefold() for value in self.config.get("allowed_framework_tools", [])
        }
        self.allowed_backstage_pages = {
            str(value).casefold() for value in self.config.get("allowed_backstage_pages", [])
        }
        self.live_queue = Path(self.config.get("live_bridge_queue", ROOT / "bridge_queue")).resolve()
        self.live_timeout = int(self.config.get("live_bridge_timeout_seconds", 180))
        self.live_arcpy_worker_fallback = bool(
            self.config.get("live_arcpy_worker_fallback", True)
        )
        self._request_local = threading.local()
        self._live_identity_lock = threading.RLock()
        self._live_session_id: str | None = None
        self._live_project_path: str | None = None
        self._framework_registry_cache: list[dict[str, Any]] | None = None
        for name in ("jobs", "results", "processing", "cancellations", "completed"):
            (self.live_queue / name).mkdir(parents=True, exist_ok=True)

    def execute_tool(
        self,
        name: str,
        arguments: dict[str, Any],
        cancellation: threading.Event,
    ) -> Any:
        self._request_local.cancellation = cancellation
        try:
            if cancellation.is_set():
                raise RequestCancelled("MCP request was cancelled")
            return self.call_tool(name, arguments)
        finally:
            self._request_local.cancellation = None

    def _check_cancelled(self) -> None:
        cancellation = getattr(self._request_local, "cancellation", None)
        if cancellation is not None and cancellation.is_set():
            raise RequestCancelled("MCP request was cancelled")

    @staticmethod
    def _load_config(path: Path) -> dict[str, Any]:
        if not path.exists():
            raise FileNotFoundError(f"Config file not found: {path}")
        data = json.loads(path.read_text(encoding="utf-8"))
        for key in ("arcgis_python", "allowed_data_roots", "output_root"):
            if key not in data:
                raise ValueError(f"Missing config key: {key}")
        if not data["allowed_data_roots"]:
            raise ValueError("allowed_data_roots must contain at least one directory")
        return data

    def _arcgis_install_root(self) -> Path:
        configured = self.config.get("arcgis_pro_install_dir")
        if configured:
            candidate = Path(str(configured)).resolve()
            if (candidate / "bin" / "ArcGIS.Desktop.Framework.dll").exists():
                return candidate
            raise FileNotFoundError(
                f"Configured ArcGIS Pro install does not contain bin/ArcGIS.Desktop.Framework.dll: {candidate}"
            )
        for candidate in self.arcgis_python.resolve().parents:
            if (candidate / "bin" / "ArcGIS.Desktop.Framework.dll").exists():
                return candidate
        raise FileNotFoundError(
            f"Could not derive the ArcGIS Pro install root from arcgis_python={self.arcgis_python}"
        )

    @staticmethod
    def _xml_local_name(tag: str) -> str:
        return tag.rsplit("}", 1)[-1]

    @staticmethod
    def _framework_extension(bin_root: Path, source: Path) -> str:
        relative = source.relative_to(bin_root)
        parts = relative.parts
        if len(parts) >= 2 and parts[0].casefold() in {"extensions", "configurations"}:
            return parts[1]
        return parts[0] if parts else "Core"

    @staticmethod
    def _framework_locale(source: Path) -> str | None:
        for part in source.parts:
            if re.fullmatch(r"[a-z]{2}-[A-Z]{2}", part):
                return part
        return None

    @staticmethod
    def _framework_risk_hints(record: dict[str, Any]) -> list[str]:
        text = " ".join(
            [
                str(record.get("id", "")),
                *record.get("captions", []),
                *record.get("localized_captions", []),
                *record.get("terms", []),
            ]
        ).casefold()
        categories = {
            "project_or_file_write": ("save", "write", "package", "backup"),
            "delete_or_remove": ("delete", "remove", "truncate", "discard"),
            "source_or_project_edit": ("edit", "create", "new", "add", "update", "apply", "repair", "convert"),
            "external_or_portal": ("publish", "share", "upload", "portal", "sign in", "signin", "license"),
            "import_or_export": ("import", "export"),
            "print": ("print", "printer"),
            "execute_or_run": ("execute", "run", "start", "stop", "finish"),
        }
        return [name for name, words in categories.items() if any(word in text for word in words)]

    def _load_framework_registry(self) -> list[dict[str, Any]]:
        if self._framework_registry_cache is not None:
            return self._framework_registry_cache
        install_root = self._arcgis_install_root()
        bin_root = install_root / "bin"
        actionable_kinds = {
            "button", "tool", "dockPane", "pane", "tab", "backstageTab",
            "propertyPage", "gallery", "comboBox", "menu", "splitButton",
        }
        records: dict[str, dict[str, Any]] = {}

        def record_for(identifier: str) -> dict[str, Any]:
            key = identifier.casefold()
            if key not in records:
                records[key] = {
                    "id": identifier,
                    "kinds": set(),
                    "extensions": set(),
                    "captions": set(),
                    "localized_captions": set(),
                    "extended_captions": set(),
                    "terms": set(),
                    "class_names": set(),
                    "conditions": set(),
                    "sources": set(),
                }
            return records[key]

        for source in bin_root.rglob("*.daml"):
            try:
                root = ET.parse(source).getroot()
            except (ET.ParseError, OSError):
                continue
            extension = self._framework_extension(bin_root, source)
            locale = self._framework_locale(source)
            for element in root.iter():
                kind = self._xml_local_name(element.tag)
                identifier = element.attrib.get("id")
                if kind not in actionable_kinds or not identifier:
                    continue
                record = record_for(identifier)
                record["kinds"].add(kind)
                record["extensions"].add(extension)
                record["sources"].add(source.relative_to(bin_root).as_posix())
                caption = element.attrib.get("caption")
                if caption:
                    target = "localized_captions" if locale else "captions"
                    record[target].add(caption.strip())
                extended = element.attrib.get("extendedCaption")
                if extended:
                    record["extended_captions"].add(extended.strip())
                class_name = element.attrib.get("className")
                if class_name:
                    record["class_names"].add(class_name)
                condition = element.attrib.get("condition")
                if condition:
                    record["conditions"].add(condition)

        for source in bin_root.rglob("CommandSearchTerms.xml"):
            try:
                root = ET.parse(source).getroot()
            except (ET.ParseError, OSError):
                continue
            extension = self._framework_extension(bin_root, source)
            for command in root.iter():
                if self._xml_local_name(command.tag) != "Command":
                    continue
                identifier = command.attrib.get("id")
                if not identifier:
                    continue
                record = record_for(identifier)
                record["extensions"].add(extension)
                record["sources"].add(source.relative_to(bin_root).as_posix())
                for child in command:
                    if self._xml_local_name(child.tag) == "Term" and child.text:
                        record["terms"].add(child.text.strip())

        materialized: list[dict[str, Any]] = []
        for record in records.values():
            item = {
                key: sorted(value, key=str.casefold) if isinstance(value, set) else value
                for key, value in record.items()
            }
            item["risk_hints"] = self._framework_risk_hints(item)
            item["execution_review_required"] = True
            materialized.append(item)
        self._framework_registry_cache = sorted(materialized, key=lambda item: item["id"].casefold())
        return self._framework_registry_cache

    def _search_framework_registry(self, arguments: dict[str, Any]) -> dict[str, Any]:
        query = str(arguments.get("query", "")).strip()
        if not query:
            raise ValueError("query must not be empty")
        tokens = [token for token in re.split(r"\s+", query.casefold()) if token]
        kinds = {str(value).casefold() for value in arguments.get("kinds") or []}
        extension = str(arguments.get("extension") or "").strip().casefold()
        max_results = min(max(int(arguments.get("max_results", 50)), 1), 500)
        matches: list[tuple[int, dict[str, Any]]] = []
        for item in self._load_framework_registry():
            item_kinds = {str(value).casefold() for value in item["kinds"]}
            if kinds and not kinds.intersection(item_kinds):
                continue
            if extension and not any(extension in str(value).casefold() for value in item["extensions"]):
                continue
            identifier = str(item["id"]).casefold()
            captions = [str(value).casefold() for value in item["captions"] + item["localized_captions"]]
            terms = [str(value).casefold() for value in item["terms"]]
            haystack = " ".join(
                [identifier, *captions, *terms, *[str(value).casefold() for value in item["extended_captions"]]]
            )
            if not all(token in haystack for token in tokens):
                continue
            score = 0
            normalized_query = query.casefold()
            if identifier == normalized_query:
                score += 1000
            elif normalized_query in identifier:
                score += 500
            if any(normalized_query == value for value in captions):
                score += 300
            elif any(normalized_query in value for value in captions):
                score += 180
            if any(normalized_query == value for value in terms):
                score += 140
            elif any(normalized_query in value for value in terms):
                score += 80
            score += sum(10 for token in tokens if token in identifier)
            matches.append((score, item))
        matches.sort(key=lambda entry: (-entry[0], entry[1]["id"].casefold()))
        install_root = self._arcgis_install_root()
        return {
            "query": query,
            "registry_size": len(self._load_framework_registry()),
            "match_count": len(matches),
            "results": [item for _, item in matches[:max_results]],
            "arcgis_pro_install": str(install_root),
            "source_data_modified": False,
            "execution_review_required": True,
        }

    def _search_sdk_registry(self, arguments: dict[str, Any]) -> dict[str, Any]:
        query = str(arguments.get("query", "")).strip()
        if not query:
            raise ValueError("query must not be empty")
        if len(query) > 200:
            raise ValueError("query must be 200 characters or fewer")
        max_results = min(max(int(arguments.get("max_results", 50)), 1), 200)
        assembly_filters = {
            str(value).strip().casefold()
            for value in arguments.get("assemblies") or []
            if str(value).strip()
        }
        script = ROOT / "scripts" / "inspect-arcgis-sdk.ps1"
        if not script.exists():
            raise FileNotFoundError(f"ArcGIS SDK inspection script not found: {script}")
        pwsh = Path(r"C:\Program Files\PowerShell\7\pwsh.exe")
        if not pwsh.exists():
            raise FileNotFoundError(
                "PowerShell 7 is required for ArcGIS Pro SDK reflection: "
                f"{pwsh}"
            )
        literal_pattern = re.escape(query)
        completed = subprocess.run(
            [
                str(pwsh),
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(script),
                "-Pattern",
                literal_pattern,
                "-MemberPattern",
                literal_pattern,
                "-ArcGISProInstallDir",
                str(self._arcgis_install_root()),
                "-MaxTypes",
                str(max_results),
            ],
            text=True,
            encoding="utf-8",
            errors="replace",
            capture_output=True,
            timeout=max(self.timeout, 120),
            cwd=str(ROOT),
            check=False,
        )
        stdout = completed.stdout.strip().lstrip("\ufeff")
        if completed.returncode != 0:
            detail = completed.stderr.strip() or stdout or f"exit code {completed.returncode}"
            raise RuntimeError(f"ArcGIS SDK registry search failed: {detail}")
        if not stdout:
            raw_results: list[dict[str, Any]] = []
        else:
            try:
                decoded = json.loads(stdout)
            except json.JSONDecodeError as exc:
                raise RuntimeError(f"Invalid ArcGIS SDK registry response: {stdout[-2000:]}") from exc
            raw_results = decoded if isinstance(decoded, list) else [decoded]
        results = [
            item
            for item in raw_results
            if isinstance(item, dict)
            and (
                not assembly_filters
                or any(value in str(item.get("assembly") or "").casefold() for value in assembly_filters)
            )
        ][:max_results]
        return {
            "query": query,
            "match_count": len(results),
            "results": results,
            "arcgis_pro_install": str(self._arcgis_install_root()),
            "source_data_modified": False,
            "project_modified": False,
            "execution_available": False,
            "implementation_review_required": True,
        }

    def _policy_state(self) -> dict[str, Any]:
        return {
            "allow_destructive_tools": self.allow_destructive,
            "allow_source_editing": self.allow_source_editing,
            "allow_schema_changes": self.allow_schema_changes,
            "allow_external_publishing": self.allow_external_publishing,
            "allow_external_workflow_actions": self.allow_external_workflow_actions,
            "allow_framework_ui_control": self.allow_framework_ui_control,
            "allowed_framework_commands": sorted(self.allowed_framework_commands),
            "allowed_framework_panes": sorted(self.allowed_framework_panes),
            "allowed_framework_tabs": sorted(self.allowed_framework_tabs),
            "allowed_framework_tools": sorted(self.allowed_framework_tools),
            "allowed_backstage_pages": sorted(self.allowed_backstage_pages),
        }

    def _reload_policy_config(self, arguments: dict[str, Any]) -> dict[str, Any]:
        if arguments.get("confirm_reload") is not True:
            raise ValueError("confirm_reload=true is required")
        before = self._policy_state()
        refreshed = self._load_config(self.config_path)
        self.allow_destructive = bool(refreshed.get("allow_destructive_tools", False))
        self.allow_source_editing = bool(refreshed.get("allow_source_editing", False))
        self.allow_schema_changes = bool(refreshed.get("allow_schema_changes", False))
        self.allow_external_publishing = bool(refreshed.get("allow_external_publishing", False))
        self.allow_external_workflow_actions = bool(
            refreshed.get("allow_external_workflow_actions", False)
        )
        self.allow_framework_ui_control = bool(
            refreshed.get("allow_framework_ui_control", False)
        )
        self.allowed_framework_commands = {
            str(value).casefold() for value in refreshed.get("allowed_framework_commands", [])
        }
        self.allowed_framework_panes = {
            str(value).casefold() for value in refreshed.get("allowed_framework_panes", [])
        }
        self.allowed_framework_tabs = {
            str(value).casefold() for value in refreshed.get("allowed_framework_tabs", [])
        }
        self.allowed_framework_tools = {
            str(value).casefold() for value in refreshed.get("allowed_framework_tools", [])
        }
        self.allowed_backstage_pages = {
            str(value).casefold() for value in refreshed.get("allowed_backstage_pages", [])
        }
        self.config.update(
            {
                key: refreshed.get(key)
                for key in (
                    "allow_destructive_tools",
                    "allow_source_editing",
                    "allow_schema_changes",
                    "allow_external_publishing",
                    "allow_external_workflow_actions",
                    "allow_framework_ui_control",
                    "allowed_framework_commands",
                    "allowed_framework_panes",
                    "allowed_framework_tabs",
                    "allowed_framework_tools",
                    "allowed_backstage_pages",
                )
            }
        )
        return {
            "config": str(self.config_path),
            "before": before,
            "after": self._policy_state(),
            "paths_and_runtime_reloaded": False,
            "arcgis_pro_restart_required": False,
            "mcp_process_restart_required": False,
        }

    @staticmethod
    def _normal(path: Path) -> str:
        return os.path.normcase(os.path.abspath(str(path)))

    def _inside(self, path: Path, roots: list[Path]) -> bool:
        candidate = self._normal(path.resolve(strict=False))
        for root in roots:
            root_text = self._normal(root.resolve(strict=False))
            try:
                if os.path.commonpath([candidate, root_text]) == root_text:
                    return True
            except ValueError:
                continue
        return False

    def _read_path(self, value: str, must_exist: bool = True) -> Path:
        path = Path(value).resolve(strict=False)
        roots = self.allowed_roots + [self.output_root]
        if not self._inside(path, roots):
            raise PermissionError(f"Path is outside allowed roots: {path}")
        if must_exist and not path.exists() and not self._catalog_container_exists(path):
            raise FileNotFoundError(str(path))
        return path

    @staticmethod
    def _catalog_container_exists(path: Path) -> bool:
        """Return true for logical datasets inside an existing geodatabase."""
        text = str(path)
        lowered = text.lower()
        for suffix in (".gdb", ".sde", ".mdb"):
            index = lowered.find(suffix)
            if index >= 0:
                container = Path(text[: index + len(suffix)])
                return container.exists()
        return False

    def _write_path(self, value: str) -> Path:
        path = Path(value).resolve(strict=False)
        if not self._inside(path, [self.output_root]):
            raise PermissionError(f"Output must be under {self.output_root}: {path}")
        path.parent.mkdir(parents=True, exist_ok=True)
        return path

    def _validate_embedded_paths(self, value: Any) -> None:
        if isinstance(value, dict):
            for item in value.values():
                self._validate_embedded_paths(item)
        elif isinstance(value, list):
            for item in value:
                self._validate_embedded_paths(item)
        elif isinstance(value, str) and Path(value).is_absolute():
            self._read_path(value, must_exist=False)

    def _worker_command(self) -> list[str]:
        if self.arcgis_python.suffix.lower() in {".bat", ".cmd"}:
            return ["cmd.exe", "/d", "/c", "call", str(self.arcgis_python), str(WORKER)]
        return [str(self.arcgis_python), str(WORKER)]

    @staticmethod
    def _worker_result(stdout: str, stderr: str = "") -> Any:
        stdout = stdout.strip()
        if not stdout:
            raise RuntimeError(f"ArcGIS worker returned no JSON. stderr: {stderr.strip()}")
        try:
            response = json.loads(stdout.splitlines()[-1])
        except json.JSONDecodeError as exc:
            raise RuntimeError(f"Invalid ArcGIS worker response: {stdout[-2000:]}") from exc
        if not response.get("ok"):
            raise RuntimeError(response.get("error", "Unknown ArcGIS worker error"))
        return response.get("result")

    @staticmethod
    def _windows_security_block(error: Exception) -> bool:
        message = str(error).casefold()
        return any(
            marker in message
            for marker in (
                "winerror 225",
                "error_virus_infected",
                "contains a virus",
                "potentially unwanted software",
                "包含病毒",
                "潜在的垃圾软件",
            )
        )

    def _run_worker_via_live_bridge(self, request: dict[str, Any]) -> Any:
        response = self._run_live(
            "run_arcpy_worker",
            {
                "worker_path": str(WORKER),
                "request": request,
                "timeout_seconds": self.timeout,
            },
            timeout_seconds=self.timeout + 30,
        )
        if not isinstance(response, dict):
            raise RuntimeError("ArcGIS Pro returned an invalid ArcPy worker response")
        return self._worker_result(json.dumps(response, ensure_ascii=False))

    def _run_worker(self, action: str, payload: dict[str, Any]) -> Any:
        if not self.arcgis_python.exists():
            raise FileNotFoundError(f"ArcGIS Python launcher not found: {self.arcgis_python}")
        request = {"action": action, "payload": payload}
        process: subprocess.Popen[str] | None = None
        try:
            creationflags = 0
            if os.name == "nt":
                creationflags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
            process = subprocess.Popen(
                self._worker_command(),
                # ArcGIS Pro's propy.bat inherits a Windows code page. ASCII-only
                # JSON keeps stdin/stdout stable regardless of that code page.
                text=True,
                encoding="utf-8",
                errors="replace",
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                cwd=str(ROOT),
                creationflags=creationflags,
            )
            input_text: str | None = json.dumps(request, ensure_ascii=True)
            deadline = time.monotonic() + self.timeout
            while True:
                self._check_cancelled()
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    self._terminate_process_tree(process)
                    raise TimeoutError(
                        f"ArcGIS worker exceeded the {self.timeout}-second timeout"
                    )
                try:
                    stdout, stderr = process.communicate(
                        input=input_text,
                        timeout=min(0.2, remaining),
                    )
                    break
                except subprocess.TimeoutExpired:
                    input_text = None
                    continue
                except BaseException:
                    self._terminate_process_tree(process)
                    raise
            stdout = stdout.strip()
            if process.returncode != 0:
                detail = stderr.strip() or stdout or f"exit code {process.returncode}"
                raise RuntimeError(f"ArcGIS worker failed: {detail}")
            return self._worker_result(stdout, stderr)
        except Exception as direct_error:
            if process is not None:
                self._terminate_process_tree(process)
            if not self.live_arcpy_worker_fallback or not self._windows_security_block(direct_error):
                raise
            try:
                return self._run_worker_via_live_bridge(request)
            except Exception as live_error:
                raise RuntimeError(
                    f"{direct_error}\nArcGIS Pro ArcPy fallback also failed: {live_error}"
                ) from live_error

    @staticmethod
    def _terminate_process_tree(process: subprocess.Popen[str]) -> None:
        if process.poll() is not None:
            return
        try:
            if os.name == "nt":
                subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                    timeout=10,
                )
            else:
                process.kill()
            process.wait(timeout=10)
        except Exception:
            try:
                process.kill()
            except Exception:
                pass

    def _run_live(
        self,
        action: str,
        payload: dict[str, Any],
        timeout_seconds: int | None = None,
        _bind_identity: bool = True,
    ) -> Any:
        timeout = self.live_timeout if timeout_seconds is None else timeout_seconds
        expected_session: str | None = None
        expected_project: str | None = None
        if _bind_identity and action not in {"status", "capabilities"}:
            expected_session, expected_project = self._live_identity()

        job_id = uuid.uuid4().hex
        now = time.time()
        job = {
            "id": job_id,
            "idempotency_key": uuid.uuid4().hex,
            "action": action,
            "payload": payload,
            "created_at": now,
            "expires_at": now + timeout,
            "expected_session_id": expected_session,
            "expected_project_path": expected_project,
        }
        temp_path = self.live_queue / "jobs" / f".{job_id}.tmp"
        job_path = self.live_queue / "jobs" / f"{job_id}.json"
        result_path = self.live_queue / "results" / f"{job_id}.json"
        cancellation_path = self.live_queue / "cancellations" / f"{job_id}.json"
        temp_path.write_text(json.dumps(job, ensure_ascii=True), encoding="ascii")
        temp_path.replace(job_path)

        deadline = time.monotonic() + timeout
        try:
            while time.monotonic() < deadline:
                self._check_cancelled()
                if result_path.exists():
                    result = json.loads(result_path.read_text(encoding="utf-8"))
                    result_path.unlink(missing_ok=True)
                    session_id = result.get("bridge_session_id")
                    if not isinstance(session_id, str) or not session_id:
                        raise RuntimeError(
                            "The ArcGIS Pro Add-in uses an older live protocol; install v0.28.1 and restart ArcGIS Pro."
                        )
                    with self._live_identity_lock:
                        self._live_session_id = session_id
                        self._live_project_path = result.get("project_path")
                    if not result.get("ok"):
                        error = result.get("error", "ArcGIS Pro live bridge error")
                        detail = result.get("detail")
                        if detail and detail != error:
                            error = f"{error}\n{detail}"
                        raise RuntimeError(error)
                    return result.get("result")
                time.sleep(0.1)
            raise TimeoutError(
                "ArcGIS Pro live bridge did not respond. Ensure ArcGIS Pro is open, a project is loaded, "
                "and the ArcGIS Codex Bridge add-in is installed and enabled."
            )
        except (TimeoutError, RequestCancelled):
            job_path.unlink(missing_ok=True)
            cancellation_temp = cancellation_path.with_suffix(".tmp")
            cancellation_temp.write_text(
                json.dumps({"id": job_id, "cancelled_at": time.time()}),
                encoding="ascii",
            )
            cancellation_temp.replace(cancellation_path)
            result_path.unlink(missing_ok=True)
            raise

    def _live_identity(self) -> tuple[str, str | None]:
        with self._live_identity_lock:
            if self._live_session_id:
                return self._live_session_id, self._live_project_path
            status = self._run_live("status", {}, _bind_identity=False)
            if not isinstance(status, dict):
                raise RuntimeError("ArcGIS Pro returned an invalid status handshake")
            if not self._live_session_id:
                raise RuntimeError("ArcGIS Pro did not return a live bridge session identifier")
            return self._live_session_id, self._live_project_path

    def _validate_tool_name(self, tool: str) -> re.Match[str]:
        match = re.fullmatch(r"([A-Za-z][A-Za-z0-9_]*)\.([A-Za-z][A-Za-z0-9_]*)", tool)
        if not match or match.group(1).lower() not in ALLOWED_TOOLBOXES:
            raise ValueError(f"Unsupported ArcPy tool path: {tool}")
        normalized = f"{match.group(1).lower()}.{match.group(2).lower()}"
        if match.group(2).lower() in DESTRUCTIVE_TOOL_NAMES and not self.allow_destructive:
            raise PermissionError(f"Destructive ArcPy tool is disabled: {tool}")
        if normalized in SCHEMA_TOOL_PATHS and not self.allow_schema_changes:
            raise PermissionError(
                f"Schema-changing ArcPy tool is disabled by allow_schema_changes=false: {tool}"
            )
        if normalized in SOURCE_MUTATING_TOOL_PATHS and not self.allow_source_editing:
            raise PermissionError(
                f"Source-mutating ArcPy tool is disabled by allow_source_editing=false: {tool}"
            )
        if normalized in PUBLISHING_TOOL_PATHS and not self.allow_external_publishing:
            raise PermissionError(
                f"Publishing/external ArcPy tool is disabled by allow_external_publishing=false: {tool}"
            )
        return match

    def _geoprocessing_safety(self, tool: str) -> dict[str, Any]:
        match = re.fullmatch(r"([A-Za-z][A-Za-z0-9_]*)\.([A-Za-z][A-Za-z0-9_]*)", tool)
        toolbox = match.group(1).lower() if match else ""
        tool_name = match.group(2).lower() if match else ""
        normalized = f"{toolbox}.{tool_name}" if match else tool.lower()
        destructive = tool_name in DESTRUCTIVE_TOOL_NAMES or tool_name.startswith(
            ("delete", "truncate", "drop", "remove")
        )
        schema_change = normalized in SCHEMA_TOOL_PATHS or any(
            token in tool_name
            for token in (
                "addfield", "alterfield", "addindex", "createdomain", "addcodedvalue",
                "assigndomain", "setsubtype", "addsubtype", "editortracking",
            )
        )
        publishing = normalized in PUBLISHING_TOOL_PATHS or any(
            token in tool_name
            for token in ("publish", "uploadservice", "stageservice", "sharepackage")
        )
        likely_source_mutation = destructive or schema_change or normalized in SOURCE_MUTATING_TOOL_PATHS
        toolbox_allowed = toolbox in ALLOWED_TOOLBOXES
        blocked_reasons: list[str] = []
        if not toolbox_allowed:
            blocked_reasons.append("toolbox is not enabled for the generic runner")
        if destructive and not self.allow_destructive:
            blocked_reasons.append("destructive tools are disabled")
        if schema_change and not self.allow_schema_changes:
            blocked_reasons.append("schema changes are disabled")
        if publishing and not self.allow_external_publishing:
            blocked_reasons.append("publishing/external operations are disabled")
        if likely_source_mutation and not (schema_change or publishing) and not self.allow_source_editing:
            blocked_reasons.append("probable source/project mutation requires explicit review")
        return {
            "generic_runner_toolbox_allowed": toolbox_allowed,
            "destructive": destructive,
            "schema_change": schema_change,
            "publishing_or_external": publishing,
            "likely_source_or_project_mutation": likely_source_mutation,
            "blocked_by_current_policy": bool(blocked_reasons),
            "blocked_reasons": blocked_reasons,
        }

    def _require_source_editing(self, destructive: bool = False) -> None:
        if not self.allow_source_editing:
            raise PermissionError(
                "Source editing is implemented but disabled by config: allow_source_editing=false"
            )
        if destructive and not self.allow_destructive:
            raise PermissionError(
                "Destructive source editing is disabled by config: allow_destructive_tools=false"
            )

    def _validate_schema_tool(self, tool: str) -> str:
        normalized = tool.lower()
        if normalized not in SCHEMA_TOOL_PATHS:
            raise ValueError(f"Unsupported schema tool: {tool}")
        if not self.allow_schema_changes:
            raise PermissionError(
                "Schema changes are implemented but disabled by config: allow_schema_changes=false"
            )
        if normalized in DESTRUCTIVE_SCHEMA_TOOL_PATHS and not self.allow_destructive:
            raise PermissionError(
                "Destructive schema changes are disabled by config: allow_destructive_tools=false"
            )
        return tool

    def _validate_publishing_tool(self, tool: str) -> str:
        if tool.lower() not in PUBLISHING_TOOL_PATHS:
            raise ValueError(f"Unsupported publishing tool: {tool}")
        if not self.allow_external_publishing:
            raise PermissionError(
                "Publishing is implemented but disabled by config: allow_external_publishing=false"
            )
        return tool

    def _require_workflow_actions(self) -> None:
        if not self.allow_external_workflow_actions:
            raise PermissionError(
                "Workflow mutations are implemented but disabled by config: "
                "allow_external_workflow_actions=false"
            )

    def _require_framework_ui(self, kind: str, identifier: str | None = None) -> None:
        if not self.allow_framework_ui_control:
            raise PermissionError(
                "Framework UI control is implemented but disabled by config: "
                "allow_framework_ui_control=false"
            )
        if identifier is None:
            return
        allowed = {
            "command": self.allowed_framework_commands,
            "pane": self.allowed_framework_panes,
            "tab": self.allowed_framework_tabs,
            "tool": self.allowed_framework_tools,
            "backstage": self.allowed_backstage_pages,
        }[kind]
        if identifier.casefold() not in allowed:
            raise PermissionError(
                f"Framework {kind} is not in the exact config allowlist: {identifier}"
            )

    def _prepare_live_action(self, action: str, payload: dict[str, Any]) -> dict[str, Any]:
        allowed_actions = {
            "capabilities",
            "status",
            "project_inventory",
            "save_project",
            "get_metadata",
            "set_metadata",
            "add_data",
            "create_map",
            "rename_map",
            "duplicate_map",
            "remove_map",
            "open_map",
            "list_bookmarks",
            "create_bookmark",
            "remove_bookmark",
            "zoom_to_bookmark",
            "configure_bookmark",
            "move_bookmark",
            "import_bookmarks",
            "export_bookmarks",
            "get_camera",
            "set_camera",
            "zoom_full_extent",
            "get_map_view_state",
            "configure_map_view",
            "get_view_filter_state",
            "configure_view_filters",
            "get_active_view_context",
            "configure_active_view_context",
            "navigate_map_view",
            "transform_view_coordinates",
            "flash_features",
            "show_feature_popup",
            "close_feature_popups",
            "manage_feature_cache",
            "capture_view_thumbnail",
            "get_map_cim",
            "set_map_cim",
            "configure_map_properties",
            "save_map_file",
            "run_geoprocessing",
            "describe_layer",
            "get_layer_cim",
            "set_layer_cim",
            "inspect_las_layer",
            "configure_las_layer",
            "inspect_voxel_layer",
            "configure_voxel_layer",
            "get_layer_schema",
            "query_features",
            "summarize_field",
            "get_table_schema",
            "query_table",
            "select_features",
            "get_selection",
            "clear_selection",
            "update_feature_attributes",
            "create_features",
            "delete_features",
            "save_edits",
            "discard_edits",
            "undo_edit",
            "redo_edit",
            "duplicate_layer",
            "create_group_layer",
            "add_standalone_table",
            "manage_standalone_table",
            "remove_standalone_table",
            "get_standalone_table_cim",
            "set_standalone_table_cim",
            "manage_layers",
            "remove_layers",
            "reorder_layer",
            "zoom_to_layer",
            "apply_symbology",
            "apply_vector_symbology",
            "configure_visual_variables",
            "apply_raster_symbology",
            "configure_raster_rendering_rule",
            "configure_multidimensional_raster",
            "list_elevation_surfaces",
            "configure_elevation_surface",
            "configure_layer_elevation",
            "sample_elevation_surface",
            "get_elevation_profile",
            "get_animation_state",
            "control_animation",
            "begin_animation_export",
            "configure_labels",
            "list_label_classes",
            "configure_label_class",
            "remove_label_classes",
            "configure_popup",
            "configure_fields",
            "configure_display_filters",
            "configure_time",
            "get_charts",
            "set_charts",
            "draw_graphics",
            "list_map_graphics",
            "manage_map_graphics",
            "get_map_graphic_cim",
            "set_map_graphic_cim",
            "create_layout",
            "duplicate_layout",
            "rename_layout",
            "remove_layout",
            "open_layout",
            "configure_layout_page",
            "manage_layout_guides",
            "save_layout_file",
            "add_map_frame",
            "list_dynamic_frames",
            "add_table_frame",
            "add_chart_frame",
            "configure_map_frame",
            "list_extent_indicators",
            "add_extent_indicator",
            "configure_extent_indicator",
            "remove_extent_indicators",
            "configure_map_clipping",
            "add_dynamic_text",
            "create_map_series",
            "list_map_series",
            "configure_map_series",
            "remove_map_series",
            "export_map_series",
            "arrange_layout_elements",
            "export_map_view",
            "get_layout_cim",
            "set_layout_cim",
            "get_layout_element_cim",
            "set_layout_element_cim",
            "create_layout_element_from_cim",
            "update_layout_element",
            "organize_layout_elements",
            "add_layout_picture",
            "add_layout_graphics",
            "add_layout_ticks",
            "add_layout_colorbar",
            "add_raster_colorbar_from_layer",
            "import_layout",
            "set_layout_text",
            "zoom_layout",
            "export_layout",
            "save_project_as",
            "save_copy",
            "list_reports",
            "create_report",
            "configure_report",
            "get_report_cim",
            "set_report_cim",
            "get_report_element_cim",
            "set_report_element_cim",
            "duplicate_report",
            "rename_report",
            "remove_report",
            "export_report_pdf",
            "list_catalog_items",
            "add_project_item",
            "remove_project_item",
            "repair_project_item",
            "manage_favorite",
            "list_presentations",
            "create_presentation",
            "get_presentation_cim",
            "set_presentation_cim",
            "get_presentation_page_cim",
            "set_presentation_page_cim",
            "get_presentation_page_element_cim",
            "set_presentation_page_element_cim",
            "duplicate_presentation",
            "rename_presentation",
            "remove_presentation",
            "open_presentation",
            "configure_presentation",
            "add_presentation_page",
            "configure_presentation_page",
            "remove_presentation_pages",
            "export_presentation",
            "save_presentation_file",
            "list_attachments",
            "export_attachment",
            "add_attachment",
            "update_attachment",
            "remove_attachments",
            "list_relationships",
            "query_related_records",
            "list_style_items",
            "list_map_grids",
            "add_map_grid",
            "set_map_grid_cim",
            "configure_map_grid",
            "remove_map_grids",
            "list_map_surrounds",
            "add_map_surround",
            "configure_map_surround",
            "set_map_surround_cim",
            "get_data_connection",
            "set_data_connection",
            "replace_data_source",
            "spatial_query_features",
            "spatial_select_features",
            "list_task_items",
            "manage_task",
            "get_workflow_status",
            "search_workflow_jobs",
            "get_workflow_job",
            "control_workflow_job",
            "list_knowledge_graphs",
            "manage_knowledge_graph_investigation",
            "query_knowledge_graph",
            "get_link_chart_state",
            "configure_link_chart",
            "inspect_network_topology_layer",
            "get_framework_state",
            "inspect_framework_command",
            "execute_framework_command",
            "configure_framework_application",
            "manage_framework_pane",
        }
        if action not in allowed_actions:
            raise ValueError(f"Unsupported live bridge action: {action}")

        source_edit_actions = {
            "update_feature_attributes",
            "create_features",
            "delete_features",
            "save_edits",
            "discard_edits",
            "undo_edit",
            "redo_edit",
            "add_attachment",
            "update_attachment",
            "remove_attachments",
        }
        if action in source_edit_actions:
            self._require_source_editing(destructive=action in {"delete_features", "remove_attachments"})

        if action == "control_workflow_job":
            self._require_workflow_actions()
        elif action == "execute_framework_command":
            self._require_framework_ui("command", str(payload.get("command_id", "")))
        elif action == "configure_framework_application":
            self._require_framework_ui("ui")
            if payload.get("active_tab"):
                self._require_framework_ui("tab", str(payload["active_tab"]))
            if payload.get("current_tool"):
                self._require_framework_ui("tool", str(payload["current_tool"]))
            if payload.get("open_backstage_page"):
                self._require_framework_ui("backstage", str(payload["open_backstage_page"]))
        elif action == "manage_framework_pane":
            pane_id = payload.get("pane_id")
            if not pane_id:
                raise ValueError("pane_id is required so the MCP allowlist can be enforced")
            self._require_framework_ui("pane", str(pane_id))

        normalized = dict(payload)
        if action == "add_data":
            normalized["data_paths"] = [str(self._read_path(path)) for path in payload.get("data_paths", [])]
            if not normalized["data_paths"]:
                raise ValueError("add_data requires data_paths")
        elif action == "apply_raster_symbology" and payload.get("colormap_file"):
            path = self._read_path(payload["colormap_file"])
            if path.suffix.lower() != ".clr":
                raise ValueError("colormap_file must end in .clr")
            normalized["colormap_file"] = str(path)
        elif action == "import_layout":
            path = self._read_path(payload["pagx_path"])
            if path.suffix.lower() != ".pagx":
                raise ValueError("pagx_path must end in .pagx")
            normalized["pagx_path"] = str(path)
        elif action == "import_bookmarks":
            path = self._read_path(payload["input_file"])
            if path.suffix.lower() != ".bkmx":
                raise ValueError("input_file must end in .bkmx")
            normalized["input_file"] = str(path)
        elif action == "configure_popup":
            import_value = payload.get("import_popup_path")
            export_value = payload.get("export_popup_path")
            if import_value:
                normalized["import_popup_path"] = str(self._read_path(import_value))
            if export_value:
                normalized["export_popup_path"] = str(self._write_path(export_value))
        elif action == "add_project_item":
            normalized["path"] = str(self._read_path(payload["path"]))
        elif action == "repair_project_item":
            if payload.get("path"):
                normalized["path"] = str(self._read_path(payload["path"], must_exist=False))
            normalized["new_path"] = str(self._read_path(payload["new_path"]))
        elif action == "remove_project_item":
            if payload.get("path"):
                normalized["path"] = str(self._read_path(payload["path"], must_exist=False))
        elif action == "manage_favorite":
            if payload.get("path"):
                normalized["path"] = str(self._read_path(
                    payload["path"],
                    must_exist=payload.get("operation") != "remove",
                ))
        elif action in {"add_presentation_page", "configure_presentation_page"}:
            if payload.get("media_path"):
                normalized["media_path"] = str(self._read_path(payload["media_path"]))
        elif action == "export_presentation":
            output = self._write_path(payload["output_file"])
            if output.suffix.lower() not in {".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".svg", ".svgz", ".eps", ".aix"}:
                raise ValueError("unsupported presentation export file extension")
            normalized["output_file"] = str(output)
        elif action == "save_presentation_file":
            output = self._write_path(payload["output_file"])
            if output.suffix.lower() != ".prsx":
                raise ValueError("output_file must end in .prsx")
            normalized["output_file"] = str(output)
        elif action == "export_attachment":
            normalized["output_file"] = str(self._write_path(payload["output_file"]))
        elif action in {"add_attachment", "update_attachment"}:
            normalized["file_path"] = str(self._read_path(payload["file_path"]))
        elif action == "replace_data_source":
            normalized["data_path"] = str(self._read_path(payload["data_path"]))
        elif action in {"export_layout", "export_map_series", "export_map_view"}:
            output = self._write_path(payload["output_file"])
            if output.suffix.lower() not in {".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".svg", ".svgz"}:
                raise ValueError("unsupported export file extension")
            normalized["output_file"] = str(output)
        elif action == "export_bookmarks":
            output = self._write_path(payload["output_file"])
            if output.suffix.lower() != ".bkmx":
                raise ValueError("output_file must end in .bkmx")
            normalized["output_file"] = str(output)
        elif action == "save_map_file":
            output = self._write_path(payload["output_file"])
            if output.suffix.lower() != ".mapx":
                raise ValueError("output_file must end in .mapx")
            normalized["output_file"] = str(output)
        elif action == "save_layout_file":
            output = self._write_path(payload["output_file"])
            if output.suffix.lower() != ".pagx":
                raise ValueError("output_file must end in .pagx")
            normalized["output_file"] = str(output)
        elif action in {"capture_view_thumbnail", "begin_animation_export"}:
            output = self._write_path(payload["output_file"])
            allowed_extensions = (
                {".png", ".jpg", ".jpeg"}
                if action == "capture_view_thumbnail"
                else {".mp4", ".avi", ".gif"}
            )
            if output.suffix.lower() not in allowed_extensions:
                raise ValueError(
                    f"unsupported {action} output extension: {output.suffix}"
                )
            normalized["output_file"] = str(output)
        elif action == "manage_task":
            operation = str(payload.get("operation", ""))
            if operation in {"import_file", "open_file"}:
                task_file = self._read_path(payload["task_file"])
                if task_file.suffix.casefold() != ".esritasks":
                    raise ValueError("task_file must end in .esriTasks")
                normalized["task_file"] = str(task_file)
            elif operation == "export_item":
                output = self._write_path(payload["output_file"])
                if output.suffix.casefold() != ".esritasks":
                    raise ValueError("output_file must end in .esriTasks")
                normalized["output_file"] = str(output)
        elif action in {"save_project_as", "save_copy"}:
            output = self._write_path(payload["output_aprx"])
            if output.suffix.lower() != ".aprx":
                raise ValueError("output_aprx must end in .aprx")
            normalized["output_aprx"] = str(output)
        else:
            self._validate_embedded_paths(normalized)
        return normalized

    @staticmethod
    def _merge_cim_patch(current: Any, patch: Any) -> Any:
        """Apply JSON Merge Patch semantics without mutating the source value."""
        if not isinstance(patch, dict):
            return patch
        result = dict(current) if isinstance(current, dict) else {}
        for key, value in patch.items():
            if value is None:
                result.pop(key, None)
            else:
                result[key] = ArcGISMCP._merge_cim_patch(result.get(key), value)
        return result

    @staticmethod
    def _cim_changed_paths(before: Any, after: Any, prefix: str = "$") -> list[str]:
        if isinstance(before, dict) and isinstance(after, dict):
            paths: list[str] = []
            for key in sorted(set(before) | set(after)):
                path = f"{prefix}.{key}"
                if key not in before or key not in after:
                    paths.append(path)
                else:
                    paths.extend(ArcGISMCP._cim_changed_paths(before[key], after[key], path))
            return paths
        if isinstance(before, list) and isinstance(after, list):
            return [] if before == after else [prefix]
        return [] if before == after else [prefix]

    @staticmethod
    def _protected_cim_patch_paths(patch: dict[str, Any], prefix: str = "$") -> list[str]:
        protected = {
            "uri",
            "sourceuri",
            "sourceportalurl",
            "dataconnection",
            "datasource",
            "featuretable",
            "layers",
            "standalonetables",
            "elements",
            "pages",
            "mapseries",
            "elementstorageuri",
            "mapframe",
            "mapmemberuri",
            "selectionseturi",
        }
        found: list[str] = []
        for key, value in patch.items():
            path = f"{prefix}.{key}"
            normalized = re.sub(r"[^a-z0-9]", "", key.casefold())
            if normalized in protected:
                found.append(path)
            if isinstance(value, dict):
                found.extend(ArcGISMCP._protected_cim_patch_paths(value, path))
        return found

    def _patch_cim_object(self, arguments: dict[str, Any]) -> dict[str, Any]:
        target_type = str(arguments["target_type"])
        patch = arguments["patch"]
        if not isinstance(patch, dict):
            raise ValueError("patch must be a JSON object")
        if len(json.dumps(patch, ensure_ascii=True)) > 1_000_000:
            raise ValueError("patch exceeds the 1 MB safety limit")

        specs: dict[str, tuple[str, str, tuple[str, ...]]] = {
            "map": ("get_map_cim", "set_map_cim", ("map_name",)),
            "layer": ("get_layer_cim", "set_layer_cim", ("map_name", "layer_name")),
            "map_graphic": (
                "get_map_graphic_cim",
                "set_map_graphic_cim",
                ("map_name", "graphics_layer_name", "element_name"),
            ),
            "layout": ("get_layout_cim", "set_layout_cim", ("layout_name",)),
            "layout_element": (
                "get_layout_element_cim", "set_layout_element_cim", ("layout_name", "element_name")
            ),
            "standalone_table": (
                "get_standalone_table_cim", "set_standalone_table_cim", ("map_name", "table_name")
            ),
            "report": ("get_report_cim", "set_report_cim", ("report_name",)),
            "report_element": (
                "get_report_element_cim", "set_report_element_cim", ("report_name", "element_name")
            ),
            "presentation": (
                "get_presentation_cim", "set_presentation_cim", ("presentation_name",)
            ),
            "presentation_page": (
                "get_presentation_page_cim", "set_presentation_page_cim", ("presentation_name", "page_index")
            ),
            "presentation_page_element": (
                "get_presentation_page_element_cim",
                "set_presentation_page_element_cim",
                ("presentation_name", "page_index", "element_name"),
            ),
        }
        if target_type not in specs:
            raise ValueError(f"Unsupported CIM patch target_type: {target_type}")
        get_action, set_action, selector_names = specs[target_type]
        selectors: dict[str, Any] = {}
        for key in selector_names:
            value = arguments.get(key)
            if key == "map_name" and value is None:
                selectors[key] = None
            elif value is None or value == "":
                raise ValueError(f"{key} is required for target_type={target_type}")
            else:
                selectors[key] = value

        protected_paths = self._protected_cim_patch_paths(patch)
        if protected_paths and not bool(arguments.get("allow_structural_changes", False)):
            raise PermissionError(
                "The CIM patch touches protected structural or data-connection paths: "
                + ", ".join(protected_paths[:50])
                + ". Set allow_structural_changes=true only for an explicitly intended project-structure change."
            )

        current_result = self._run_live(get_action, selectors)
        cim_type = str(current_result.get("cim_type") or "")
        expected = arguments.get("expected_cim_type")
        if expected and str(expected).casefold() != cim_type.casefold():
            raise ValueError(f"CIM type changed: expected {expected}, current {cim_type}")
        current_json = current_result.get("cim_json")
        if not isinstance(current_json, str):
            raise RuntimeError(f"{get_action} did not return cim_json")
        current = json.loads(current_json)
        patched = self._merge_cim_patch(current, patch)
        changed_paths = self._cim_changed_paths(current, patched)
        patched_json = json.dumps(patched, ensure_ascii=False, separators=(",", ":"))

        response: dict[str, Any] = {
            "target_type": target_type,
            "cim_type": cim_type,
            "changed": bool(changed_paths),
            "changed_paths": changed_paths[:2000],
            "changed_path_count": len(changed_paths),
            "protected_paths": protected_paths,
            "dry_run": bool(arguments.get("dry_run", False)),
            "source_data_modified": False,
        }
        if bool(arguments.get("include_cim_json", False)):
            response["cim_json"] = patched_json
        if response["dry_run"] or not changed_paths:
            return response
        if not bool(arguments.get("confirm_apply", False)):
            raise PermissionError("confirm_apply=true is required to apply a CIM patch")

        set_payload = dict(selectors)
        set_payload.update(
            {
                "cim_json": patched_json,
                "confirm_replace_definition": True,
            }
        )
        response["result"] = self._run_live(set_action, set_payload)
        return response

    def call_tool(self, name: str, arguments: dict[str, Any]) -> Any:
        if name == "live_patch_cim_object":
            return self._patch_cim_object(dict(arguments))

        if name == "arcgis_status":
            try:
                return self._run_worker("status", {})
            except Exception as worker_error:
                try:
                    live = self._run_live("status", {})
                except Exception:
                    raise worker_error
                return {
                    "available": True,
                    "execution_mode": "ArcGIS Pro SDK in-process bridge",
                    "project": live.get("project"),
                    "addin_version": live.get("addin_version"),
                    "protocol_version": live.get("protocol_version"),
                    "background_arcpy_available": False,
                    "background_arcpy_error": str(worker_error),
                }

        if name == "search_arcgis_framework_registry":
            return self._search_framework_registry(dict(arguments))

        if name == "search_arcgis_geoprocessing_registry":
            result = self._run_worker(
                "search_gp_tools",
                {
                    "query": str(arguments["query"]),
                    "toolboxes": [str(item) for item in arguments.get("toolboxes", [])],
                    "max_results": min(max(int(arguments.get("max_results", 50)), 1), 500),
                },
            )
            for item in result.get("results") or []:
                item["safety"] = self._geoprocessing_safety(str(item.get("tool") or ""))
            result["source_data_modified"] = False
            return result

        if name == "describe_arcgis_geoprocessing_tool":
            result = self._run_worker(
                "describe_gp_tool",
                {
                    "tool": str(arguments["tool"]),
                    "include_documentation": bool(arguments.get("include_documentation", False)),
                },
            )
            result["safety"] = self._geoprocessing_safety(str(result.get("tool") or ""))
            result["source_data_modified"] = False
            return result

        if name == "search_arcgis_sdk_registry":
            return self._search_sdk_registry(dict(arguments))

        if name == "reload_arcgis_mcp_policy":
            return self._reload_policy_config(dict(arguments))

        if name == "scan_data_folder":
            folder = self._read_path(arguments["folder"])
            return self._run_worker(
                "scan",
                {
                    "folder": str(folder),
                    "recursive": bool(arguments.get("recursive", True)),
                    "max_items": min(max(int(arguments.get("max_items", 500)), 1), 5000),
                },
            )

        if name == "describe_dataset":
            path = self._read_path(arguments["path"])
            return self._run_worker("describe", {"path": str(path)})

        if name == "list_field_values":
            path = self._read_path(arguments["path"])
            return self._run_worker(
                "field_values",
                {
                    "path": str(path),
                    "field": str(arguments["field"]),
                    "max_values": min(max(int(arguments.get("max_values", 200)), 1), 2000),
                },
            )

        if name == "run_geoprocessing_tool":
            tool = str(arguments["tool"])
            self._validate_tool_name(tool)
            args = arguments.get("args", [])
            kwargs = arguments.get("kwargs", {})
            self._validate_embedded_paths(args)
            self._validate_embedded_paths(kwargs)
            workspace = arguments.get("workspace")
            if workspace:
                workspace = str(self._read_path(workspace, must_exist=False))
            return self._run_worker(
                "run_gp",
                {
                    "tool": tool,
                    "args": args,
                    "kwargs": kwargs,
                    "workspace": workspace,
                    "overwrite_output": bool(arguments.get("overwrite_output", False)),
                },
            )

        if name == "project_info":
            aprx = self._read_path(arguments["aprx_path"])
            return self._run_worker("project_info", {"aprx_path": str(aprx)})

        if name == "add_data_to_project_copy":
            source = self._read_path(arguments["source_aprx"])
            output = self._write_path(arguments["output_aprx"])
            data_paths = [str(self._read_path(p)) for p in arguments["data_paths"]]
            return self._run_worker(
                "add_data",
                {
                    "source_aprx": str(source),
                    "output_aprx": str(output),
                    "data_paths": data_paths,
                    "map_name": arguments.get("map_name"),
                },
            )

        if name == "export_layout":
            aprx = self._read_path(arguments["aprx_path"])
            output = self._write_path(arguments["output_file"])
            if output.suffix.lower() not in {".pdf", ".png"}:
                raise ValueError("output_file must end in .pdf or .png")
            return self._run_worker(
                "export_layout",
                {
                    "aprx_path": str(aprx),
                    "layout_name": arguments["layout_name"],
                    "output_file": str(output),
                    "dpi": int(arguments.get("dpi", 300)),
                },
            )

        if name == "live_bridge_status":
            status = self._run_live("status", {})
            status["mcp_safety"] = {
                "allow_destructive_tools": self.allow_destructive,
                "allow_source_editing": self.allow_source_editing,
                "allow_schema_changes": self.allow_schema_changes,
                "allow_external_publishing": self.allow_external_publishing,
                "allow_external_workflow_actions": self.allow_external_workflow_actions,
                "allow_framework_ui_control": self.allow_framework_ui_control,
                "allowed_framework_commands": sorted(self.allowed_framework_commands),
                "allowed_framework_panes": sorted(self.allowed_framework_panes),
                "allowed_framework_tabs": sorted(self.allowed_framework_tabs),
                "allowed_framework_tools": sorted(self.allowed_framework_tools),
                "allowed_backstage_pages": sorted(self.allowed_backstage_pages),
            }
            return status

        if name == "live_capabilities":
            capabilities = self._run_live("capabilities", {})
            capabilities["mcp_safety"] = {
                "allow_destructive_tools": self.allow_destructive,
                "allow_source_editing": self.allow_source_editing,
                "allow_schema_changes": self.allow_schema_changes,
                "allow_external_publishing": self.allow_external_publishing,
                "allow_external_workflow_actions": self.allow_external_workflow_actions,
                "allow_framework_ui_control": self.allow_framework_ui_control,
                "allowed_framework_commands": sorted(self.allowed_framework_commands),
                "allowed_framework_panes": sorted(self.allowed_framework_panes),
                "allowed_framework_tabs": sorted(self.allowed_framework_tabs),
                "allowed_framework_tools": sorted(self.allowed_framework_tools),
                "allowed_backstage_pages": sorted(self.allowed_backstage_pages),
            }
            return capabilities

        if name == "live_project_inventory":
            return self._run_live("project_inventory", {})

        if name == "live_save_project":
            return self._run_live("save_project", {})

        if name == "live_get_metadata":
            return self._run_live("get_metadata", dict(arguments))

        if name == "live_set_metadata":
            return self._run_live("set_metadata", dict(arguments))

        if name == "live_add_data":
            data_paths = [str(self._read_path(path)) for path in arguments["data_paths"]]
            return self._run_live("add_data", {"data_paths": data_paths, "map_name": arguments.get("map_name")})

        if name == "live_create_map":
            return self._run_live("create_map", dict(arguments))

        if name == "live_rename_map":
            return self._run_live("rename_map", dict(arguments))

        if name == "live_duplicate_map":
            return self._run_live("duplicate_map", dict(arguments))

        if name == "live_remove_map":
            return self._run_live("remove_map", dict(arguments))

        if name == "live_open_map":
            return self._run_live("open_map", dict(arguments))

        if name == "live_list_bookmarks":
            return self._run_live("list_bookmarks", dict(arguments))

        if name == "live_create_bookmark":
            return self._run_live("create_bookmark", dict(arguments))

        if name == "live_remove_bookmark":
            return self._run_live("remove_bookmark", dict(arguments))

        if name == "live_zoom_to_bookmark":
            return self._run_live("zoom_to_bookmark", dict(arguments))

        bookmark_routes = {
            "live_configure_bookmark": "configure_bookmark",
            "live_move_bookmark": "move_bookmark",
            "live_import_bookmarks": "import_bookmarks",
            "live_export_bookmarks": "export_bookmarks",
        }
        if name in bookmark_routes:
            action = bookmark_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        if name == "live_get_camera":
            return self._run_live("get_camera", dict(arguments))

        if name == "live_set_camera":
            return self._run_live("set_camera", dict(arguments))

        if name == "live_zoom_full_extent":
            return self._run_live("zoom_full_extent", dict(arguments))

        map_view_routes = {
            "live_get_map_view_state": "get_map_view_state",
            "live_configure_map_view": "configure_map_view",
            "live_get_view_filter_state": "get_view_filter_state",
            "live_configure_view_filters": "configure_view_filters",
            "live_get_active_view_context": "get_active_view_context",
            "live_configure_active_view_context": "configure_active_view_context",
            "live_navigate_map_view": "navigate_map_view",
            "live_transform_view_coordinates": "transform_view_coordinates",
            "live_flash_features": "flash_features",
            "live_show_feature_popup": "show_feature_popup",
            "live_close_feature_popups": "close_feature_popups",
            "live_manage_feature_cache": "manage_feature_cache",
        }
        if name in map_view_routes:
            action = map_view_routes[name]
            return self._run_live(action, dict(arguments))

        if name == "live_capture_view_thumbnail":
            normalized = dict(arguments)
            output = self._write_path(arguments["output_file"])
            if output.suffix.lower() not in {".png", ".jpg", ".jpeg"}:
                raise ValueError("output_file must end in .png, .jpg, or .jpeg")
            normalized["output_file"] = str(output)
            return self._run_live("capture_view_thumbnail", normalized)

        if name == "live_get_map_cim":
            return self._run_live("get_map_cim", dict(arguments))

        if name == "live_set_map_cim":
            return self._run_live("set_map_cim", dict(arguments))

        if name == "live_configure_map_properties":
            return self._run_live("configure_map_properties", dict(arguments))

        if name == "live_save_map_file":
            return self._run_live(
                "save_map_file",
                self._prepare_live_action("save_map_file", dict(arguments)),
            )

        if name == "live_describe_layer":
            return self._run_live("describe_layer", dict(arguments))

        if name == "live_get_layer_cim":
            return self._run_live("get_layer_cim", dict(arguments))

        if name == "live_set_layer_cim":
            return self._run_live("set_layer_cim", dict(arguments))

        specialized_layer_routes = {
            "live_inspect_las_layer": "inspect_las_layer",
            "live_configure_las_layer": "configure_las_layer",
            "live_inspect_voxel_layer": "inspect_voxel_layer",
            "live_configure_voxel_layer": "configure_voxel_layer",
        }
        if name in specialized_layer_routes:
            return self._run_live(specialized_layer_routes[name], dict(arguments))

        if name == "live_get_layer_schema":
            return self._run_live("get_layer_schema", dict(arguments))

        if name == "live_query_features":
            return self._run_live("query_features", dict(arguments))

        if name == "live_summarize_field":
            return self._run_live("summarize_field", dict(arguments))

        if name == "live_get_table_schema":
            return self._run_live("get_table_schema", dict(arguments))

        if name == "live_query_table":
            return self._run_live("query_table", dict(arguments))

        if name == "live_select_features":
            return self._run_live("select_features", dict(arguments))

        if name == "live_get_selection":
            return self._run_live("get_selection", dict(arguments))

        if name == "live_clear_selection":
            return self._run_live("clear_selection", dict(arguments))

        if name == "live_update_feature_attributes":
            self._require_source_editing()
            return self._run_live("update_feature_attributes", dict(arguments))

        if name == "live_create_features":
            self._require_source_editing()
            return self._run_live("create_features", dict(arguments))

        if name == "live_delete_features":
            self._require_source_editing(destructive=True)
            return self._run_live("delete_features", dict(arguments))

        if name == "live_save_edits":
            self._require_source_editing()
            return self._run_live("save_edits", dict(arguments))

        if name == "live_discard_edits":
            self._require_source_editing()
            return self._run_live("discard_edits", dict(arguments))

        if name == "live_undo_edit":
            self._require_source_editing()
            return self._run_live("undo_edit", dict(arguments))

        if name == "live_redo_edit":
            self._require_source_editing()
            return self._run_live("redo_edit", dict(arguments))

        if name == "live_duplicate_layer":
            return self._run_live("duplicate_layer", dict(arguments))

        if name == "live_create_group_layer":
            return self._run_live("create_group_layer", dict(arguments))

        if name == "live_add_standalone_table":
            table_path = self._read_path(arguments["table_path"])
            normalized = dict(arguments)
            normalized["table_path"] = str(table_path)
            return self._run_live("add_standalone_table", normalized)

        if name == "live_manage_standalone_table":
            return self._run_live("manage_standalone_table", dict(arguments))

        if name == "live_remove_standalone_table":
            return self._run_live("remove_standalone_table", dict(arguments))

        standalone_table_cim_routes = {
            "live_get_standalone_table_cim": "get_standalone_table_cim",
            "live_set_standalone_table_cim": "set_standalone_table_cim",
        }
        if name in standalone_table_cim_routes:
            action = standalone_table_cim_routes[name]
            return self._run_live(action, dict(arguments))

        if name == "live_run_geoprocessing_tool":
            tool = str(arguments["tool"])
            self._validate_tool_name(tool)
            args = arguments.get("args", [])
            kwargs = arguments.get("kwargs", {})
            self._validate_embedded_paths(args)
            self._validate_embedded_paths(kwargs)
            return self._run_live(
                "run_geoprocessing",
                {
                    "tool": tool,
                    "args": args,
                    "kwargs": kwargs,
                    "add_outputs_to_map": bool(arguments.get("add_outputs_to_map", True)),
                    "overwrite_output": bool(arguments.get("overwrite_output", False)),
                },
            )

        if name == "live_run_schema_tool":
            tool = self._validate_schema_tool(str(arguments["tool"]))
            args = arguments.get("args", [])
            self._validate_embedded_paths(args)
            return self._run_live(
                "run_geoprocessing",
                {
                    "tool": tool,
                    "args": args,
                    "kwargs": {},
                    "add_outputs_to_map": bool(arguments.get("add_outputs_to_map", False)),
                    "overwrite_output": bool(arguments.get("overwrite_output", False)),
                },
            )

        if name == "live_run_publishing_tool":
            tool = self._validate_publishing_tool(str(arguments["tool"]))
            args = arguments.get("args", [])
            self._validate_embedded_paths(args)
            return self._run_live(
                "run_geoprocessing",
                {
                    "tool": tool,
                    "args": args,
                    "kwargs": {},
                    "add_outputs_to_map": bool(arguments.get("add_outputs_to_map", False)),
                    "overwrite_output": bool(arguments.get("overwrite_output", False)),
                },
            )

        if name == "live_import_layout_template":
            pagx = self._read_path(arguments["pagx_path"])
            if pagx.suffix.lower() != ".pagx":
                raise ValueError("pagx_path must end in .pagx")
            return self._run_live("import_layout", {"pagx_path": str(pagx)})

        if name == "live_set_layout_text":
            return self._run_live("set_layout_text", dict(arguments))

        if name == "live_apply_simple_symbology":
            return self._run_live("apply_symbology", dict(arguments))

        if name == "live_manage_layers":
            return self._run_live("manage_layers", dict(arguments))

        if name == "live_remove_layers_from_map":
            return self._run_live("remove_layers", dict(arguments))

        if name == "live_reorder_layer":
            return self._run_live("reorder_layer", dict(arguments))

        if name == "live_zoom_to_layer":
            return self._run_live("zoom_to_layer", dict(arguments))

        if name == "live_apply_vector_symbology":
            return self._run_live("apply_vector_symbology", dict(arguments))

        if name == "live_configure_visual_variables":
            return self._run_live("configure_visual_variables", dict(arguments))

        if name == "live_apply_raster_symbology":
            return self._run_live("apply_raster_symbology", dict(arguments))

        if name == "live_configure_raster_rendering_rule":
            return self._run_live("configure_raster_rendering_rule", dict(arguments))

        if name == "live_configure_multidimensional_raster":
            return self._run_live("configure_multidimensional_raster", dict(arguments))

        scene_view_routes = {
            "live_list_elevation_surfaces": "list_elevation_surfaces",
            "live_configure_elevation_surface": "configure_elevation_surface",
            "live_configure_layer_elevation": "configure_layer_elevation",
            "live_sample_elevation_surface": "sample_elevation_surface",
            "live_get_elevation_profile": "get_elevation_profile",
            "live_get_animation_state": "get_animation_state",
            "live_control_animation": "control_animation",
        }
        if name in scene_view_routes:
            action = scene_view_routes[name]
            return self._run_live(action, dict(arguments))

        if name == "live_begin_animation_export":
            output = self._write_path(arguments["output_file"])
            if output.suffix.lower() not in {".mp4", ".avi", ".gif"}:
                raise ValueError("output_file must end in .mp4, .avi, or .gif")
            normalized = dict(arguments)
            normalized["output_file"] = str(output)
            return self._run_live("begin_animation_export", normalized)

        if name == "live_create_terrain_stack":
            output = self._write_path(arguments["hillshade_output"])
            if output.suffix.lower() not in {".tif", ".tiff", ".img"}:
                raise ValueError("hillshade_output must end in .tif, .tiff, or .img")
            map_name = arguments.get("map_name")
            dem_layer_name = str(arguments["dem_layer_name"])
            layer_info = self._run_live(
                "describe_layer",
                {"map_name": map_name, "layer_name": dem_layer_name, "include_cim": False},
            )
            source_value = layer_info.get("path")
            if not source_value:
                raise ValueError(f"DEM layer does not expose a local raster path: {dem_layer_name}")
            source = self._read_path(source_value)
            hillshade = self._run_live(
                "run_geoprocessing",
                {
                    "tool": "ddd.HillShade",
                    "args": [
                        str(source),
                        str(output),
                        float(arguments.get("azimuth", 315)),
                        float(arguments.get("altitude", 45)),
                        "SHADOWS" if bool(arguments.get("model_shadows", False)) else "NO_SHADOWS",
                        float(arguments.get("z_factor", 1.0)),
                    ],
                    "kwargs": {},
                    "add_outputs_to_map": False,
                    "overwrite_output": bool(arguments.get("overwrite_output", False)),
                },
            )
            if hillshade.get("failed"):
                raise RuntimeError(f"ArcGIS Pro hillshade failed: {hillshade.get('messages')}")
            added = self._run_live(
                "add_data",
                {"data_paths": [str(output)], "map_name": map_name},
            )
            added_names = added.get("added") or []
            if not added_names:
                raise RuntimeError("Hillshade was created but ArcGIS Pro did not add it to the map.")
            raw_hillshade_name = str(added_names[0])
            hillshade_name = str(arguments.get("hillshade_layer_name", "DEM 山体阴影"))
            self._run_live(
                "manage_layers",
                {
                    "map_name": map_name,
                    "layer_names": [raw_hillshade_name],
                    "name": hillshade_name,
                    "visible": True,
                    "transparency": float(arguments.get("hillshade_transparency", 35)),
                    "blend_mode": str(arguments.get("hillshade_blend_mode", "Multiply")),
                },
            )
            hillshade_style = self._run_live(
                "apply_raster_symbology",
                {
                    "map_name": map_name,
                    "layer_name": hillshade_name,
                    "colorizer": "stretch",
                    "stretch_type": "minimum_maximum",
                    "minimum": 0,
                    "maximum": 255,
                    "color_stops": ["#000000", "#FFFFFF"],
                    "gamma": 1.0,
                    "transparency": float(arguments.get("hillshade_transparency", 35)),
                    "heading": "Hillshade",
                },
            )
            elevation_style = self._run_live(
                "apply_raster_symbology",
                {
                    "map_name": map_name,
                    "layer_name": dem_layer_name,
                    "colorizer": "stretch",
                    "stretch_type": str(arguments.get("stretch_type", "minimum_maximum")),
                    "minimum": float(arguments["minimum"]),
                    "maximum": float(arguments["maximum"]),
                    "color_stops": arguments["color_stops"],
                    "gamma": float(arguments.get("gamma", 1.0)),
                    "transparency": 0,
                    "heading": "Elevation",
                },
            )
            order = self._run_live(
                "reorder_layer",
                {"map_name": map_name, "layer_name": hillshade_name, "position": "top"},
            )
            return {
                "map": map_name or layer_info.get("map"),
                "elevation_layer": dem_layer_name,
                "hillshade_layer": hillshade_name,
                "hillshade_dataset": str(output),
                "source_data_modified": False,
                "hillshade": hillshade,
                "hillshade_style": hillshade_style,
                "elevation_style": elevation_style,
                "order": order,
            }

        if name == "live_configure_labels":
            return self._run_live("configure_labels", dict(arguments))

        label_class_routes = {
            "live_list_label_classes": "list_label_classes",
            "live_configure_label_class": "configure_label_class",
            "live_remove_label_classes": "remove_label_classes",
        }
        if name in label_class_routes:
            action = label_class_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        if name == "live_configure_popup":
            normalized = dict(arguments)
            import_value = arguments.get("import_popup_path")
            export_value = arguments.get("export_popup_path")
            if import_value:
                normalized["import_popup_path"] = str(self._read_path(import_value))
            if export_value:
                normalized["export_popup_path"] = str(self._write_path(export_value))
            return self._run_live("configure_popup", normalized)

        if name == "live_configure_fields":
            return self._run_live("configure_fields", dict(arguments))

        if name == "live_configure_display_filters":
            return self._run_live("configure_display_filters", dict(arguments))

        if name == "live_configure_time":
            return self._run_live("configure_time", dict(arguments))

        if name == "live_get_charts":
            return self._run_live("get_charts", dict(arguments))

        if name == "live_set_charts":
            return self._run_live("set_charts", dict(arguments))

        if name == "live_draw_graphics":
            return self._run_live("draw_graphics", dict(arguments))

        map_graphic_routes = {
            "live_list_map_graphics": "list_map_graphics",
            "live_manage_map_graphics": "manage_map_graphics",
            "live_get_map_graphic_cim": "get_map_graphic_cim",
            "live_set_map_graphic_cim": "set_map_graphic_cim",
        }
        if name in map_graphic_routes:
            return self._run_live(map_graphic_routes[name], dict(arguments))

        if name == "live_create_layout":
            return self._run_live("create_layout", dict(arguments))

        if name == "live_duplicate_layout":
            return self._run_live("duplicate_layout", dict(arguments))

        if name == "live_rename_layout":
            return self._run_live("rename_layout", dict(arguments))

        if name == "live_remove_layout":
            return self._run_live("remove_layout", dict(arguments))

        if name == "live_open_layout":
            return self._run_live("open_layout", dict(arguments))

        if name == "live_configure_layout_page":
            return self._run_live("configure_layout_page", dict(arguments))

        if name == "live_manage_layout_guides":
            return self._run_live("manage_layout_guides", dict(arguments))

        if name == "live_save_layout_file":
            return self._run_live(
                "save_layout_file",
                self._prepare_live_action("save_layout_file", dict(arguments)),
            )

        if name == "live_update_layout_element":
            return self._run_live("update_layout_element", dict(arguments))

        if name == "live_organize_layout_elements":
            return self._run_live("organize_layout_elements", dict(arguments))

        if name == "live_add_layout_picture":
            picture = self._read_path(arguments["picture_path"])
            normalized = dict(arguments)
            normalized["picture_path"] = str(picture)
            return self._run_live("add_layout_picture", normalized)

        if name == "live_add_map_frame":
            return self._run_live("add_map_frame", dict(arguments))

        dynamic_frame_routes = {
            "live_list_dynamic_frames": "list_dynamic_frames",
            "live_add_table_frame": "add_table_frame",
            "live_add_chart_frame": "add_chart_frame",
        }
        if name in dynamic_frame_routes:
            action = dynamic_frame_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        if name == "live_configure_map_frame":
            return self._run_live("configure_map_frame", dict(arguments))

        advanced_cartography_routes = {
            "live_list_extent_indicators": "list_extent_indicators",
            "live_add_extent_indicator": "add_extent_indicator",
            "live_configure_extent_indicator": "configure_extent_indicator",
            "live_remove_extent_indicators": "remove_extent_indicators",
            "live_configure_map_clipping": "configure_map_clipping",
            "live_add_dynamic_text": "add_dynamic_text",
        }
        if name in advanced_cartography_routes:
            action = advanced_cartography_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        if name == "live_create_map_series":
            return self._run_live("create_map_series", dict(arguments))

        map_series_routes = {
            "live_list_map_series": "list_map_series",
            "live_configure_map_series": "configure_map_series",
            "live_remove_map_series": "remove_map_series",
            "live_export_map_series": "export_map_series",
            "live_arrange_layout_elements": "arrange_layout_elements",
            "live_export_map_view": "export_map_view",
        }
        if name in map_series_routes:
            action = map_series_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        if name == "live_get_layout_cim":
            return self._run_live("get_layout_cim", dict(arguments))

        if name == "live_set_layout_cim":
            return self._run_live("set_layout_cim", dict(arguments))

        layout_element_cim_routes = {
            "live_get_layout_element_cim": "get_layout_element_cim",
            "live_set_layout_element_cim": "set_layout_element_cim",
            "live_create_layout_element_from_cim": "create_layout_element_from_cim",
        }
        if name in layout_element_cim_routes:
            action = layout_element_cim_routes[name]
            return self._run_live(action, dict(arguments))

        if name == "live_add_layout_graphics":
            return self._run_live("add_layout_graphics", dict(arguments))

        if name == "live_add_layout_ticks":
            return self._run_live("add_layout_ticks", dict(arguments))

        if name == "live_add_layout_colorbar":
            return self._run_live("add_layout_colorbar", dict(arguments))

        if name == "live_add_raster_colorbar_from_layer":
            return self._run_live("add_raster_colorbar_from_layer", dict(arguments))

        if name == "live_execute_batch":
            commands = []
            for command in arguments["commands"]:
                action = str(command["action"])
                commands.append(
                    {
                        "action": action,
                        "payload": self._prepare_live_action(action, command.get("payload") or {}),
                    }
                )
            return self._run_live(
                "execute_batch",
                {
                    "commands": commands,
                    "continue_on_error": bool(arguments.get("continue_on_error", False)),
                },
            )

        if name == "live_zoom_layout_to_layer":
            return self._run_live("zoom_layout", dict(arguments))

        if name == "live_export_layout":
            output = self._write_path(arguments["output_file"])
            if output.suffix.lower() not in {".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".svg", ".svgz", ".eps", ".aix"}:
                raise ValueError("unsupported layout export file extension")
            payload = dict(arguments)
            payload["output_file"] = str(output)
            payload["dpi"] = int(arguments.get("dpi", 300))
            return self._run_live(
                "export_layout",
                payload,
            )

        if name == "live_list_reports":
            return self._run_live("list_reports", {})

        if name == "live_create_report":
            return self._run_live("create_report", dict(arguments))

        if name == "live_configure_report":
            return self._run_live("configure_report", dict(arguments))

        report_cim_routes = {
            "live_get_report_cim": "get_report_cim",
            "live_set_report_cim": "set_report_cim",
            "live_get_report_element_cim": "get_report_element_cim",
            "live_set_report_element_cim": "set_report_element_cim",
        }
        if name in report_cim_routes:
            action = report_cim_routes[name]
            return self._run_live(action, dict(arguments))

        if name == "live_duplicate_report":
            return self._run_live("duplicate_report", dict(arguments))

        if name == "live_rename_report":
            return self._run_live("rename_report", dict(arguments))

        if name == "live_remove_report":
            return self._run_live("remove_report", dict(arguments))

        if name == "live_export_report_pdf":
            output = self._write_path(arguments["output_file"])
            if output.suffix.lower() != ".pdf":
                raise ValueError("output_file must end in .pdf")
            normalized = dict(arguments)
            normalized["output_file"] = str(output)
            return self._run_live("export_report_pdf", normalized)

        catalog_routes = {
            "live_list_catalog_items": "list_catalog_items",
            "live_add_project_item": "add_project_item",
            "live_remove_project_item": "remove_project_item",
            "live_repair_project_item": "repair_project_item",
            "live_manage_favorite": "manage_favorite",
        }
        if name in catalog_routes:
            action = catalog_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        presentation_routes = {
            "live_list_presentations": "list_presentations",
            "live_create_presentation": "create_presentation",
            "live_get_presentation_cim": "get_presentation_cim",
            "live_set_presentation_cim": "set_presentation_cim",
            "live_get_presentation_page_cim": "get_presentation_page_cim",
            "live_set_presentation_page_cim": "set_presentation_page_cim",
            "live_get_presentation_page_element_cim": "get_presentation_page_element_cim",
            "live_set_presentation_page_element_cim": "set_presentation_page_element_cim",
            "live_duplicate_presentation": "duplicate_presentation",
            "live_rename_presentation": "rename_presentation",
            "live_remove_presentation": "remove_presentation",
            "live_open_presentation": "open_presentation",
            "live_configure_presentation": "configure_presentation",
            "live_add_presentation_page": "add_presentation_page",
            "live_configure_presentation_page": "configure_presentation_page",
            "live_remove_presentation_pages": "remove_presentation_pages",
            "live_export_presentation": "export_presentation",
            "live_save_presentation_file": "save_presentation_file",
        }
        if name in presentation_routes:
            action = presentation_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        attachment_relationship_routes = {
            "live_list_attachments": "list_attachments",
            "live_export_attachment": "export_attachment",
            "live_add_attachment": "add_attachment",
            "live_update_attachment": "update_attachment",
            "live_remove_attachments": "remove_attachments",
            "live_list_relationships": "list_relationships",
            "live_query_related_records": "query_related_records",
        }
        if name in attachment_relationship_routes:
            action = attachment_relationship_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        cartography_routes = {
            "live_list_style_items": "list_style_items",
            "live_list_map_grids": "list_map_grids",
            "live_add_map_grid": "add_map_grid",
            "live_set_map_grid_cim": "set_map_grid_cim",
            "live_configure_map_grid": "configure_map_grid",
            "live_remove_map_grids": "remove_map_grids",
            "live_list_map_surrounds": "list_map_surrounds",
            "live_add_map_surround": "add_map_surround",
            "live_configure_map_surround": "configure_map_surround",
            "live_set_map_surround_cim": "set_map_surround_cim",
        }
        if name in cartography_routes:
            action = cartography_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        data_query_routes = {
            "live_get_data_connection": "get_data_connection",
            "live_set_data_connection": "set_data_connection",
            "live_replace_data_source": "replace_data_source",
            "live_spatial_query_features": "spatial_query_features",
            "live_spatial_select_features": "spatial_select_features",
        }
        if name in data_query_routes:
            action = data_query_routes[name]
            return self._run_live(action, self._prepare_live_action(action, dict(arguments)))

        if name == "live_list_task_items":
            return self._run_live("list_task_items", {})

        if name == "live_manage_task":
            normalized = dict(arguments)
            operation = str(arguments["operation"])
            if operation in {"import_file", "open_file"}:
                task_file = self._read_path(arguments["task_file"])
                if task_file.suffix.casefold() != ".esritasks":
                    raise ValueError("task_file must end in .esriTasks")
                normalized["task_file"] = str(task_file)
            elif operation == "export_item":
                output = self._write_path(arguments["output_file"])
                if output.suffix.casefold() != ".esritasks":
                    raise ValueError("output_file must end in .esriTasks")
                normalized["output_file"] = str(output)
            return self._run_live("manage_task", normalized)

        task_workflow_routes = {
            "live_get_workflow_status": "get_workflow_status",
            "live_search_workflow_jobs": "search_workflow_jobs",
            "live_get_workflow_job": "get_workflow_job",
        }
        if name in task_workflow_routes:
            return self._run_live(task_workflow_routes[name], dict(arguments))

        if name == "live_control_workflow_job":
            self._require_workflow_actions()
            return self._run_live("control_workflow_job", dict(arguments))

        knowledge_network_routes = {
            "live_list_knowledge_graphs": "list_knowledge_graphs",
            "live_manage_knowledge_graph_investigation": "manage_knowledge_graph_investigation",
            "live_query_knowledge_graph": "query_knowledge_graph",
            "live_get_link_chart_state": "get_link_chart_state",
            "live_configure_link_chart": "configure_link_chart",
            "live_inspect_network_topology_layer": "inspect_network_topology_layer",
        }
        if name in knowledge_network_routes:
            return self._run_live(knowledge_network_routes[name], dict(arguments))

        if name == "live_get_framework_state":
            return self._run_live("get_framework_state", dict(arguments))

        if name == "live_inspect_framework_command":
            return self._run_live("inspect_framework_command", dict(arguments))

        if name == "live_execute_framework_command":
            self._require_framework_ui("command", str(arguments["command_id"]))
            return self._run_live("execute_framework_command", dict(arguments))

        if name == "live_configure_framework_application":
            self._require_framework_ui("ui")
            if arguments.get("active_tab"):
                self._require_framework_ui("tab", str(arguments["active_tab"]))
            if arguments.get("current_tool"):
                self._require_framework_ui("tool", str(arguments["current_tool"]))
            if arguments.get("open_backstage_page"):
                self._require_framework_ui("backstage", str(arguments["open_backstage_page"]))
            return self._run_live("configure_framework_application", dict(arguments))

        if name == "live_manage_framework_pane":
            pane_id = arguments.get("pane_id")
            if not pane_id:
                raise ValueError("pane_id is required so the MCP allowlist can be enforced")
            self._require_framework_ui("pane", str(pane_id))
            return self._run_live("manage_framework_pane", dict(arguments))

        application_routes = {
            "live_get_portal_state": "get_portal_state",
            "live_inspect_time_zones": "inspect_time_zones",
            "live_inspect_locators": "inspect_locators",
            "live_geocode_location": "geocode_location",
            "live_get_operation_history": "get_operation_history",
        }
        if name in application_routes:
            return self._run_live(application_routes[name], dict(arguments))

        if name == "live_control_operation_history":
            self._require_source_editing()
            return self._run_live("control_operation_history", dict(arguments))

        if name == "live_geometry_engine":
            return self._run_live("geometry_engine", dict(arguments))

        if name == "live_manage_exploratory_analysis":
            return self._run_live("manage_exploratory_analysis", dict(arguments))

        if name == "live_manage_report_elements":
            return self._run_live("manage_report_elements", dict(arguments))

        if name == "live_manage_presentation_elements":
            media_path = arguments.get("media_path")
            payload = dict(arguments)
            if media_path:
                payload["media_path"] = str(self._read_path(str(media_path)))
            return self._run_live("manage_presentation_elements", payload)

        if name == "live_configure_specialized_renderer":
            return self._run_live("configure_specialized_renderer", dict(arguments))

        if name == "live_inspect_printers":
            return self._run_live("inspect_printers", dict(arguments))

        if name == "live_manage_report_composition":
            return self._run_live("manage_report_composition", dict(arguments))

        if name == "live_configure_presentation_transition":
            return self._run_live("configure_presentation_transition", dict(arguments))

        if name == "live_evaluate_attribute_rules":
            self._require_source_editing()
            return self._run_live("evaluate_attribute_rules", dict(arguments))

        if name == "live_save_project_copy":
            output = self._write_path(arguments["output_aprx"])
            if output.suffix.lower() != ".aprx":
                raise ValueError("output_aprx must end in .aprx")
            return self._run_live(
                "save_copy",
                {
                    "output_aprx": str(output),
                    "overwrite": bool(arguments.get("overwrite", False)),
                },
            )

        if name == "live_save_project_as":
            output = self._write_path(arguments["output_aprx"])
            if output.suffix.lower() != ".aprx":
                raise ValueError("output_aprx must end in .aprx")
            return self._run_live(
                "save_project_as",
                {
                    "output_aprx": str(output),
                    "overwrite": bool(arguments.get("overwrite", False)),
                },
            )

        raise KeyError(f"Unknown tool: {name}")


def response(request_id: Any, result: Any = None, error: dict[str, Any] | None = None) -> dict[str, Any]:
    message: dict[str, Any] = {"jsonrpc": "2.0", "id": request_id}
    if error is not None:
        message["error"] = error
    else:
        message["result"] = result
    return message


def emit(message: dict[str, Any]) -> None:
    encoded = (json.dumps(message, ensure_ascii=True) + "\n").encode("utf-8")
    with EMIT_LOCK:
        sys.stdout.buffer.write(encoded)
        sys.stdout.buffer.flush()


def serve(server: ArcGISMCP) -> None:
    # MCP stdio transport is UTF-8. Reading the binary stream avoids the
    # system GBK code page corrupting Chinese dataset and folder names.
    pending: dict[Any, tuple[threading.Event, concurrent.futures.Future[Any]]] = {}
    pending_lock = threading.Lock()
    executor = concurrent.futures.ThreadPoolExecutor(
        max_workers=min(32, (os.cpu_count() or 4) + 4),
        thread_name_prefix="arcgis-mcp",
    )

    def finish_tool(request_id: Any, future: concurrent.futures.Future[Any]) -> None:
        with pending_lock:
            pending.pop(request_id, None)
        try:
            tool_result = future.result()
            text = json.dumps(tool_result, ensure_ascii=False, indent=2, default=str)
            result = {"content": [{"type": "text", "text": text}], "isError": False}
            emit(response(request_id, result=result))
        except Exception as exc:
            if isinstance(exc, RequestCancelled):
                LOG.info("Tool request cancelled: %s", request_id)
            else:
                LOG.exception("Tool request failed")
            emit(response(request_id, error={
                "code": -32800 if isinstance(exc, RequestCancelled) else -32000,
                "message": str(exc),
                "data": {"type": type(exc).__name__},
            }))

    try:
        input_stream = sys.stdin.buffer
        for raw_bytes in input_stream:
            raw_line = raw_bytes.decode("utf-8").strip()
            if not raw_line:
                continue
            request_id = None
            try:
                request = json.loads(raw_line)
                request_id = request.get("id")
                method = request.get("method")
                params = request.get("params") or {}

                if method == "initialize":
                    result = {
                        "protocolVersion": params.get("protocolVersion", "2024-11-05"),
                        "capabilities": {"tools": {"listChanged": False}},
                        "serverInfo": {"name": "arcgis-pro-mcp", "version": VERSION},
                        "instructions": SERVER_INSTRUCTIONS,
                    }
                elif method == "ping":
                    result = {}
                elif method == "tools/list":
                    result = {"tools": TOOLS}
                elif method == "tools/call":
                    if request_id is None:
                        continue
                    cancellation = threading.Event()
                    future = executor.submit(
                        server.execute_tool,
                        params["name"],
                        params.get("arguments") or {},
                        cancellation,
                    )
                    with pending_lock:
                        pending[request_id] = (cancellation, future)
                    future.add_done_callback(
                        lambda completed, current_id=request_id: finish_tool(
                            current_id, completed
                        )
                    )
                    continue
                elif method == "notifications/cancelled":
                    cancelled_id = params.get("requestId")
                    with pending_lock:
                        pending_request = pending.get(cancelled_id)
                    if pending_request:
                        pending_request[0].set()
                    continue
                elif method == "notifications/initialized":
                    continue
                else:
                    if request_id is None:
                        continue
                    emit(response(request_id, error={
                        "code": -32601,
                        "message": f"Method not found: {method}",
                    }))
                    continue

                if request_id is not None:
                    emit(response(request_id, result=result))
            except Exception as exc:
                LOG.exception("Request failed")
                if request_id is not None:
                    error = {
                        "code": -32000,
                        "message": str(exc),
                        "data": {"type": type(exc).__name__},
                    }
                    emit(response(request_id, error=error))
    finally:
        with pending_lock:
            outstanding = list(pending.values())
        for cancellation, _ in outstanding:
            cancellation.set()
        executor.shutdown(wait=True, cancel_futures=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="ArcGIS Pro MCP server")
    parser.add_argument("--config", type=Path, default=ROOT / "config.json")
    parser.add_argument("--log-level", default="INFO")
    args = parser.parse_args()
    logging.basicConfig(level=getattr(logging, args.log_level.upper(), logging.INFO), stream=sys.stderr)
    serve(ArcGISMCP(args.config))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
