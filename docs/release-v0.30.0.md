# v0.30.0 — Project creation, native legends and task tracking

Windows x64 · ArcGIS Pro 3.6 · Apache-2.0

## 更新 / Changes

- `live_create_blank_project` creates an editable native `.aprx` even when no
  project is open. It requires an explicit output path and verifies the actual
  saved location. Optional initialization creates a map, layout, map frame and
  data references. Results report the support folder, default GDB and toolbox.
  无项目时可创建原生项目；必须指定路径，并校验实际保存位置。
- `create_project_from_template` initializes a staging copy and promotes it
  after success. Explicit overwrite retains a `.previous` backup. Results
  include before/after inventories and a change summary.
  模板副本采用 staging 创建，支持覆盖备份及项目变更清单。
- Native legends support layer filtering, ordering and item display options,
  with a transparent background by default.
  原生图例支持筛选、排序和显示项控制，默认背景透明。
- `live_validate_layout` adds basic geometry checks and optional export gating.
  新增基础布局检查及可选的导出前拦截。
- Job status, history, cooperative cancellation, MCP phase progress and
  output-root artifact inventories are available through the MCP server.
  支持任务状态、历史、协作式取消、阶段进度和输出清单。
- The diagnostic probe now handles notifications, pagination, response IDs,
  tool errors, stderr draining and deadlines. Registration tests cover legacy
  transport-setting replacement.
  改进 MCP 诊断的通知、分页、错误和超时处理，并补充旧注册项替换测试。

## 限制 / Known limitations

Layout checks remain a geometry-check prototype. Point text and external tick
labels can report false zero-size errors. Bounding-box overlap does not prove
that useful map content is obstructed. Leader-line collisions and visual balance
still require preview inspection. Export validation is opt-in and defaults to
`validate_before_export=false`; this release does not provide automatic academic
layout planning or repair.

布局检查仍为几何检查原型。点文字和外部刻度可能误报零尺寸，矩形重叠不能可靠判断
有效地图内容是否被遮挡。指示线碰撞和视觉重心仍需检查预览，导出前验证默认关闭。
本版不提供自动学术排版或自动修复。

Progress reports server phases, not ArcGIS-native tool percentages or an ETA.
History is process-local and resets on restart. Cancellation is cooperative.
Transactional staging applies to template copies; failed blank SDK creation
does not restore the previously open project and may retain its new support
folder for inspection.

进度为服务阶段进度，没有原生工具百分比和剩余时间估算；历史随进程退出清空，取消
为协作式。事务式 staging 适用于模板副本；SDK 空白项目创建失败时不会恢复之前的
项目，可能保留新建的支持目录供检查。

## 安装 / Installation

Verify `arcgis-pro-codex-bridge-0.30.0-win-x64.zip.sha256`, extract the matching
ZIP, then run in PowerShell:

```powershell
.\scripts\install.ps1 `
  -AllowedDataRoot 'C:\GISData' `
  -OutputRoot 'C:\GISOutput'
```

The Release ZIP includes a precompiled Add-in; installation does not require
Visual Studio, MSBuild or the .NET SDK. Upgrade the runtime and Add-in together,
then restart the MCP client and ArcGIS Pro. ArcMap / ArcGIS Desktop 10.x remain
unsupported. Source editing and destructive operations stay disabled by default.

安装包包含预编译 Add-in，无需 Visual Studio、MSBuild 或 .NET SDK。runtime 与
Add-in 应配套升级，之后重启 MCP 客户端及 ArcGIS Pro。仍仅支持 ArcGIS Pro 3.6，
源数据编辑和破坏性操作默认关闭。

## 验证 / Validation

- Release Add-in build: 0 errors, 0 warnings; source and package versions match.
- 19 Python source contracts, including 12 diagnostic-probe test cases.
- PowerShell installation contracts and isolated installation lifecycle.
- Release ZIP exact file inventory, per-file hashes, privacy scan (including
  Add-in contents), precompiled installation, MCP initialization and discovery
  of 259 tools, late-failure rollback and exact uninstall.

These checks verify the build, protocol and isolated installation. They do not
constitute visual approval of generated maps or a fresh live-session load test
of this versioned Add-in.

上述验证覆盖构建、协议和隔离安装，不代表生成地图的视觉质量验收；本次发布验证
未将该版本 Add-in 替换到实际安装目录并执行新一轮会话加载测试。
