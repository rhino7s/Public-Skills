# SQL Server Read-only MCP

面向局域网 Agent 的本地 stdio MCP，提供只读 SQL 查询、数据库对象探索，以及目录授权的 procedure 执行。

## 模式选择

| 认证 | access.mode 默认值 | 使用范围 |
|---|---|---|
| Windows AD | catalog | 能力目录授权的业务对象 |
| SQL 密码 | development | 数据库权限及工具规则允许的只读查询与元数据；可配置为 catalog |

默认值适用于省略、null 或空白。完整工具矩阵与授权规则见 [访问模式](docs/access-modes.md)。

## 快速开始

1. 按 [Agent 通用安装说明](docs/agent-install.md) 下载并校验 Windows x64 Release，无需另装 .NET。
2. 下载后以包内同版本安装说明为准，准备独立的本机配置。
3. 配置齐全后接入 Agent，分别验证启动、工具列表及数据库连接。

缺少配置时可以先完成安装；连接验证未通过不等于安装失败。数据库身份、目录函数及授权由管理员提供。

## 文档导航

| 文档 | 用途 |
|---|---|
| [安装与配置](docs/agent-install.md) | 安装、配置路径、日志设置、接入及验收 |
| [配置 Schema](appsettings.schema.json) | 字段范围与默认值；[示例](appsettings.example.json) 提供完整模板 |
| [访问模式](docs/access-modes.md) | 工具矩阵、授权边界、SQL 支持范围、超时与日志 |
| [目录契约](docs/capabilities.md) | 目录函数、维护及迁移 |
| [工具与参数说明](docs/tool-prompts.md) | 实际发送给 Agent 的固定提示词 |
| [SQL Server 权限](docs/sqlserver-permissions.md) | 管理员维护数据库权限 |
| [开发与发布](docs/development.md) | 构建、测试、Inspector、版本发布 |
| [验证记录](docs/access-modes-validation.md) | 历史验证与待验收项目 |

## 关键限制

- execute_sql 禁止持久化修改；已授权 procedure 可能修改数据，其内部行为由管理员审核。
- 能力目录只限制通过本 MCP 的使用；实际执行仍受 SQL Server 权限约束。
- 每次 SQL 调用独立，临时表与变量不能跨调用复用；截断或部分结果不能视为完整资料。
- Windows AD 使用 MCP 进程的当前 Windows 身份；其他平台从源码构建，使用 SQL 密码认证。
- 本机配置与日志应限制访问；默认不记录 SQL 文本。不要提交或上传真实配置、凭证及日志。
