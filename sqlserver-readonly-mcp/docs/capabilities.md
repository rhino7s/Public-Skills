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

- 不配置或两函数名留空时，不注册 list_capabilities、get_capability_details 和 execute_procedure，仅开放四项通用工具。
- Windows 集成认证启用目录时必须配置两个函数。SQL 密码模式只要求 listFunction，即使设置 checkFunction 也不执行 AD 检查。
- 名称只允许三段、dbo schema，支持 `[带特殊字符的名称]` 引用，不接受调用括号、参数或 SQL 片段。
- pageSize 为正整数，默认 100，最大 2147483646（预留额外探测行），实际每页同时受 query.maxResultSizeKb 限制。通常无需调整到很大。
- 保存后重启本项目开发中 MCP。配置不热更新；数据库中的说明和授权则每次重新查询，无缓存。

连接直接使用工具指定的数据库，capabilities 使用各函数三段名中的数据库。已移除 connection.defaultDatabase：旧本地配置中的该字段由程序忽略，可自行删除；新 schema 不再接受此字段。不再先连接默认库后切库，也不回退到 Login 默认数据库。连接池按目标数据库连接字符串划分，所有活动操作仍共用并发门。

## 函数契约

`list_capabilities()` 是无参数表值函数，返回 int id、int ord、文本 iname、非空白 summary、非空 bit has_desp、非 NULL desp。summary 为 nvarchar(1000)，desp 为 nvarchar(max)，无详情维护为空字符串。has_desp 按 `CONVERT(bit, CASE WHEN i.desp <> N'' THEN 1 ELSE 0 END)` 计算，MCP 信任该标志，不另作正文空白归一化。summary 则使用 string.IsNullOrWhiteSpace 校验。

函数按登录身份筛选；同一 id 的多个有效授权，按 tools_grant.ord、tools_role_group.ord 成对选取最靠前的位置并去重，再按这两个权重及 id 生成 int 类型的连续 ord（从 1 开始）。两个原始权重不被修改，返回 ord 仅表示展示顺序，不是稳定身份。MCP 只选择所需约定列，按 ord、id 排序；不公开 itype、remark 或账号。summary/desp 原样保留 Markdown，不自动加类型标题。

- `list_capabilities(offset=0)` 只读取 id、ord、iname、summary、has_desp，structuredContent 返回 items（id、iname、summary、has_desp）、has_more、next_offset，不读取正文。
- `get_capability_details(id)` 每次从同一配置函数参数化筛选当前身份可见的 id，structuredContent 返回 id、iname、summary、has_desp、desp；不新增数据库函数或配置项。不可见 id 返回 access_denied，重复 id 或契约错误返回 capabilities_unavailable。
- 两项工具的 content.text 仅为简短摘要，不重复结构化内容；实际序列化响应受 maxResultSizeKb 限制，单条无法放下时返回 capability_too_large，不交付半份说明。
- 先读取完整摘要目录，再读取所有适用且 has_desp=true 的条目详情；false 时 summary 即完整说明。规则的适用条件由管理员写入 summary。

`list_capabilities_check()` 是无参数标量函数，必须返回非空 SQL bit。程序只接受 bit 1；int 1、文本、NULL 都是契约错误。它只判断当前用户是否有任一有效目录记录，不验证业务对象权限。

管理员 base 函数不对普通用户授权。普通用户须能连接配置库，并有目录函数 SELECT 与检查函数 EXECUTE。不得为了方便读取而授予配置库 db_datareader 或 schema 级 SELECT。实际 SQL 权限仍由 SQL Server 决定。

## 授权角色组排序

首次建表包含 tools_grant.ord int NOT NULL DEFAULT (0)。已有环境先由管理员执行 [授权排序迁移](migrate-capability-grant-order.sql)，再部署 [目录函数](create-capabilities-functions.sql)。迁移不覆盖已有权重；默认全为 0 时保持原来的组内 ord、id 排序效果。无需修改 MCP 配置或接口。

例如 A 组授权 ord=1、组内 X=30，B 组授权 ord=2、组内 X=1，则 X 取 (1,30)，不能分别取 MIN 拼成 (1,1)。相同授权权重时继续比较组内权重，相同两层权重时按能力 id。

验证覆盖跨组重复、相同权重、停用授权／能力、其他账号隔离、分页及全零默认权重。新增窗口排序的真实执行计划和迁移执行仍需在部署环境验证。回退函数时可保留新增列；不删除管理员已维护的权重。

## 运行行为

`tools_role_group.active` 控制单条角色组能力关联，默认 1。已有环境先由管理员执行 [关联启用字段迁移](migrate-capability-role-active.sql)，再部署目录函数。有效能力要求授权、角色组关联和能力本身三层 active 均为 1；同一能力有其他有效路径时仍保留，并按剩余路径排序。目录、详情、入口检查和 procedure 目录授权复用此过滤规则，不修改 SQL Server 本身的权限。MCP 无需调整接口或配置；全部路径停用后按既有空目录／拒绝规则处理。

启用目录的 AD 连接，所有七个工具每次调用都先检查，再执行实际工具；即使 Agent 跳过目录或提交无效业务参数，仍先受统一检查。

检查返回 false、AD 首页目录为空或明确缺少 SQL 权限时返回 `access_denied`。检查超时、连接故障返回 `access_check_unavailable`，本次业务操作不执行，可稍后重试；函数缺失或契约错误使用相同 code，但提示联系管理员。调用方取消返回 `canceled`，不自动重试。目录读取故障返回 `capabilities_unavailable`，停止本次业务操作。所有错误仅提供通用说明，内部日志记录错误类型和 SQL 错误号，不记录凭证或说明正文。检查通过后业务操作仍可能因自身 SQL 权限不足而失败。

check 和实际操作按先后执行，各自使用现有连接池与并发门，不并行占用额外连接，不长期保持检查连接；本版不改变原工具连接生命周期。两次操作并非原子事务，已放行操作不因随后撤权而自动取消。不同数据库访问需要的成本应在部署环境测量。

SQL 密码模式不做这项前置检查；目录读取失败返回一般的目录不可用错误，不泄露底层详情。

Agent 首次调用 list_capabilities 不需参数，续取只传 `offset`。页大小由配置决定；structuredContent 包含 has_more 和 next_offset。以完整记录为单位分页，单条过长时明确报错。首次空目录对 AD 拒绝，越界 offset 返回结束页。分页期间数据变化可能重复或遗漏，必要时从 0 重读。

## 测试与部署边界

测试只允许启动本项目构建的 MCP，禁止使用 Codex 已配置的真实 MCP。自动协议测试使用临时配置和不可连接的本机地址，不读取真实配置。其他单元测试使用替代存储验证 false、异常、恢复授权、分页等行为。

后续由管理员配置开发中 MCP，再验证真实函数结果、AD 权限拒绝及执行计划。脚本 [check-capability-permissions.sql](check-capability-permissions.sql) 仅供管理员在配置库手动诊断，不通过普通 MCP 执行，不更改授权；输出有效权限差异、未验证项和扫描范围。真实数据库联调前，不将语法检查视为权限或性能验证。

初始 capabilities 实现验证记录（以下哈希仅标识当时产物）：

- 构建零警告、零错误；155 项测试中 151 项通过，4 项真实数据库测试因未配置而跳过。
- 沙箱命名管道限制导致 dotnet test 包装进程无法连接，改为直接运行项目编译的 xUnit 测试程序集，完整测试通过。
- 四个平台的本地构建产物已生成。NuGet 审计源不可访问，最后一次构建只在进程环境中临时关闭审计并使用已有依赖，未改项目审计配置；不代表漏洞审计通过。
- 摘要升级前的 `publish/win-x64/sqlserver-readonly-mcp.exe` 曾通过独立 stdio 验证：当时注册六项工具、AD 检查连接失败统一拒绝。使用临时测试配置及 `127.0.0.1:1`，未使用真实 MCP 或数据库配置。
- Windows 产物 SHA-256：`A49AF117698B10328734517831DA7595F1DD618687FE6B270CC135646F84DCF4`。
- 函数及管理员核查脚本通过 ScriptDom 语法解析，未在数据库执行；实际权限、跨库定序和查询计划仍待联调。

移除默认库依赖后的补充验证：

- 160 项自动测试中 156 项通过，4 项通用数据库联调测试跳过；新增缺失目标库拒绝、连接参数隔离和旧 defaultDatabase 字段忽略测试。
- 另通过最新项目开发中 MCP 和未修改的本地 AD 配置进行实际只读验证：目录返回 1 条 SKILL，check 返回 SQL bit true，DB_NAME() 与明确指定的目标数据库一致。
- 配置文件测试前后哈希一致；未修改实际表、函数或授权，未调用 Codex 已配置的真实 MCP。

## Procedure 强制授权

未配置 listFunction 时不注册 execute_procedure。配置后所有账号每次执行都直接查询目录函数的 iname，独立于 AD checkFunction、展示分页和 Agent 历史调用，不缓存授权。skill 的 iname 为空字符串（兼容 NULL/空白）；仅当前身份有效 grant 返回完整三段对象名。函数负责这一契约，MCP 不解析说明文字判断授权。

对象名按数据库、schema、对象分别比较，数据库名通过 DB_ID 解析；schema 和对象名使用目标库目录定序（CATALOG_DEFAULT），不使用数据定序，兼容方括号。未匹配返回 access_denied；查询失败返回安全的不可用/取消状态，不执行业务调用。授权通过后仍核验真实用户 procedure、系统同名冲突及 EXECUTE 权限。检查与执行不是原子事务。无需为 grant 增加对象类型字段。

## 固定提示词

统一指令与工具注册共用 capabilities.Enabled 条件：未启用目录时不提及 list_capabilities、get_capability_details 或 execute_procedure。通用安全与范围规则集中在统一指令；工具说明保留用途及调用衔接；参数说明保留格式、默认值、范围和分页关系。启用目录后先读取完整摘要目录及适用条目的详情，执行 procedure 前仍确认 canExecute=true，参数不明确时读取详情。引用命中须核对 matches 和候选定义，长定义先定位后分段读取。此整理不改变程序授权或 SQL 安全检查。

## 升级

已升级的数据库无需重建。其他环境使用 [分阶段迁移脚本](migrate-capability-summaries.sql)，人工补齐摘要后部署 [最终函数](create-capabilities-functions.sql) 与新版程序。旧 MCP 不能正确消费空 desp，回滚必须恢复兼容的正文、函数和程序组合。迁移不自动复制正文作为摘要，也不更改授权。

## 摘要/详情升级验证（2026-09-10）

188 项本地单元/协议测试及 11 项真实只读联调通过。当前配置函数的摘要与详情响应、SQL bit 判定、查询部分失败统计均通过本项目开发 MCP 或项目 SQL 测试验证；未执行业务 procedure 或更改数据库。完整边界与产物哈希见 [实施记录](cb-practice-improvement-plan.md#11-实施记录2026-09-10)。
