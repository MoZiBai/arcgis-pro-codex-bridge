# ArcGIS Pro in-app bridge

这个桥接在 ArcGIS Pro 内运行，使用 `arcpy.mp.ArcGISProject("CURRENT")` 操作当前项目。它不模拟鼠标或键盘。

## 启动

1. 在 ArcGIS Pro 中打开或新建一个项目；项目至少包含一张地图。
2. 在“目录”窗格中添加本仓库的 `bridge` 文件夹连接。
3. 展开 `ArcGISCodexBridge.pyt`，运行 **Start Codex Bridge**。
4. 保持该工具处于运行状态。要停止桥接，取消该地理处理任务即可。

启动后，先通过 MCP 调用 `live_bridge_status` 检查当前项目、地图和布局，确认桥接已经连接。MCP 的 `live_*` 工具会直接操作当前项目：

- `live_bridge_status`：查看当前项目、地图和布局；
- `live_add_data`：把数据加载到当前地图；
- `live_run_geoprocessing_tool`：在 Pro 内运行地理处理并把结果加到地图；
- `live_import_layout_template`：导入 `.pagx` 布局模板；
- `live_set_layout_text`：修改命名文本元素；
- `live_zoom_layout_to_layer`：将地图框缩放到图层；
- `live_export_layout`：导出 PDF/PNG；
- `live_save_project_copy`：另存项目副本。

这个 Python Toolbox 是可立即使用的 MVP。正式版本可将同一任务协议移入 ArcGIS Pro SDK Add-in，从而随 Pro 启动、提供按钮/状态面板，并避免长期运行的地理处理工具。
