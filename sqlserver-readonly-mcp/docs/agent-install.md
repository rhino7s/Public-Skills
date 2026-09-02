# Agent 通用安装说明

供 Codex、OpenClaw、Claude 等支持本地 `stdio` MCP 的 Agent 使用；按客户端格式映射本文的 Name、Command 和 Args。

## 范围与身份校验

`Public-Skills` 是多项目仓库。本次只安装 `sqlserver-readonly-mcp`，不要安装 `dab-mcp-skill`，也不要把整个仓库当作一个项目安装。

| 项目 | 正确值 |
| --- | --- |
| 来源 | `https://github.com/rhino7s/Public-Skills` |
| Release 标签 | `sqlserver-readonly-mcp-v*` |
| Windows 包 / 程序 | `sqlserver-readonly-mcp-win-x64.zip` / `sqlserver-readonly-mcp.exe` |
| MCP 名称 | `sqlserver-readonly` |
| 配置字段 | `connection.server`、`connection.username`、`connection.password`、`connection.defaultDatabase` |
| 工具 | `execute_sql`、`execute_procedure`、`find_object`、`find_object_references`、`get_object_details` |
| Windows 运行时 | x64 自包含包，不需要另装 .NET Runtime |

任一项不符，或出现 `credentialTarget`、顶层 `database` 等其他项目的配置格式时，立即停止；不要搜索或替换成同名、近似项目，也不要修改 Agent 配置。

## Windows x64 安装

1. 只下载以下两个文件：

   - [最新 Windows x64 ZIP](https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip)
   - [SHA-256 校验文件](https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip.sha256)

2. 在两个文件所在目录校验 ZIP；失败时停止：

```powershell
$archivePath = Join-Path (Get-Location) 'sqlserver-readonly-mcp-win-x64.zip'
$expectedHash = ((Get-Content -LiteralPath "$archivePath.sha256" -Raw).Trim() -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw 'Release ZIP 的 SHA-256 校验失败。' }
```

3. 包内只能有以下 6 个文件；缺少、改名或出现其他可执行文件时停止：

   - `sqlserver-readonly-mcp.exe`
   - `appsettings.example.json`
   - `appsettings.schema.json`
   - `README.md`
   - `docs/agent-install.md`
   - `VERSION.txt`

4. 解压到按版本区分的目录，例如 `C:\Users\<Windows用户名>\AppData\Local\Programs\sqlserver-readonly-mcp\<版本>`；尖括号内容必须替换为本机实际值。升级时使用新目录，验证后再切换 Agent 路径，不要覆盖正在运行的版本。
5. 在独立配置目录复制 `appsettings.example.json` 和 `appsettings.schema.json`，将前者改名为 `appsettings.local.json`；两者保持同一相对位置，使 `$schema` 可解析。

## 凭证、日志与权限

当前版本不支持 Windows Credential Manager。密码明文保存在 `appsettings.local.json`；由用户在本机填写，不得在聊天、终端输出、日志、提交或上传中回显。

先读取 Windows 实际目录：

```powershell
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$userProfile = [Environment]::GetFolderPath('UserProfile')
```

程序、配置和日志分开存放：

- 程序：`$localAppData\Programs\sqlserver-readonly-mcp\<版本>`
- 配置：`$userProfile\.codex\mcp-configs\sqlserver-readonly\appsettings.local.json`
- 日志：`$localAppData\sqlserver-readonly-mcp\logs`

`$localAppData` 和 `$userProfile` 只用于计算路径。写入 JSON、Command 和 Args 时，必须使用展开后的绝对路径，不得写入字面量 `$localAppData`、`$userProfile`、`%LOCALAPPDATA%` 或 `%USERPROFILE%`；程序不会展开这些变量。JSON 路径建议写成 `C:/Users/...`，若使用反斜线则必须按 JSON 规则写成 `C:\\Users\\...`。

将 `logging.directory` 设为展开后的日志绝对路径，并保持 `logging.includeSqlText=false`；只有用户明确接受 SQL 文本写入本机日志后才能启用。

NTFS ACL 是限制访问账号，不是把文件设为“只读”，也不能用文件的只读属性代替：

| 账号 | 必要权限 |
| --- | --- |
| 负责维护配置的当前用户 | 修改配置文件 |
| 其他 MCP 运行账号（如有） | 读取配置、修改日志目录 |
| `SYSTEM`、本机 `Administrators` | 可保留完全控制 |
| `Everyone`、`Users`、`Authenticated Users` 等泛用户组 | 不得读取配置或日志 |

当前用户与 MCP 运行账号相同时，无需重复授权；当前用户必须仍可修改配置，不是只读。

## 接入与验证

修改 Agent 现有配置前先备份。只新增或更新 `sqlserver-readonly`；若同名条目来源不明，停止并报告冲突。

| 参数 | 值 |
| --- | --- |
| Name | `sqlserver-readonly` |
| Transport | `stdio` |
| Command | `sqlserver-readonly-mcp.exe` 展开后的绝对路径 |
| Args | `--config`、`appsettings.local.json` 展开后的绝对路径，作为两个独立参数 |

重新加载 Agent 后，确认只能看到身份表中的 5 个工具。数据库连接验证必须由用户指定数据库和对象，并使用低成本只读操作；不要枚举数据库、对象或执行大范围查询。

## 其他平台

Linux、Intel Mac 和 Apple Silicon Mac 暂无预编译 Release，需安装 .NET 10 SDK 后从源码运行 `publish-all.ps1` 或 `publish-all.sh`。发布后设置：

```sh
chmod 700 /absolute/path/sqlserver-readonly-mcp
chmod 600 /absolute/path/appsettings.local.json
```

## 交给 Agent 的安装指令

```text
请只安装 rhino7s/Public-Skills 仓库中的 sqlserver-readonly-mcp。

先完整阅读并严格执行：
https://github.com/rhino7s/Public-Skills/blob/main/sqlserver-readonly-mcp/docs/agent-install.md

若来源、文件、配置格式或工具列表与文档不符，立即停止。数据库凭证由用户在本机填写，不得要求发送到聊天或回显。
```
