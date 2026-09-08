# SQL Server Read-only MCP

面向局域网 Agent 的本地 `stdio` MCP，用于查询资料、定位对象、读取 SQL 定义及执行受限的只读 T-SQL。

SQL Server 低权限身份是最终安全边界。默认使用专用 SQL Login；管理员确认 AD 映射与最终权限后，也可明确改用启动 MCP 进程的 Windows/AD 身份。`execute_procedure` 可执行另外授权的存储过程，过程可能修改资料，必须单独审核。

## 工具

| 工具 | 用途 |
| --- | --- |
| `execute_sql` | 执行受限查询；允许变量、CTE、表变量和本地临时表。 |
| `execute_procedure` | 执行当前账号已获对象级权限的单一静态存储过程调用。 |
| `find_object` | 在明确数据库中定位 Table、View、SP 或 Function，并检查 SP 执行权限。 |
| `find_object_references` | 搜索对象定义和可选 Job 中的原始文本命中候选。 |
| `get_object_details` | 读取对象字段、索引、参数、权限和定义片段。 |

`find_object_references` 的结果可能包含注释、对象自身定义或其他非执行文字，也可能遗漏运行时拼接的动态 SQL；它不代表实际调用方、读取方、写入方或完整血缘。

## 安全边界

- `execute_sql` 禁止持久化 DML/DDL、`EXEC`、动态 SQL、远程及 Ad Hoc 数据源、全局临时表和其他有副作用的语法；只允许本地临时对象写入。
- 写入语句中的表别名和 CTE 名称不得使用 `#` 前缀（包括全角兼容写法），避免把持久化对象伪装成临时表。请使用普通别名，并直接指定实际的 `#本地临时表` 或 `@表变量` 作为写入目标；独立只读语句不受此命名限制。
- `execute_procedure` 只接受一条静态命名调用，且三段名数据库必须与 `database` 参数一致。拒绝 `sys` 架构及 `sp_`、`xp_` 前缀的过程，不区分大小写，包括 `sp_executesql`、`sp_prepexec` 和游标动态执行入口；业务过程也不能使用这些保留名称。
- 每个数据库参数只接受一个明确数据库，不接受数据库列表。Agent 应按用户指定范围查询，禁止无目的枚举或大范围取数；这属于使用约定，SQL 分析器不会判断查询的业务目的，可读取范围仍由数据库权限控制。
- MCP 不维护业务 SP 白名单。SQL Server 仍会拒绝账号没有权限的资料和过程。
- MCP 使用的 Windows/AD 身份或 SQL Login 不得加入 `sysadmin`、`db_owner`、`db_ddladmin`，也不应取得数据库级 `GRANT EXECUTE`。

## 快速开始

### 1. 取得程序

自动发布的预编译包目前只提供 Windows x64，自包含包不要求目标机器安装 .NET：

- [下载最新 Windows x64 ZIP](https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip)
- [下载 SHA-256 校验文件](https://github.com/rhino7s/Public-Skills/releases/latest/download/sqlserver-readonly-mcp-win-x64.zip.sha256)

安装和校验步骤见 [Agent 通用安装说明](docs/agent-install.md)。只允许使用 `rhino7s/Public-Skills` 发布的 `sqlserver-readonly-mcp-win-x64.zip`；不要按项目名称搜索或替换为其他同名、近似 MCP。正确可执行文件为 `sqlserver-readonly-mcp.exe`，MCP 名称为 `sqlserver-readonly`，Windows 包不需要另装 .NET Runtime。Linux、Intel Mac 和 Apple Silicon Mac 暂无预编译 Release；需要安装 .NET 10 SDK 并从源码发布。

### 2. 设置 SQL Server 权限

Windows 内网建议将 AD 安全群组映射为 SQL Server Login；SQL 密码模式则使用专用低权限 SQL Login。两者都只在允许访问的数据库加入 `db_datareader`、`db_denydatawriter` 并授予 `VIEW DEFINITION`；存储过程只做对象级 `GRANT EXECUTE`。

完整授权、Job 查询权限、撤销方式及风险说明见 [SQL Server 权限](docs/sqlserver-permissions.md)。配置后可运行 [check-access.sql](docs/check-access.sql) 做只读检查。

### 3. 建立本机配置

复制 [appsettings.example.json](appsettings.example.json) 为 `appsettings.local.json`。默认 `authentication=sqlPassword`，必须在本机填写专用低权限 SQL Login；空白凭证会导致启动失败。只有管理员确认 AD 映射及实际用户最终权限后，才能明确改为 `windowsIntegrated`，并将 `username` 和 `password` 保持为空字符串。字段说明、默认值及允许范围由 [appsettings.schema.json](appsettings.schema.json) 维护；真实配置已被 Git 忽略。

省略 `authentication` 时一律按 `sqlPassword` 处理，以兼容旧版配置且避免意外使用当前 AD 权限。切换到 Windows 集成认证时必须明确写入 `windowsIntegrated`，并同时清空账号和密码。

本机配置和日志必须放在 Release 目录之外，并限制可访问账号；这不是把当前用户设为只读。SQL 密码模式的配置包含明文密码，Windows 集成模式不保存 AD 密码。具体 ACL 和日志要求见 [Agent 通用安装说明](docs/agent-install.md)。

### 4. 接入 Agent

依照 [Agent 通用安装说明](docs/agent-install.md)，把当前平台可执行文件的绝对路径以及 `--config <配置绝对路径>` 映射到客户端的本地 `stdio` MCP 配置。

## 运行行为

- 按随包示例配置，查询超时 60 秒，最多返回 200 行、约 256 KB；单进程并发查询 2，连接池上限 2。
- 可调范围和程序硬上限以配置 schema 为准；越界配置会导致启动失败，不会静默改值。
- 截断结果会明确返回 `truncated` 和原因，不得视为完整资料。
- 完整结果放在 `structuredContent`；`content.text` 只提供摘要或错误，避免重复占用上下文。
- 本地 JSON Lines 日志默认保留 20 天。随包示例配置关闭 SQL 文本记录；程序不主动记录连接凭证、完整查询结果或对象定义，但错误消息可能包含业务值，日志仍须限制访问。启用 SQL 文本记录前应评估业务资料风险。

## 开发与发布

```powershell
.\check-public-repo.ps1
dotnet restore SqlServerReadonlyMcp.slnx
dotnet build SqlServerReadonlyMcp.slnx --no-restore
.\publish-all.ps1
dotnet test SqlServerReadonlyMcp.slnx --no-restore --no-build
```

Linux/macOS 使用 `publish-all.sh`。发布结果位于被 Git 忽略的 `publish/<rid>`。

从本机向 GitHub 推送本项目变更或发布新版时，先运行 `publish-all.ps1`（Linux/macOS 使用 `publish-all.sh`），确认本地发布成功，再完成测试及发布产物验证，最后推送或发布。GitHub 推送和 Release 工作流本身不会更新本机的 `publish/<rid>`；此步骤也不会更新其他安装目录或重启正在运行的 MCP。

Windows x64 GitHub Release 由 [发布工作流](https://github.com/rhino7s/Public-Skills/actions/workflows/release-sqlserver-readonly-mcp.yml) 自动建立。标签必须使用 `sqlserver-readonly-mcp-v<项目版本>`，例如 `sqlserver-readonly-mcp-v0.9.0`；标签版本必须与项目文件中的 `Version` 完全一致。带预发布后缀的版本（例如 `0.10.0-rc.1`）会发布为 Pre-release，且不会替代正式 Latest。云端工作流从标签提交构建、测试，并从全新暂存目录按固定白名单生成 ZIP，不使用开发机的 `publish/`、日志或本地配置。

在已提交并推送版本变更后，可从仓库根目录运行 `pwsh -NoProfile -File ./sqlserver-readonly-mcp/push-release-tag.ps1 -Version <版本>`。脚本只允许在干净的 `main`、`HEAD=origin/main`、固定远端及 noreply Git 邮箱下运行；本机检查、构建和测试通过后，只新建并推送对应 Release 标签，不会推送分支或强制改写历史。标签推送后，脚本只读查询固定 GitHub 仓库，等待 workflow 完成并校验 Release 的标签、预发布状态及两个固定资产。

若本机同时存在 `appsettings.local.json` 和 `integration.local.json`，发布脚本会自动运行 `test-integration.ps1`。否则必须明确添加且只能添加一个参数：已对当前提交完成真实测试时使用 `-ConfirmIntegrationTestsCompleted`；本次没有数据库访问逻辑变更时使用 `-ConfirmIntegrationTestsNotRequired`。没有测试或确认时不会创建标签。

本机存在 `appsettings.local.json` 时，数据库访问逻辑变更必须在提交或发布前使用被 Git 忽略的 `integration.local.json` 执行真实只读测试：

```powershell
.\test-integration.ps1
```

Inspector 使用 `publish/<rid>` 下的程序和项目内的 `appsettings.local.json`。验证本地发布程序及配置时，应先断开 Inspector 并停止由它启动的旧服务进程，完成本地发布，再通过 `start-inspector.ps1` 或 `start-inspector.sh` 启动并重新连接，确认工具列表及用户指定的低成本只读查询。不得用仍在运行的旧进程作为新版验证结果；Inspector 只应在本机使用。

普通自动协议测试启动的是测试构建中的程序集。`test-integration.ps1` 则先重新发布当前平台程序，再通过 `publish/<rid>` 中的程序和指定的本机配置测试 MCP，包括 `execute_sql` 的低成本连接查询；发布失败立即停止，不回退到旧程序。脚本输出被测程序路径、SHA-256 和配置路径，便于核对测试范围。测试通过仅适用于该程序和该配置，不能代表其他配置或进程也能连接。

## 已知限制

- 加密模块无法读取定义。
- `canExecute=true` 只代表账号有权限，不代表存储过程只读。
- MCP 不检查已授权存储过程内部的动态 SQL 或实际副作用。
- 每位用户本地启动一个 `stdio` 进程；并发和连接池限制按进程计算。
- `windowsIntegrated` 使用 MCP 进程的当前 Windows 身份，不接受配置中的 AD 用户名或密码；本项目只承诺在 Windows AD 环境使用该模式。
