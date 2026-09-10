# 可选业务能力目录

当前源码支持，已完成开发中 MCP 的 AD 目录与 check 成功路径验证。其他账号、权限组合及查询计划仍需按部署环境验证。无需更改已有业务执行工具。

## 配置

在本机配置增加以下部分，将两个名称替换为已建立函数的实际三段名：

```json
"capabilities": {
  "listFunction": "ConfigDB.dbo.list_capabilities",
  "checkFunction": "ConfigDB.dbo.list_capabilities_check",
  "pageSize": 100
}
```

- 不配置或两函数名留空时，不注册 list_capabilities，保持原有行为。
- Windows 集成认证启用目录时必须配置两个函数。SQL 密码模式只要求 listFunction，即使设置 checkFunction 也不执行 AD 检查。
- 名称只允许三段、dbo schema，支持 `[带特殊字符的名称]` 引用，不接受调用括号、参数或 SQL 片段。
- pageSize 为正整数，默认 100，最大 2147483646（预留额外探测行），实际每页同时受 query.maxResultSizeKb 限制。通常无需调整到很大。
- 保存后重启本项目开发中 MCP。配置不热更新；数据库中的说明和授权则每次重新查询，无缓存。

连接直接使用工具指定的数据库，capabilities 使用各函数三段名中的数据库。已移除 connection.defaultDatabase：旧本地配置中的该字段由程序忽略，可自行删除；新 schema 不再接受此字段。不再先连接默认库后切库，也不回退到 Login 默认数据库。连接池按目标数据库连接字符串划分，所有活动操作仍共用并发门。

## 函数契约

`list_capabilities()` 是无参数表值函数，返回 int id、int ord、文本 iname、非空文本 desp。函数按登录身份筛选、按 id 去重，保留最小 ord。MCP 只查询这四列，显式按 ord、id 排序，不暴露额外字段。

每笔说明在函数内整合为 `### SKILL` 或 `### 对象：数据库.dbo.对象` 标题加正文。MCP 不区分后台 skill/grant，返回按顺序拼接的 Markdown 文本及分页尾部，不重复返回完整结构化目录。

`list_capabilities_check()` 是无参数标量函数，必须返回非空 SQL bit。程序只接受 bit 1；int 1、文本、NULL 都是契约错误。它只判断当前用户是否有任一有效目录记录，不验证业务对象权限。

管理员 base 函数不对普通用户授权。普通用户须能连接配置库，并有目录函数 SELECT 与检查函数 EXECUTE。不得为了方便读取而授予配置库 db_datareader 或 schema 级 SELECT。实际 SQL 权限仍由 SQL Server 决定。

## 运行行为

启用目录的 AD 连接，所有六个工具每次调用都先检查，再执行实际工具；即使 Agent 跳过目录或提交无效业务参数，仍先受统一检查。

检查返回 false、AD 首页目录为空或明确缺少 SQL 权限时返回 `access_denied`。检查超时、连接故障返回 `access_check_unavailable`，本次业务操作不执行，可稍后重试；函数缺失或契约错误使用相同 code，但提示联系管理员。调用方取消返回 `canceled`，不自动重试。目录读取故障返回 `capabilities_unavailable`，停止本次业务操作。所有错误仅提供通用说明，内部日志记录错误类型和 SQL 错误号，不记录凭证或说明正文。检查通过后业务操作仍可能因自身 SQL 权限不足而失败。

check 和实际操作按先后执行，各自使用现有连接池与并发门，不并行占用额外连接，不长期保持检查连接；本版不改变原工具连接生命周期。两次操作并非原子事务，已放行操作不因随后撤权而自动取消。不同数据库访问需要的成本应在部署环境测量。

SQL 密码模式不做这项前置检查；目录读取失败返回一般的目录不可用错误，不泄露底层详情。

Agent 首次调用 list_capabilities 不需参数，续取只传 `offset`。页大小由配置决定；尾部包含 has_more 和 next_offset。以完整记录为单位分页，单条过长时明确报错。首次空目录对 AD 拒绝，越界 offset 返回结束页。分页期间数据变化可能重复或遗漏，必要时从 0 重读。

## 测试与部署边界

测试只允许启动本项目构建的 MCP，禁止使用 Codex 已配置的真实 MCP。自动协议测试使用临时配置和不可连接的本机地址，不读取真实配置。其他单元测试使用替代存储验证 false、异常、恢复授权、分页等行为。

后续由管理员配置开发中 MCP，再验证真实函数结果、AD 权限拒绝及执行计划。脚本 [check-capability-permissions.sql](check-capability-permissions.sql) 仅供管理员在配置库手动诊断，不通过普通 MCP 执行，不更改授权；输出有效权限差异、未验证项和扫描范围。真实数据库联调前，不将语法检查视为权限或性能验证。

初始 capabilities 实现验证记录（以下哈希仅标识当时产物）：

- 构建零警告、零错误；155 项测试中 151 项通过，4 项真实数据库测试因未配置而跳过。
- 沙箱命名管道限制导致 dotnet test 包装进程无法连接，改为直接运行项目编译的 xUnit 测试程序集，完整测试通过。
- 四个平台的本地构建产物已生成。NuGet 审计源不可访问，最后一次构建只在进程环境中临时关闭审计并使用已有依赖，未改项目审计配置；不代表漏洞审计通过。
- 新生成的 `publish/win-x64/sqlserver-readonly-mcp.exe` 已通过独立 stdio 验证：注册六项工具、AD 检查连接失败统一拒绝。使用临时测试配置及 `127.0.0.1:1`，未使用真实 MCP 或数据库配置。
- Windows 产物 SHA-256：`A49AF117698B10328734517831DA7595F1DD618687FE6B270CC135646F84DCF4`。
- 函数及管理员核查脚本通过 ScriptDom 语法解析，未在数据库执行；实际权限、跨库定序和查询计划仍待联调。

移除默认库依赖后的补充验证：

- 160 项自动测试中 156 项通过，4 项通用数据库联调测试跳过；新增缺失目标库拒绝、连接参数隔离和旧 defaultDatabase 字段忽略测试。
- 另通过最新项目开发中 MCP 和未修改的本地 AD 配置进行实际只读验证：目录返回 1 条 SKILL，check 返回 SQL bit true，DB_NAME() 与明确指定的目标数据库一致。
- 配置文件测试前后哈希一致；未修改实际表、函数或授权，未调用 Codex 已配置的真实 MCP。

## Procedure 强制授权

未配置 listFunction 时不注册 execute_procedure。配置后所有账号每次执行都直接查询目录函数的 iname，独立于 AD checkFunction、展示分页和 Agent 历史调用，不缓存授权。skill 的 iname 为空字符串（兼容 NULL/空白）；仅当前身份有效 grant 返回完整三段对象名。函数负责这一契约，MCP 不解析说明文字判断授权。

对象名按数据库、schema、对象分别比较，数据库名通过 DB_ID 解析；schema 和对象名使用目标库目录定序（CATALOG_DEFAULT），不使用数据定序，兼容方括号。未匹配返回 access_denied；查询失败返回安全的不可用/取消状态，不执行业务调用。授权通过后仍核验真实用户 procedure、系统同名冲突及 EXECUTE 权限。检查与执行不是原子事务。无需为 grant 增加对象类型字段。
