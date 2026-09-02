# Agent 通用安装说明

供 Codex、OpenClaw、Claude 等支持本地 `stdio` MCP 的 Agent 使用。不同客户端的配置格式不统一，应把下列参数映射到客户端当前支持的格式。

## 安装

自动发布的预编译包目前只提供 Windows x64。Linux、Intel Mac 和 Apple Silicon Mac 需要安装 .NET 10 SDK，并从源码运行 `publish-all.ps1` 或 `publish-all.sh`。

### Windows x64

1. 下载 [最新 Windows x64 ZIP](https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip) 和对应的 [SHA-256 校验文件](https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip.sha256)。
2. 在 PowerShell 中校验 ZIP。以下命令假设两个文件位于当前目录：

```powershell
$archivePath = Join-Path (Get-Location) 'sqlserver-readonly-mcp-win-x64.zip'
$checksumPath = "$archivePath.sha256"
$expectedHash = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw 'Release ZIP 的 SHA-256 校验失败。'
}
```

3. 解压 ZIP 到固定目录。升级时建议解压到新目录，验证后再切换 Agent 的可执行文件路径；不要把真实配置放进 Release 解压目录。
4. 将包内 `appsettings.example.json` 复制到独立的本机配置目录并命名为 `appsettings.local.json`，由用户填写 SQL Server 低权限账号。不得回显、提交、上传或写入聊天记录。
5. 限制 `appsettings.local.json` 的 NTFS 权限，只允许当前使用者和管理员读取。

Release ZIP 只包含可执行文件、示例配置、配置 schema、版本和离线说明，不包含日志、本地配置或开发机发布目录。

### 接入 Agent

在 Agent 中建立本地 MCP：

| 参数 | 值 |
| --- | --- |
| Name | `sqlserver-readonly` |
| Transport | `stdio` |
| Command | 当前平台 MCP 可执行文件的绝对路径 |
| Args | `--config`、`appsettings.local.json` 的绝对路径 |

Command 和 Args 不要拼成一段 shell 字符串。Windows 可执行文件以 `.exe` 结尾，Linux/macOS 没有扩展名。

Linux/macOS 从源码发布后设置最小文件权限：

```sh
chmod 700 /absolute/path/sqlserver-readonly-mcp
chmod 600 /absolute/path/appsettings.local.json
```

重新加载 MCP 客户端，确认可列出 `execute_sql`、`execute_procedure`、`find_object`、`find_object_references` 和 `get_object_details`。

连接验证必须由用户提供明确数据库和对象，并使用低成本只读操作；不要枚举对象或执行大范围查询。
