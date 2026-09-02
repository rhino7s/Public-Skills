# Agent 通用安装说明

供 Codex、OpenClaw、Claude 等支持本地 `stdio` MCP 的 Agent 使用。不同客户端的配置格式不统一，应把下列参数映射到客户端当前支持的格式。

## 安装前身份校验（必须）

本文只适用于 `rhino7s/Public-Skills` 仓库中的 `sqlserver-readonly-mcp`。Agent 必须使用本文给出的固定下载地址，不得通过项目名称搜索、选择同名或近似项目，也不得在下载失败时自行寻找替代实现。

| 项目 | 必须符合 |
| --- | --- |
| 源仓库 | `https://github.com/rhino7s/Public-Skills` |
| Release 标签 | 以 `sqlserver-readonly-mcp-v` 开头 |
| Windows ZIP | `sqlserver-readonly-mcp-win-x64.zip` |
| Windows 可执行文件 | `sqlserver-readonly-mcp.exe` |
| MCP 名称 | `sqlserver-readonly` |
| 运行时 | Windows Release 为自包含包，不需要另装 .NET Runtime |
| 配置格式 | `connection.server`、`connection.username`、`connection.password`、`connection.defaultDatabase` |
| 工具列表 | `execute_sql`、`execute_procedure`、`find_object`、`find_object_references`、`get_object_details` |

以下任一情况表示来源或项目不正确，Agent 必须立即停止，不得继续安装或修改 MCP 客户端配置：

- 仓库所有者不是 `rhino7s`，或仓库名不是 `Public-Skills`。
- ZIP、可执行文件或 MCP 名称与上表不一致。
- 安装过程要求额外安装 .NET Runtime。
- 配置模板使用顶层 `database`、`credentialTarget` 等不属于本项目的字段。
- 初始化后的工具不是上表列出的 5 个工具。

当前版本不支持 Windows Credential Manager。真实 SQL Server 密码保存在本机 `appsettings.local.json` 中，因此该文件必须位于 Release 解压目录之外、限制 NTFS 读取权限，并且不得在聊天、日志或提交中回显。

## 安装

自动发布的预编译包目前只提供 Windows x64。Linux、Intel Mac 和 Apple Silicon Mac 需要安装 .NET 10 SDK，并从源码运行 `publish-all.ps1` 或 `publish-all.sh`。

### Windows x64

1. 仅从以下固定地址下载，不要搜索 Release 或替换仓库：

   - [最新 Windows x64 ZIP](https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip)
   - [SHA-256 校验文件](https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip.sha256)

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

3. 解压前后确认包内只有以下 6 个文件；缺少、改名或出现其他可执行文件时停止安装：

   - `sqlserver-readonly-mcp.exe`
   - `appsettings.example.json`
   - `appsettings.schema.json`
   - `README.md`
   - `docs/agent-install.md`
   - `VERSION.txt`

4. 解压 ZIP 到固定目录。升级时建议解压到新目录，验证后再切换 Agent 的可执行文件路径；不要把真实配置放进 Release 解压目录，也不要安装额外的 .NET Runtime。
5. 将包内 `appsettings.example.json` 复制到独立的本机配置目录并命名为 `appsettings.local.json`，保持原有 JSON 结构，由用户在本机填写 SQL Server 低权限账号。不得回显、提交、上传或写入聊天记录。
6. 限制 `appsettings.local.json` 的 NTFS 权限，只允许当前使用者和管理员读取。

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

## 可直接交给 Agent 的严格安装指令

```text
只能安装 rhino7s/Public-Skills 仓库中的 sqlserver-readonly-mcp，禁止搜索、选择或替换成任何其他同名或近似项目。

严格按照以下文档执行：
https://github.com/rhino7s/Public-Skills/blob/main/sqlserver-readonly-mcp/docs/agent-install.md

只能从以下地址下载 Windows x64 包和校验文件：
https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip
https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip.sha256

下载后必须验证 SHA-256。正确可执行文件必须叫 sqlserver-readonly-mcp.exe，MCP 名称必须是 sqlserver-readonly，配置必须基于包内 appsettings.example.json。Windows 包是自包含版本，不得安装额外的 .NET Runtime。

预期工具只有 execute_sql、execute_procedure、find_object、find_object_references、get_object_details。如果仓库、Tag 前缀、ZIP、可执行文件、配置字段、运行时要求或工具列表有任何不一致，立即停止并报告，不得自行寻找替代项目，不得修改 MCP 客户端配置。

数据库凭证必须由用户在本机 appsettings.local.json 中填写，不得要求用户贴到聊天里，也不得回显配置内容。
```
