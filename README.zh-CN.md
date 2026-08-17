<div align="right">

<a href="README.md">English</a> | 简体中文

</div>

# ArcGIS Pro Bridge for Codex

通过本地 MCP、ArcPy 和 ArcGIS Pro SDK Add-in 集成 Codex 与当前打开的
ArcGIS Pro 项目，无需鼠标键盘自动化。

[![ArcGIS Pro](https://img.shields.io/badge/ArcGIS%20Pro-3.6-2C7AC3)](https://www.esri.com/en-us/arcgis/products/arcgis-pro/overview)
[![Protocol](https://img.shields.io/badge/bridge%20protocol-0.29.0-486581)](docs/live-bridge-protocol-v0.29.0.md)
[![CI](https://github.com/MoZiBai/arcgis-pro-codex-bridge/actions/workflows/ci.yml/badge.svg)](https://github.com/MoZiBai/arcgis-pro-codex-bridge/actions/workflows/ci.yml)
[![Safety](https://img.shields.io/badge/source%20editing-disabled%20by%20default-2E8B57)](#安全边界)
[![License](https://img.shields.io/badge/license-Apache--2.0-D22128)](LICENSE)

当前源码提供 253 个 MCP 工具，其中 236 个工具通过 Add-in 操作当前 ArcGIS Pro
会话。服务器可作为标准 stdio MCP 注册到 Codex，也可供其他兼容 MCP 的客户端使用。

## 主要能力

- 数据发现与描述：扫描文件夹，读取矢量、栅格和表格的字段、坐标系、范围与记录数。
- 地理处理：调用受控的 ArcPy 工具，并检索本机 1,000 余个地理处理工具的参数与安全信息。
- 项目与地图：盘点项目，创建、复制、重命名和打开地图，管理图层、独立表、书签和相机。
- 数据查询：有限属性查询、统计、选择、空间查询、关系类和附件盘点。
- 符号与标注：矢量和栅格渲染、视觉变量、显示过滤、Standard/Maplex 标注与图层高程。
- 地形与三维：高程设色、独立山体阴影、LAS/体素、场景高程面和探索分析。
- 学术制图：布局、地图框、原生图例、比例尺、指北针、经纬网、颜色条、范围指示器和地图系列。
- 导出：PDF、PNG、JPEG、TIFF、SVG、SVGZ、EPS、AIX、报表、演示和地图系列。
- 高级对象：Catalog 项、报表、演示、Task Assistant、Workflow Manager、Knowledge Graph 和 CIM 对象。
- 扩展发现：只读检索 ArcGIS Pro SDK、DAML/Framework 和地理处理注册表。

完整能力列表见 [capability-matrix.md](docs/capability-matrix.md)。

## 架构

```text
Codex 或其他 MCP 客户端
  │ stdio MCP
  ▼
Python MCP server ── ArcPy worker
  │ bounded local queue
  ▼
ArcGIS Pro SDK Add-in ── 当前打开的 ArcGIS Pro 项目
```

- MCP server 负责工具定义、路径验证、配置和安全策略。
- ArcPy worker 负责数据扫描、数据描述及后台地理处理。
- SDK Add-in 在 ArcGIS Pro 进程内操作当前项目和界面对象。
- Add-in 自动加载并处理本地队列，不需要 Python Toolbox 或额外启动步骤。

当 Codex Windows 沙箱因 Windows Security 错误 225 无法加载 ArcPy 原生模块时，
服务器可以把同一条已验证的 worker 请求交给当前 ArcGIS Pro 进程启动的受限子进程。
该回退不接受任意可执行文件或任意 worker 路径，也不会添加 Defender 排除项。

## 系统要求

- Windows 10 或 Windows 11 x64
- ArcGIS Pro 3.6
- Python 3.10 或更高版本（用于 MCP server）
- Codex 或其他支持 stdio MCP 的客户端

GitHub Release 安装不需要 Visual Studio、MSBuild 或 .NET SDK。只有从源码重新构建
ArcGIS Pro Add-in 时才需要 .NET 8 SDK 和 Visual Studio 2022 Build Tools。

ArcMap / ArcGIS Desktop 10.2–10.8 使用不同的 Python、ArcObjects 和 Add-in 架构，
当前版本不支持。

## 安装

推荐从 GitHub Releases 下载：

```text
arcgis-pro-codex-bridge-0.29.0-win-x64.zip
arcgis-pro-codex-bridge-0.29.0-win-x64.zip.sha256
```

验证 SHA-256 后解压，在 PowerShell 中运行：

```powershell
.\scripts\install.ps1 `
  -AllowedDataRoot 'C:\GISData' `
  -OutputRoot 'C:\GISOutput'
```

安装器会：

1. 检查 ArcGIS Pro 3.6 与系统 Python。
2. 将 MCP 运行时安装到 `%LOCALAPPDATA%\ArcGISCodexBridge`。
3. 生成机器私有的安全配置、HMAC 密钥和 Windows ACL。
4. 校验并安装预编译 `.esriAddinX`。
5. 在用户级 `~/.codex/config.toml` 注册 `arcgis-pro` MCP。

如果 ArcGIS Pro 安装在非默认目录，可传入 `-ArcGISProInstallDir`。安装完成后重启
Codex；Add-in 发生变化时还需要重启 ArcGIS Pro。

详细参数和生命周期说明见 [installation.md](docs/installation.md)。

## 状态与诊断

常规检查不会导入 ArcPy：

```powershell
.\scripts\status.ps1
```

完整运行时诊断会同时测试 ArcPy：

```powershell
.\scripts\doctor.ps1
```

诊断内容包括安装清单、版本、全局 MCP 注册、Add-in、服务器初始化、工具发现和
ArcPy 运行时。Windows Security 报告 ArcGIS DLL 时，应优先检查 Protection History
并通过 Esri 官方安装程序修复，不应对整个 ArcGIS 目录添加宽泛排除。

## 配置

使用默认安装根时，私有配置位于：

```text
%LOCALAPPDATA%\ArcGISCodexBridge\config\config.json
```

使用 `-InstallRoot` 时，配置位于 `<InstallRoot>\config\config.json`；固定的
用户级 locator 会引导 Add-in 找到该自定义安装根。

主要配置项：

| 配置项 | 作用 | 默认值 |
| --- | --- | --- |
| `allowed_data_roots` | 允许读取的数据根目录 | 安装目录内的 `data` |
| `output_root` | 所有派生输出的根目录 | 安装目录内的 `outputs` |
| `live_bridge_queue` | MCP 与 Add-in 的本地队列 | 安装目录内的 `queue` |
| `live_bridge_hmac_key_file` | 队列消息认证使用的私有 HMAC 密钥 | 安装目录内的 `config/live-bridge.key` |
| `include_debug_details` | 在私有诊断详情/日志中记录完整 Add-in 异常 | `false` |
| `allow_source_editing` | 允许修改源数据 | `false` |
| `allow_destructive_tools` | 允许删除或清空操作 | `false` |
| `allow_schema_changes` | 允许字段和结构修改 | `false` |
| `allow_external_publishing` | 允许外部发布 | `false` |
| `allow_framework_ui_control` | 允许通用 Framework UI 控制 | `false` |

开发配置模板见 [config.example.json](config.example.json)。安全策略可显式热重载，
但数据路径和运行时位置需要重启 MCP 才会生效。

## 安全边界

- 只允许访问 `allowed_data_roots`，派生文件必须写入 `output_root`。
- 删除、清空、源数据编辑、结构修改、发布和外部工作流动作默认关闭。
- 通用 ArcPy 执行器与专用工具使用相同安全门，不能绕过策略。
- Framework 控制需要总开关和精确 ID 允许列表同时授权。
- Job、Result 和 Cancellation 队列消息全部使用 HMAC-SHA256 认证。
- 安装器关闭私有配置、签名密钥和队列的继承 ACL，只保留当前用户、SYSTEM 和
  Administrators 的访问权限。
- 对外 MCP 错误始终隐藏绝对路径；只有显式启用 `include_debug_details` 才在私有
  Add-in 诊断详情/日志中记录完整异常。
- 工程文件修改优先写入新的 `.aprx` 副本。
- ArcPy 安全回退仅响应明确的 Windows Security 加载错误；普通异常不会触发回退。
- 回退 worker 固定为稳定安装目录中的 `runtime\worker.py`，Python 固定来自当前
  ArcGIS Pro 安装目录。
- 不创建 Defender 排除项、服务、计划任务、防火墙规则或提权 helper。
- `config.json`、GIS 数据、输出、队列、日志和构建产物均被版本控制排除。

## 从源码构建

从源码安装且没有 `prebuilt` 包时，安装器会构建 Add-in：

```powershell
.\scripts\install.ps1 `
  -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

也可以分别执行开发脚本：

```powershell
.\scripts\build-addin.ps1
.\scripts\install-addin.ps1
.\scripts\register-mcp.ps1
```

构建 GitHub Release 安装包：

```powershell
.\scripts\build-release.ps1 `
  -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

输出位于 `dist`，包含版本化目录、ZIP、逐文件 SHA-256 清单和 ZIP 校验文件。

## 测试

核心协议和安全测试：

```powershell
python .\scripts\protocol_contract_test.py
python .\scripts\mcp_protocol_contract_test.py
python .\scripts\version_contract_test.py
python .\scripts\safety_contract_test.py
python .\scripts\runtime_safety_contract_test.py
python .\scripts\arcpy_worker_fallback_contract_test.py
python .\scripts\queue_and_concurrency_contract_test.py
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\installation_contract_test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\installation_lifecycle_test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release_lifecycle_test.ps1
```

Release 生命周期测试在临时目录中完成解压、逐文件哈希验证、隐私扫描、无 MSBuild
安装、253 工具发现和卸载，不写入真实 GIS 数据或实际 Add-in 目录。

## 卸载

```powershell
.\scripts\uninstall.ps1
```

默认保留 ArcGIS Pro Add-in。需要一并移除时：

```powershell
.\scripts\uninstall.ps1 -RemoveAddin
```

## 文档

- [安装与生命周期](docs/installation.md)
- [v0.29.0 协议](docs/live-bridge-protocol-v0.29.0.md)
- [实时桥接设计](docs/live-bridge-design.md)
- [能力矩阵](docs/capability-matrix.md)
- [SDK 覆盖审计](docs/sdk-coverage-audit-v0.27.md)
- [v0.29.0 Release 说明](docs/release-v0.29.0.md)

## 许可证

版权所有 © 2026 MoZiBai。

本项目采用 [Apache License 2.0](LICENSE) 开源许可证。

ArcGIS Pro 与 ArcGIS Pro SDK 是 Esri 产品，仍受 Esri 自身许可条款约束；它们只是
本项目的依赖，并不因本仓库采用 Apache-2.0 而被重新许可。
