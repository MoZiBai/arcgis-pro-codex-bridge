# v0.29.0 — 安全队列与 MCP 互操作版

v0.29.0 targets Windows x64 with ArcGIS Pro 3.6. It keeps the existing GIS
action surface and focuses on authenticated local bridging, safer installation,
and MCP protocol behavior.

## 主要变化 / Highlights

- 本地 jobs、results 和 cancellation 队列现在使用 `hmac-sha256-v1` 签名信封；
  未签名、篡改或未知版本的消息会被拒绝。
- 安装器生成随机 32 字节 HMAC 密钥，并为配置、密钥和队列关闭继承 ACL；
  仅当前用户、SYSTEM 和本机 Administrators 保留访问权限。
- Add-in 默认不再把完整异常堆栈返回到 MCP；公开错误会限制长度并隐藏绝对路径。
- 工具执行失败使用 MCP `isError=true` 结果；请求取消继续使用 JSON-RPC 取消错误。
- MCP 初始化不再回显客户端任意协议版本，仅协商服务器实际支持的
  `2024-11-05`。
- CI 增加 Python、PowerShell、Add-in 编译、包版本、安装生命周期和 Release ZIP
  隔离验证。

## 不兼容变化 / Compatibility

v0.29.0 的签名队列与 v0.28.2 不兼容。MCP runtime 和 ArcGIS Pro Add-in 必须
一起升级；安装后重启 Codex，并在 Add-in 发生变化时重启 ArcGIS Pro。

当前仍只支持 ArcGIS Pro 3.6。ArcMap / ArcGIS Desktop 10.2–10.8 暂不支持。

ArcGIS Pro 3.6 的原生报表创建 API 不支持通过 `use_selection=true` 创建选择集
报表；请使用 `definition_query`，或先创建完整报表再导出/配置。

## 安装 / Install

GitHub Release 安装不需要 Visual Studio、MSBuild 或 .NET SDK。下载并验证
`arcgis-pro-codex-bridge-0.29.0-win-x64.zip.sha256` 后，解压并运行：

```powershell
.\scripts\install.ps1 `
  -AllowedDataRoot 'C:\GISData' `
  -OutputRoot 'C:\GISOutput'
```

安装器会校验预编译 `.esriAddinX` 的版本、ID 和 SHA-256，并在失败时回滚已拥有的
runtime、配置、注册、locator 和 Add-in 状态。

## 验证 / Validation

源码仓库包含以下契约：

- `source_contract_suite.py`
- `mcp_protocol_contract_test.py`
- `queue_and_concurrency_contract_test.py`
- `installation_contract_test.ps1`
- `installation_lifecycle_test.ps1`
- `release_lifecycle_test.ps1`
