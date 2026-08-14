# v0.28.2 — 安装一致性与策略维护版

本版本面向 **Windows x64 + ArcGIS Pro 3.6**，不增加 MCP 工具数量，集中修复
安装生命周期、状态隐私和批处理策略一致性。

## 主要变化

- 安装清单记录 Add-in 的准确目录、文件、版本和 SHA-256；状态检查会离线验证。
- 卸载先校验准确路径、ID 与哈希，再移除属于当前安装根的 MCP 注册和 Add-in；校验失败时不产生部分卸载。
- 安装后期失败时回滚 runtime、私有配置、清单、MCP 注册、locator 和 Add-in。
- `-SkipAddin` 会拒绝留下与 MCP 版本不一致的现有 Add-in。
- `arcgis_status` 不再返回安装用户名、安装源目录、安装时间和完整 Python 路径。
- `live_execute_batch` 与直接工具共用同一动作范围、路径处理和源编辑安全门。

## 安装

下载并解压 `arcgis-pro-codex-bridge-0.28.2-win-x64.zip`，验证同名
`.zip.sha256` 后运行：

```powershell
.\scripts\install.ps1 `
  -AllowedDataRoot 'C:\GISData' `
  -OutputRoot 'C:\GISOutput'
```

完成后重启 Codex，并重启 ArcGIS Pro 以加载 v0.28.2 Add-in。安装器不会读取
或修改允许数据目录内的 GIS 数据，也不会添加 Defender 排除项。

## 兼容性

当前仅支持 ArcGIS Pro 3.6。ArcMap / ArcGIS Desktop 10.2–10.8 尚未支持。
