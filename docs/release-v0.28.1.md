# v0.28.1 — 队列安全、取消与发布隐私维护版

本版本面向 **Windows x64 + ArcGIS Pro 3.6**，不新增 GIS 工具数量，重点修复
实时桥接和公开安装包中必须处理的可靠性与隐私问题。

## 主要变化

- Release 构建关闭 PDB 与调试目录信息，发布测试会解开 `.esriAddinX`，拒绝
  PDB，并扫描 DLL 等二进制中的源码目录、用户目录、邮箱、令牌和私钥标记。
- live queue 增加到期时间、ArcGIS Pro 会话 ID、项目路径、取消标记和幂等键。
  超时、取消、ArcGIS Pro 重启或打开项目发生变化时，不再允许迟到执行。
- 完成结果按幂等键短期缓存；重复投递只返回原结果，不会重复修改项目。
- MCP stdio 主循环支持并发处理工具请求，并能在长任务执行期间响应标准
  `notifications/cancelled` 通知。
- 直接 ArcPy worker 在取消或超时时终止进程树；Add-in 内的 worker 继续使用
  .NET 取消令牌终止完整子进程树。
- 新增固定安装 locator，自定义 `-InstallRoot` 后 Add-in 仍能找到私有配置、
  live queue 和受限的 `runtime\worker.py`。

## 安装

下载并解压 `arcgis-pro-codex-bridge-0.28.1-win-x64.zip`，校验同名
`.zip.sha256` 后运行：

```powershell
.\scripts\install.ps1 `
  -AllowedDataRoot 'C:\GISData' `
  -OutputRoot 'C:\GISOutput'
```

安装完成后重启 Codex，并重启 ArcGIS Pro 以加载 v0.28.1 Add-in。安装器不会
读取或修改允许数据目录中的实际 GIS 数据，也不会添加 Defender 排除项。

## 兼容性

当前仅支持 ArcGIS Pro 3.6。ArcMap / ArcGIS Desktop 10.2–10.8 尚未支持。
