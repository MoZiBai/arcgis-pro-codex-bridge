# v0.28.0 — ArcPy 运行时回退与免编译 Windows 安装包

本版本面向 **Windows x64 + ArcGIS Pro 3.6**，重点解决 Codex Windows
沙箱中 ArcPy 原生模块被 Windows Security 阻止加载时的运行问题，并提供
无需 Visual Studio、MSBuild 或 .NET SDK 的预编译安装包。

## 主要变化

- 新增受限的 ArcPy worker 回退：只有直接 ArcPy 进程明确返回 Windows
  Security 错误 225 或等价病毒/潜在不受欢迎软件拦截时，才转交给当前打开的
  ArcGIS Pro Add-in 执行。
- Add-in 只允许当前用户稳定安装目录中的 `runtime\worker.py`，Python 路径从
  正在运行的 ArcGIS Pro 安装目录推导；不能指定任意程序或 worker。
- Add-in 会从稳定安装目录的私有 `config.json` 读取 live queue，升级时可继承
  旧配置，不需要创建全局环境变量。
- 新增预编译 `ArcGISCodexBridgeAddin.esriAddinX`。Release 安装器优先使用该包，
  不调用 Visual Studio 或 MSBuild。
- 安装时校验 Add-in ID、版本和复制后的 SHA-256；Release 同时包含逐文件
  `release-manifest.json` 与 ZIP 的 `.sha256` 文件。
- MCP 仍通过初始化 `instructions` 约束为仅在 ArcGIS/GIS/空间分析/制图任务中
  主动调用；无关任务不应触发。

## 安装

1. 下载并解压 `arcgis-pro-codex-bridge-0.28.0-win-x64.zip`。
2. 在解压目录运行：

   ```powershell
   .\scripts\install.ps1 `
     -AllowedDataRoot 'C:\GISData' `
     -OutputRoot 'C:\GISOutput'
   ```

3. 重启 Codex；启动或重启 ArcGIS Pro 并打开一个项目。
4. 运行 `scripts\status.ps1`。需要同时验证 ArcPy 时运行
   `scripts\doctor.ps1`。

安装器不会读取或修改指定数据根中的数据。源数据编辑、删除、结构修改、发布、
外部工作流动作和通用 Framework UI 控制仍默认关闭。

## 校验下载

将 ZIP 与同名 `.zip.sha256` 放在同一目录，然后运行：

```powershell
$expected = (Get-Content .\arcgis-pro-codex-bridge-0.28.0-win-x64.zip.sha256).Split()[0]
$actual = (Get-FileHash .\arcgis-pro-codex-bridge-0.28.0-win-x64.zip -Algorithm SHA256).Hash
if ($actual -ine $expected) { throw 'Release checksum mismatch' }
```

## 安全边界与兼容性

- 不添加 Defender 排除项、服务、计划任务、防火墙规则或提权 helper。
- ArcPy 普通执行错误不会触发回退。
- 当前仅支持 ArcGIS Pro 3.6。ArcMap / ArcGIS Desktop 10.2–10.8 尚未支持。
- 如 Windows Security 报告 ArcGIS DLL，请先检查 Protection History，并通过
  Esri 官方安装程序修复；不要对整个 ArcGIS 目录添加宽泛排除。
