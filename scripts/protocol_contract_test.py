from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import TOOLS


CAPABILITIES_SOURCE = ROOT / "addin" / "ArcGISCodexBridgeAddin" / "BridgeSupport.cs"
DISPATCH_SOURCE = ROOT / "addin" / "ArcGISCodexBridgeAddin" / "BridgeModule.cs"


TOOL_TO_ACTION = {
    "live_bridge_status": "status",
    "live_capabilities": "capabilities",
    "live_project_inventory": "project_inventory",
    "live_save_project": "save_project",
    "live_get_metadata": "get_metadata",
    "live_set_metadata": "set_metadata",
    "live_add_data": "add_data",
    "live_create_map": "create_map",
    "live_rename_map": "rename_map",
    "live_duplicate_map": "duplicate_map",
    "live_remove_map": "remove_map",
    "live_open_map": "open_map",
    "live_list_bookmarks": "list_bookmarks",
    "live_create_bookmark": "create_bookmark",
    "live_remove_bookmark": "remove_bookmark",
    "live_zoom_to_bookmark": "zoom_to_bookmark",
    "live_configure_bookmark": "configure_bookmark",
    "live_move_bookmark": "move_bookmark",
    "live_import_bookmarks": "import_bookmarks",
    "live_export_bookmarks": "export_bookmarks",
    "live_get_camera": "get_camera",
    "live_set_camera": "set_camera",
    "live_zoom_full_extent": "zoom_full_extent",
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
    "live_capture_view_thumbnail": "capture_view_thumbnail",
    "live_get_map_cim": "get_map_cim",
    "live_set_map_cim": "set_map_cim",
    "live_configure_map_properties": "configure_map_properties",
    "live_save_map_file": "save_map_file",
    "live_run_geoprocessing_tool": "run_geoprocessing",
    "live_describe_layer": "describe_layer",
    "live_get_layer_cim": "get_layer_cim",
    "live_set_layer_cim": "set_layer_cim",
    "live_inspect_las_layer": "inspect_las_layer",
    "live_configure_las_layer": "configure_las_layer",
    "live_inspect_voxel_layer": "inspect_voxel_layer",
    "live_configure_voxel_layer": "configure_voxel_layer",
    "live_get_layer_schema": "get_layer_schema",
    "live_query_features": "query_features",
    "live_summarize_field": "summarize_field",
    "live_get_table_schema": "get_table_schema",
    "live_query_table": "query_table",
    "live_select_features": "select_features",
    "live_get_selection": "get_selection",
    "live_clear_selection": "clear_selection",
    "live_update_feature_attributes": "update_feature_attributes",
    "live_create_features": "create_features",
    "live_delete_features": "delete_features",
    "live_save_edits": "save_edits",
    "live_discard_edits": "discard_edits",
    "live_undo_edit": "undo_edit",
    "live_redo_edit": "redo_edit",
    "live_duplicate_layer": "duplicate_layer",
    "live_create_group_layer": "create_group_layer",
    "live_add_standalone_table": "add_standalone_table",
    "live_manage_standalone_table": "manage_standalone_table",
    "live_remove_standalone_table": "remove_standalone_table",
    "live_get_standalone_table_cim": "get_standalone_table_cim",
    "live_set_standalone_table_cim": "set_standalone_table_cim",
    "live_apply_simple_symbology": "apply_symbology",
    "live_manage_layers": "manage_layers",
    "live_remove_layers_from_map": "remove_layers",
    "live_reorder_layer": "reorder_layer",
    "live_zoom_to_layer": "zoom_to_layer",
    "live_apply_vector_symbology": "apply_vector_symbology",
    "live_configure_visual_variables": "configure_visual_variables",
    "live_apply_raster_symbology": "apply_raster_symbology",
    "live_configure_raster_rendering_rule": "configure_raster_rendering_rule",
    "live_configure_multidimensional_raster": "configure_multidimensional_raster",
    "live_list_elevation_surfaces": "list_elevation_surfaces",
    "live_configure_elevation_surface": "configure_elevation_surface",
    "live_configure_layer_elevation": "configure_layer_elevation",
    "live_sample_elevation_surface": "sample_elevation_surface",
    "live_get_elevation_profile": "get_elevation_profile",
    "live_get_animation_state": "get_animation_state",
    "live_control_animation": "control_animation",
    "live_begin_animation_export": "begin_animation_export",
    "live_configure_labels": "configure_labels",
    "live_list_label_classes": "list_label_classes",
    "live_configure_label_class": "configure_label_class",
    "live_remove_label_classes": "remove_label_classes",
    "live_configure_popup": "configure_popup",
    "live_configure_fields": "configure_fields",
    "live_configure_display_filters": "configure_display_filters",
    "live_configure_time": "configure_time",
    "live_get_charts": "get_charts",
    "live_set_charts": "set_charts",
    "live_draw_graphics": "draw_graphics",
    "live_list_map_graphics": "list_map_graphics",
    "live_manage_map_graphics": "manage_map_graphics",
    "live_get_map_graphic_cim": "get_map_graphic_cim",
    "live_set_map_graphic_cim": "set_map_graphic_cim",
    "live_create_layout": "create_layout",
    "live_duplicate_layout": "duplicate_layout",
    "live_rename_layout": "rename_layout",
    "live_remove_layout": "remove_layout",
    "live_open_layout": "open_layout",
    "live_configure_layout_page": "configure_layout_page",
    "live_manage_layout_guides": "manage_layout_guides",
    "live_save_layout_file": "save_layout_file",
    "live_add_map_frame": "add_map_frame",
    "live_list_dynamic_frames": "list_dynamic_frames",
    "live_add_table_frame": "add_table_frame",
    "live_add_chart_frame": "add_chart_frame",
    "live_configure_map_frame": "configure_map_frame",
    "live_list_extent_indicators": "list_extent_indicators",
    "live_add_extent_indicator": "add_extent_indicator",
    "live_configure_extent_indicator": "configure_extent_indicator",
    "live_remove_extent_indicators": "remove_extent_indicators",
    "live_configure_map_clipping": "configure_map_clipping",
    "live_add_dynamic_text": "add_dynamic_text",
    "live_create_map_series": "create_map_series",
    "live_list_map_series": "list_map_series",
    "live_configure_map_series": "configure_map_series",
    "live_remove_map_series": "remove_map_series",
    "live_export_map_series": "export_map_series",
    "live_arrange_layout_elements": "arrange_layout_elements",
    "live_export_map_view": "export_map_view",
    "live_get_layout_cim": "get_layout_cim",
    "live_set_layout_cim": "set_layout_cim",
    "live_get_layout_element_cim": "get_layout_element_cim",
    "live_set_layout_element_cim": "set_layout_element_cim",
    "live_create_layout_element_from_cim": "create_layout_element_from_cim",
    "live_update_layout_element": "update_layout_element",
    "live_organize_layout_elements": "organize_layout_elements",
    "live_add_layout_picture": "add_layout_picture",
    "live_add_layout_graphics": "add_layout_graphics",
    "live_add_layout_ticks": "add_layout_ticks",
    "live_add_layout_colorbar": "add_layout_colorbar",
    "live_add_raster_colorbar_from_layer": "add_raster_colorbar_from_layer",
    "live_import_layout_template": "import_layout",
    "live_set_layout_text": "set_layout_text",
    "live_zoom_layout_to_layer": "zoom_layout",
    "live_export_layout": "export_layout",
    "live_list_reports": "list_reports",
    "live_create_report": "create_report",
    "live_configure_report": "configure_report",
    "live_get_report_cim": "get_report_cim",
    "live_set_report_cim": "set_report_cim",
    "live_get_report_element_cim": "get_report_element_cim",
    "live_set_report_element_cim": "set_report_element_cim",
    "live_duplicate_report": "duplicate_report",
    "live_rename_report": "rename_report",
    "live_remove_report": "remove_report",
    "live_export_report_pdf": "export_report_pdf",
    "live_list_catalog_items": "list_catalog_items",
    "live_add_project_item": "add_project_item",
    "live_remove_project_item": "remove_project_item",
    "live_repair_project_item": "repair_project_item",
    "live_manage_favorite": "manage_favorite",
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
    "live_list_attachments": "list_attachments",
    "live_export_attachment": "export_attachment",
    "live_add_attachment": "add_attachment",
    "live_update_attachment": "update_attachment",
    "live_remove_attachments": "remove_attachments",
    "live_list_relationships": "list_relationships",
    "live_query_related_records": "query_related_records",
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
    "live_get_data_connection": "get_data_connection",
    "live_set_data_connection": "set_data_connection",
    "live_replace_data_source": "replace_data_source",
    "live_spatial_query_features": "spatial_query_features",
    "live_spatial_select_features": "spatial_select_features",
    "live_list_task_items": "list_task_items",
    "live_manage_task": "manage_task",
    "live_get_workflow_status": "get_workflow_status",
    "live_search_workflow_jobs": "search_workflow_jobs",
    "live_get_workflow_job": "get_workflow_job",
    "live_control_workflow_job": "control_workflow_job",
    "live_list_knowledge_graphs": "list_knowledge_graphs",
    "live_manage_knowledge_graph_investigation": "manage_knowledge_graph_investigation",
    "live_query_knowledge_graph": "query_knowledge_graph",
    "live_get_link_chart_state": "get_link_chart_state",
    "live_configure_link_chart": "configure_link_chart",
    "live_inspect_network_topology_layer": "inspect_network_topology_layer",
    "live_get_framework_state": "get_framework_state",
    "live_inspect_framework_command": "inspect_framework_command",
    "live_execute_framework_command": "execute_framework_command",
    "live_configure_framework_application": "configure_framework_application",
    "live_manage_framework_pane": "manage_framework_pane",
    "live_get_portal_state": "get_portal_state",
    "live_inspect_time_zones": "inspect_time_zones",
    "live_inspect_locators": "inspect_locators",
    "live_geocode_location": "geocode_location",
    "live_get_operation_history": "get_operation_history",
    "live_control_operation_history": "control_operation_history",
    "live_geometry_engine": "geometry_engine",
    "live_manage_exploratory_analysis": "manage_exploratory_analysis",
    "live_manage_report_elements": "manage_report_elements",
    "live_manage_presentation_elements": "manage_presentation_elements",
    "live_configure_specialized_renderer": "configure_specialized_renderer",
    "live_inspect_printers": "inspect_printers",
    "live_manage_report_composition": "manage_report_composition",
    "live_configure_presentation_transition": "configure_presentation_transition",
    "live_evaluate_attribute_rules": "evaluate_attribute_rules",
    "live_save_project_copy": "save_copy",
    "live_save_project_as": "save_project_as",
    "live_execute_batch": "execute_batch",
}


def main() -> int:
    source = CAPABILITIES_SOURCE.read_text(encoding="utf-8")
    match = re.search(
        r"Capabilities\s*=\s*\{(?P<body>.*?)\};",
        source,
        flags=re.DOTALL,
    )
    if not match:
        raise RuntimeError("Could not parse BridgeSupport.Capabilities")
    capabilities = set(re.findall(r'"([a-z_]+)"', match.group("body")))
    dispatch_source = DISPATCH_SOURCE.read_text(encoding="utf-8")
    dispatch_match = re.search(
        r"return action switch\s*\{(?P<body>.*?)_ => throw new NotSupportedException",
        dispatch_source,
        flags=re.DOTALL,
    )
    if not dispatch_match:
        raise RuntimeError("Could not parse BridgeModule.DispatchAsync")
    dispatch_actions: set[str] = set()
    for left, right in re.findall(
        r'"([a-z_]+)"(?:\s+or\s+"([a-z_]+)")?\s*=>',
        dispatch_match.group("body"),
    ):
        dispatch_actions.add(left)
        if right:
            dispatch_actions.add(right)
    tool_name_list = [tool["name"] for tool in TOOLS]
    tool_names = set(tool_name_list)

    missing_tools = sorted(set(TOOL_TO_ACTION) - tool_names)
    missing_actions = sorted(set(TOOL_TO_ACTION.values()) - capabilities)
    missing_dispatch = sorted(capabilities - dispatch_actions)
    extra_dispatch = sorted(dispatch_actions - capabilities)
    extra_actions = sorted(capabilities - set(TOOL_TO_ACTION.values()))
    duplicate_tools = sorted({name for name in tool_name_list if tool_name_list.count(name) > 1})
    high_level_live_tools = {
        "live_create_terrain_stack",
        "live_run_schema_tool",
        "live_run_publishing_tool",
        "live_patch_cim_object",
    }
    unmapped_live_tools = sorted(
        {name for name in tool_names if name.startswith("live_")}
        - set(TOOL_TO_ACTION)
        - high_level_live_tools
    )
    if (
        missing_tools
        or missing_actions
        or extra_actions
        or missing_dispatch
        or extra_dispatch
        or duplicate_tools
        or unmapped_live_tools
    ):
        raise RuntimeError(
            "Protocol mismatch: "
            f"missing_tools={missing_tools}, missing_actions={missing_actions}, "
            f"extra_actions={extra_actions}, missing_dispatch={missing_dispatch}, "
            f"extra_dispatch={extra_dispatch}, duplicate_tools={duplicate_tools}, "
            f"unmapped_live_tools={unmapped_live_tools}"
        )

    print(
        f"protocol contract ok: {len(TOOL_TO_ACTION)} live tools, "
        f"{len(capabilities)} add-in actions"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
