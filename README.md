# ArcGIS Pro Bridge for Codex

> 通过 MCP、ArcPy 与 ArcGIS Pro SDK Add-in，让 Codex 安全地操作当前
> ArcGIS Pro 项目，无需鼠标键盘自动化。

[![ArcGIS Pro](https://img.shields.io/badge/ArcGIS%20Pro-3.6-2C7AC3)](https://www.esri.com/en-us/arcgis/products/arcgis-pro/overview)
[![Protocol](https://img.shields.io/badge/bridge%20protocol-0.27.3-486581)](docs/live-bridge-protocol-v0.27.3.md)
[![Safety](https://img.shields.io/badge/source%20editing-disabled%20by%20default-2E8B57)](#安全边界)

这个项目让 Codex 通过本地 MCP 调用 ArcGIS Pro 自带的 ArcPy，并通过自动加载的
ArcGIS Pro SDK Add-in 操作当前可见项目。当前源码协议 v0.27.3 支持：

- 扫描指定文件夹中的 GIS 数据；
- 检索本机 ArcGIS Pro SDK 的公开类型、方法、属性、构造函数和枚举，以便系统化扩展桥接，而不开放不受控反射执行；
- 通过受限 GeometryEngine 接口执行内存几何投影、缓冲、叠加、关系、量测、简化和几何变换，不读写数据集；
- 检查门户、时区、定位器和地图/布局/报表/演示的撤销重做历史，并提供不改变地图的地址匹配查询；
- 查看要素类、表格、栅格的字段、坐标系、范围和记录数；
- 调用常用 ArcPy 地理处理工具；
- 读取 `.aprx` 中的地图、图层和布局；
- 复制工程并添加数据，避免直接覆盖原工程；
- 将布局导出为 PDF 或 PNG。
- 实时开关、排序和调整图层透明度；
- 矢量唯一值/分级渲染和栅格拉伸/分级/晕渲；
- 标签、地图图形、参数化布局、外框坐标刻度和颜色条；
- 通过批处理命令一次完成有序制图流程。
- 图层检查、重命名、复制、分组和混合模式；
- 从真实栅格渲染器生成按数值比例定位的颜色条；
- 自动创建“高程设色 + 独立山体阴影”的非破坏性地形图层栈。
- 项目盘点和显式保存，地图创建、复制、重命名、删除和打开；
- 地图相机、全图范围和书签；
- 图层与独立表字段结构、有限查询、统计和选择；
- 布局复制、图片元素、多地图框、元素分组和地图系列；
- 项目项元数据读取与受控写入、已有报表管理和 PDF 导出；
- PDF、PNG、JPEG、TIFF、SVG、SVGZ、EPS 和 AIX 布局/活动视图导出。
- Catalog 项目项、文件夹/数据库/工具箱/样式/服务器连接引用和收藏夹管理；
- 演示文稿创建、复制、页面管理、打开、导出和 PRSX 保存；
- 附件只读盘点与导出、默认关闭的受控附件编辑，以及关系类查询。
- 原生经纬网/测量网/参考网格的样式检索、添加、盘点、删除和 CIM 配置；
- 图例、指北针和比例尺的独立添加、重定位、重设样式、重连地图框和 CIM 配置。
- 图层数据连接读取、受控替换和按工作空间批量修复数据源；
- 基于包络、多边形或参考图层的空间查询与空间选择。
- 原生动态表格框和图表框，以及对应地图成员绑定盘点；
- 多标注类的创建、独立配置、Standard/Maplex 放置和受控移除；
- 报表创建、字段/分组/统计/页面/节配置；
- 原生经纬网和测量网的间隔、内部线、标注及刻度类型化配置。
- 活动视图的时间、数值范围和楼层过滤，以及完整地图图形元素管理；
- LAS 数据集图层盘点/显示过滤与体素变量、渲染器、等值面和体块盘点配置。
- 活动视图/TOC 选择状态、原生导航、要素闪烁和弹窗、显示缓存及视图缩略图导出。
- Task Assistant 任务盘点/打开/导入导出与 Workflow Manager 作业查询和独立门控的步骤控制；
- Knowledge Graph 数据模型、openCypher/搜索、链接图配置，以及拓扑/Utility Network 等网络图层盘点。
- ArcGIS Pro Framework 通用状态、命令检查，以及默认关闭、精确允许列表保护的命令、选项卡、工具、后台页面和窗格控制，便于以后通过配置覆盖长尾 UI 功能而无需再次编译 Add-in。
- 只读检索本机 ArcGIS Pro DAML 与命令搜索注册表，支持中英文标题、搜索词、扩展来源、精确 Framework ID 和执行风险提示。
- 显式热重载 MCP 安全开关和 Framework 精确允许列表；审核新 ID 后无需重启 ArcGIS Pro 或 MCP 进程，数据路径和运行时位置不会被热重载。
- 检索本机 1,977 个 ArcPy 地理处理工具，并读取中文参数名、输入输出方向、类型、默认值、过滤条件和安全门；无需为每个分析工具重新编译 Add-in。
- 原生布局参考线的检查、添加、替换、移除和清空，以及地图/视图客户区/屏幕坐标转换。
- 活动三维场景的 Viewshed、ViewDome 和 LineOfSight 探索分析检查、新增和显式清理；
- 通过原生 ReportElementFactory 创建报表段落、字段值/统计、动态图、附件框、图表框和地图框；
- 通过原生 PresentationElementFactory 创建演示页文字、图片、形状、箭头、分组和取消分组；
- TIN、Terrain 和点云场景图层的专用渲染器及点云 Eye-Dome Lighting 配置；
- 只读检查布局打印机偏好和活动地图视图打印就绪状态，不发送打印任务。
- 报表补充布局页、子报表、表达式与页面替换，以及演示页转场、方向、时长和演讲者备注；
- 属性规则评估桥接（可能写入验证错误表，因此默认禁用并要求源编辑开关与显式确认）。

## 架构

```text
Codex
  │ MCP (stdio)
  ▼
Python MCP server ── ArcPy worker
  │ bounded local queue
  ▼
ArcGIS Pro SDK Add-in ── 当前打开的 ArcGIS Pro 项目
```

- MCP server 负责工具注册、路径校验和安全策略；
- ArcPy worker 负责数据扫描、描述和地理处理；
- SDK Add-in 在 ArcGIS Pro 进程内操作当前地图、布局、报表和演示；
- 源数据编辑、字段结构修改、外部发布、工作流动作和通用 UI 控制均有独立安全门，默认关闭。

## 环境要求

- Windows 10/11；
- ArcGIS Pro 3.6；
- Python 3.10+（用于 MCP server；ArcPy 使用 ArcGIS Pro 自带环境）；
- .NET 8 SDK 与 Visual Studio 2022 Build Tools（仅构建 Add-in 时需要）；
- Codex CLI 或支持 stdio MCP 的客户端。

## 快速开始

1. 克隆仓库并创建本地配置：

   ```powershell
   Copy-Item .\config.example.json .\config.json
   ```

2. 编辑 `config.json`，设置 ArcGIS Python、允许读取的数据目录、输出目录和桥接队列。不要提交该文件。

3. 构建并安装 Add-in：

   ```powershell
   .\scripts\build-addin.ps1
   .\scripts\install-addin.ps1
   ```

   如果 ArcGIS Pro 不在默认目录，可设置 `ARCGIS_PRO_INSTALL_DIR`，或向脚本传入 `-ArcGISProInstallDir`。

4. 注册 MCP：

   ```powershell
   .\scripts\register-mcp.ps1
   ```

5. 重启 ArcGIS Pro，打开项目；随后新建 Codex 任务并调用 `arcgis_status` / `live_bridge_status` 检查连接。

> 仓库不包含 GIS 数据、生成地图、真实配置、运行队列或编译产物。它们均由 `.gitignore` 排除。

## 快速测试

只测试 MCP 协议，不调用 ArcPy：

```powershell
python .\scripts\smoke_test.py
python .\scripts\protocol_contract_test.py
python .\scripts\version_contract_test.py
python .\scripts\safety_contract_test.py
python .\scripts\cim_patch_contract_test.py
python .\scripts\framework_registry_contract_test.py
python .\scripts\geoprocessing_registry_contract_test.py
python .\scripts\runtime_safety_contract_test.py
python .\scripts\policy_reload_contract_test.py
python .\scripts\exploratory_analysis_contract_test.py
python .\scripts\document_element_factory_contract_test.py
python .\scripts\specialized_rendering_contract_test.py
python .\scripts\advanced_document_contract_test.py
python .\scripts\attribute_rule_evaluation_contract_test.py
```

同时测试 ArcPy：

```powershell
python .\scripts\smoke_test.py --with-arcpy
```

使用全新生成的合成数据测试地理处理，不读取现有 GIS 数据：

```powershell
python .\scripts\integration_test.py
```

## 手动接入 Codex

可以使用 Codex CLI 注册 stdio MCP：

```powershell
codex mcp add arcgis-pro -- python .\src\arcgis_mcp\server.py --config .\config.json
```

也可以在 Codex 的 `config.toml` 中配置：

```toml
[mcp_servers."arcgis-pro"]
command = "python"
args = [
  "C:\\path\\to\\arcgis-pro-codex-bridge\\src\\arcgis_mcp\\server.py",
  "--config",
  "C:\\path\\to\\arcgis-pro-codex-bridge\\config.json",
]
```

配置后重启 Codex 或新建任务，让新的 MCP 工具被加载。

插件与 MCP 的实时命令必须通过 `live_capabilities` 握手确认。协议说明见
[`docs/live-bridge-protocol-v0.27.3.md`](docs/live-bridge-protocol-v0.27.3.md)，完整能力盘点见
[`docs/capability-matrix.md`](docs/capability-matrix.md)，本机 SDK 缺口审计见
[`docs/sdk-coverage-audit-v0.27.md`](docs/sdk-coverage-audit-v0.27.md)。

也可以在受信任的本地项目中创建 `.codex/config.toml`；该机器相关文件不随仓库发布。

## 使用方式

以后可以直接对 Codex 说：

> 数据在配置允许的 `project-a` 目录。先检查数据和坐标系，把道路缓冲 500 米，与居民地点叠加统计，结果放到输出目录；再复制我的模板 `.aprx`，加入结果图层并导出布局 PDF。不要修改源数据。

Codex 会先调用 `scan_data_folder` 和 `describe_dataset`，再组合 `run_geoprocessing_tool`、`add_data_to_project_copy` 和 `export_layout`。

## 安全边界

- MCP 只能访问 `config.json` 的 `allowed_data_roots` 和 `output_root`；
- 输出文件必须位于 `output_root`；
- 删除、清空等破坏性 ArcPy 工具默认禁用；
- 通用 ArcPy 执行器同样执行源数据修改、字段结构修改和发布安全门，不能绕过专用受控接口；
- 工程修改默认通过 `saveACopy` 写到新 `.aprx`。
- Workflow Manager 远程步骤变更由 `allow_external_workflow_actions` 独立控制，默认关闭。
- Framework 通用 UI 控制由 `allow_framework_ui_control` 总开关及命令、窗格、选项卡、工具和后台页面的精确 ID 允许列表共同保护，默认全部关闭。
- `config.json`、数据、输出、队列、日志、本地运行时和 Add-in 构建产物不会进入版本控制。

## 关于“打开 ArcGIS Pro 后实时操作”

项目现在包含自动加载的 ArcGIS Pro SDK Add-in，位置见
[addin/README.md](addin/README.md)。安装一次并重启 ArcGIS Pro 后，用户只需
打开 Pro 和一个项目，Codex 即可通过 `live_*` 工具操作当前地图，无需再运行
Python Toolbox，也不使用鼠标键盘模拟。

旧的 Python Toolbox 保留为诊断后备方案。整体设计见
[docs/live-bridge-design.md](docs/live-bridge-design.md)。
