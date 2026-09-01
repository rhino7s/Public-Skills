# SQL Server Read-only MCP

面向局域网 Agent 的本地 `stdio` MCP，用于查询资料、定位对象、读取 SQL 定义及执行受限的只读 T-SQL。

SQL Server 低权限账号是最终安全边界。`execute_procedure` 可执行另外授权的存储过程，过程可能修改资料，必须单独审核。

## 工具

| 工具 | 用途 |
| --- | --- |
| `execute_sql` | 执行受限查询；允许变量、CTE、表变量和本地临时表。 |
| `execute_procedure` | 执行当前账号已获对象级权限的单一静态存储过程调用。 |
| `find_object` | 在明确数据库中定位 Table、View、SP 或 Function，并检查 SP 执行权限。 |
| `find_object_references` | 搜索对象定义和可选 Job 中的静态文本命中候选。 |
| `get_object_details` | 读取对象字段、索引、参数、权限和定义片段。 |

`find_object_references` 的结果可能包含注释、对象自身定义或其他非执行文字，也可能遗漏运行时拼接的动态 SQL；它不代表实际调用方、读取方、写入方或完整血缘。

## 安全边界

- `execute_sql` 禁止持久化 DML/DDL、`EXEC`、动态 SQL、远程及 Ad Hoc 数据源、全局临时表和其他有副作用的语法；只允许本地临时对象写入。
- `execute_procedure` 只接受一条静态命名调用，且三段名数据库必须与 `database` 参数一致。
- 每个数据库参数只接受一个明确数据库，不接受数据库列表；工具不会枚举数据库或无目的抓取资料。
- MCP 不维护业务 SP 白名单。SQL Server 仍会拒绝账号没有权限的资料和过程。
- 专用账号不得加入 `sysadmin`、`db_owner`、`db_ddladmin`，也不应取得数据库级 `GRANT EXECUTE`。

## 快速开始

### 1. 取得程序

支持 `win-x64`、`linux-x64`、`osx-x64` 和 `osx-arm64`。自包含发布包不要求目标机器安装 .NET；从源码构建需要 .NET 10 SDK。

### 2. 设置 SQL Server 权限

建议为专用 Login 在允许访问的数据库加入 `db_datareader`、`db_denydatawriter` 并授予 `VIEW DEFINITION`；存储过程只做对象级 `GRANT EXECUTE`。

完整授权、Job 查询权限、撤销方式及风险说明见 [SQL Server 权限](docs/sqlserver-permissions.md)。配置后可运行 [check-access.sql](docs/check-access.sql) 做只读检查。

### 3. 建立本机配置

复制 [appsettings.example.json](appsettings.example.json) 为 `appsettings.local.json`，填写专用低权限账号。字段说明、默认值及允许范围由 [appsettings.schema.json](appsettings.schema.json) 维护；真实配置已被 Git 忽略。

限制配置文件 ACL，只允许使用者和管理员读取。`trustServerCertificate=true` 只适合没有可信证书的内部环境；部署可信证书后应改为 `false`。

### 4. 接入 Agent

依照 [Agent 通用安装说明](docs/agent-install.md)，把当前平台可执行文件的绝对路径以及 `--config <配置绝对路径>` 映射到客户端的本地 `stdio` MCP 配置。

## 运行行为

- 默认查询超时 60 秒，最多返回 200 行、约 256 KB；单进程并发查询 2，连接池 4。
- 可调范围和程序硬上限以配置 schema 为准；越界配置会导致启动失败，不会静默改值。
- 截断结果会明确返回 `truncated` 和原因，不得视为完整资料。
- 完整结果放在 `structuredContent`；`content.text` 只提供摘要或错误，避免重复占用上下文。
- 本地 JSON Lines 日志默认保留 20 天，不记录密码、连接字符串、查询结果或对象定义。启用 SQL 文本日志前应评估业务资料风险。

## 开发与发布

```powershell
dotnet restore SqlServerReadonlyMcp.slnx
dotnet build SqlServerReadonlyMcp.slnx --no-restore
dotnet test SqlServerReadonlyMcp.slnx --no-restore --no-build
.\check-public-repo.ps1
.\publish-all.ps1
```

Linux/macOS 使用 `publish-all.sh`。发布结果位于被 Git 忽略的 `publish/<rid>`。

本机存在 `appsettings.local.json` 时，数据库访问逻辑变更必须在提交或发布前使用被 Git 忽略的 `integration.local.json` 执行真实只读测试：

```powershell
.\test-integration.ps1
```

调试 MCP 协议可运行 `start-inspector.ps1` 或 `start-inspector.sh`；Inspector 只应在本机使用。

## 已知限制

- 加密模块无法读取定义。
- `canExecute=true` 只代表账号有权限，不代表存储过程只读。
- MCP 不检查已授权存储过程内部的动态 SQL 或实际副作用。
- 每位用户本地启动一个 `stdio` 进程；并发和连接池限制按进程计算。
