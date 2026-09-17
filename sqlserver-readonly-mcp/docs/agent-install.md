# Agent 通用安装说明

供支持本地 stdio MCP 的 Agent 使用。安装、配置、连接验证是三个独立阶段；信息不足时完成可做部分，不猜测配置、不把连接失败当成安装失败。

## 取得正确版本

| 项目 | 值 |
|---|---|
| 仓库 | https://github.com/rhino7s/Public-Skills |
| Release 标签 | sqlserver-readonly-mcp-v&lt;版本&gt; |
| Windows x64 包 | sqlserver-readonly-mcp-win-x64.zip |
| 程序 | sqlserver-readonly-mcp.exe |
| MCP 注册名称 | sqlserver-readonly |
| 运行时 | Windows x64 自包含，无需另装 .NET |

只安装此仓库的 sqlserver-readonly-mcp 项目，不安装其他项目或同名产品。

1. 从 [Release 列表](https://github.com/rhino7s/Public-Skills/releases) 选择本项目的目标版本，下载同一 Release 的 ZIP 与同名 .sha256 文件。
2. 校验 ZIP：

~~~powershell
$archivePath = Join-Path (Get-Location) 'sqlserver-readonly-mcp-win-x64.zip'
$expectedHash = ((Get-Content -LiteralPath "$archivePath.sha256" -Raw).Trim() -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw 'Release ZIP 的 SHA-256 校验失败。' }
~~~

3. 解压后核对 VERSION.txt 与所选版本，并以包内 docs/agent-install.md 为该版本的安装依据。仓库 main 的说明可能领先于已发布程序。
4. 本版包内包含以下 5 个文件；缺失或出现额外文件时停止并核对来源：

~~~text
sqlserver-readonly-mcp.exe
VERSION.txt
appsettings.example.json
appsettings.schema.json
docs/agent-install.md
~~~

5. 安装到按版本区分的目录。升级使用新目录，验证后再切换 Agent 路径，不覆盖正在运行的版本。

## 准备本机配置

复制包内 appsettings.example.json 和 appsettings.schema.json 到独立配置目录，将示例改名为 appsettings.local.json；两者保持同一相对位置。升级时保留原配置，按目标版本说明补齐必要字段。

| 必要信息 | 填写规则 |
|---|---|
| connection.server | 管理员提供的服务器／实例，不设置默认数据库 |
| connection.authentication | sqlPassword 或 windowsIntegrated；省略时为 sqlPassword |
| connection.username / password | SQL 密码认证必须填写；AD 必须为空字符串，使用进程当前 Windows 身份 |
| access.mode | 示例为空白：AD 默认 catalog，SQL 密码默认 development；AD 不允许 development |
| capabilities.listFunction | catalog 必填；development 可选，填写即启用目录 |
| capabilities.checkFunction | catalog 必填；不能在缺少 listFunction 时单独配置 |
| 两个目录函数名称 | 管理员提供的 database.dbo.function 三段名，不填参数或括号 |

SQL 密码认证下，access.mode 省略、null 或空白时使用 development；账号密码仍必须填写。AD 需管理员先完成身份映射及数据库授权。切换 AD 时可保留示例的空白 mode，并补齐两个目录函数。

缺少必要信息时，保留待填写的配置模板，报告缺项；不启动 MCP、不做连接测试，也不把未配置的实例启用到 Agent。不要要求用户将密码发送到聊天，凭证应在本机填写，不回显或上传。

### 配置路径与日志

| 项目 | 行为 |
|---|---|
| 配置路径优先级 | --config 参数 → SQLSERVER_MCP_CONFIG 环境变量 → 程序目录的 appsettings.local.json |
| 相对配置路径 | 参数／环境变量中的相对路径以进程工作目录为基准；接入时使用绝对路径 |
| logging.directory | 相对路径以程序目录为基准；建议改为独立的绝对路径 |
| logging.minimumLevel | 控制运行日志；不关闭独立写入的调用审计 |
| logging.includeSqlText | 默认 false；显式 true 才记录 SQL 文本，maxSqlTextChars 控制记录长度 |
| logging.retentionDays | 默认 20 天 |

程序不展开配置路径中的 %USERPROFILE%、%LOCALAPPDATA% 或 PowerShell 变量。先读取实际目录，再写入展开后的绝对路径：

~~~powershell
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$userProfile = [Environment]::GetFolderPath('UserProfile')
~~~

建议程序放在 $localAppData/Programs/sqlserver-readonly-mcp/&lt;版本&gt;，配置放在 $userProfile/.config/sqlserver-readonly，日志放在 $localAppData/sqlserver-readonly-mcp/logs。这些只是路径示意；写入 JSON 时使用实际绝对路径，建议用正斜线。

程序、配置、日志分开存放。SQL 密码明文保存在本机配置；配置与日志的 NTFS ACL 仅开放给维护者、实际运行账号及必要系统管理员，不能用文件“只读”属性替代访问控制。日志可能含业务诊断内容，不提交或上传。

### 超时与结果限制

| 设置 | 用途 |
|---|---|
| connection.connectTimeoutSeconds | 连接默认 5 秒，无自动重连 |
| 执行前预算 | 入口检查、解析、对象授权及元数据核验共用 15 秒 |
| query.timeoutSeconds | 业务阶段总预算，含排队、连接及结果读取，默认 60 秒；也用于数据库命令超时 |
| 结果限制 | 随包示例最多 500 行、512 KB；截断和部分结果以实际响应为准 |

完整字段范围见随包 Schema。更改配置后重启 MCP 生效。

## 接入与分阶段验收

配置齐全后，备份 Agent 原配置，只新增或更新来源明确的 sqlserver-readonly 条目。

| 参数 | 值 |
|---|---|
| Transport | stdio |
| Command | 程序的绝对路径 |
| Args | --config、配置文件绝对路径，作为两个独立参数 |

重新加载该 MCP 后，按配置核对工具集合：

| 配置 | 预期工具 |
|---|---|
| catalog | execute_sql、execute_procedure、list_capabilities、get_capability_details（4 项） |
| development，目录禁用 | execute_sql、find_object、get_object_details、find_object_references（4 项） |
| development，目录启用 | 上一行 4 项，加 execute_procedure、list_capabilities、get_capability_details（7 项） |

这是安装验收清单；目录返回的业务条目不增加 MCP 工具数量。完整技术规则由对应版本的仓库文档维护。

工具发现不需要数据库连接。配置齐全后可做以下低成本连接验证；用户要求只安装时跳过：

- catalog：先调用 list_capabilities 验证目录访问。业务查询不是安装必需项；需要验证时读取适用详情，只查询授权条目。
- development：使用用户指定的数据库执行 SELECT 1；未指定数据库时标记连接待验证。
- 不枚举数据库、不执行业务 procedure，也不为安装修改数据库授权。无法连接时提示“连接失败，请确认网络连接后再试。”，不自动重试。

| 阶段结果 | 报告 |
|---|---|
| 文件已安装，配置不完整 | 安装完成，待补齐配置；列明缺项 |
| 已接入且工具列表正确，未做连接测试 | 安装与接入完成，连接待验证 |
| 配置被程序拒绝 | 安装完成，配置校验未通过；说明需修正字段 |
| 连接失败 | 安装与接入完成，连接验证未通过 |
| 目录拒绝访问 | 安装与接入完成，当前用户没有访问权限 |
| 目录或低成本查询成功 | 安装、接入与连接验证完成，不代表所有业务权限已验证 |

## 管理员与维护者入口

数据库授权、SSMS 身份诊断及 check-access.sql 属于管理员工作，不是普通安装的必经步骤。需要时在上述仓库中选择与 VERSION.txt 相同的 sqlserver-readonly-mcp-v&lt;版本&gt; 标签，读取：

- sqlserver-readonly-mcp/docs/sqlserver-permissions.md：数据库权限与诊断。
- sqlserver-readonly-mcp/docs/access-modes.md：访问模式及授权规则。
- sqlserver-readonly-mcp/docs/development.md：源码构建、测试与发布。

这些文档不随包提供。Linux/macOS 从源码构建需要 .NET 10 SDK；本项目仅承诺 Windows AD 环境的集成认证，其他平台使用 SQL 密码。

## 交给 Agent 的安装指令

~~~text
请安装 rhino7s/Public-Skills 的 sqlserver-readonly-mcp。
从该项目 Release 下载目标版本及校验文件，校验后按包内 docs/agent-install.md 配置。
缺少信息时先完成安装，列明待配置项目；不要猜测配置或要求我在聊天中提供密码。
配置就绪后接入并验证工具列表，再做低成本连接验证，不自动重试。
~~~
