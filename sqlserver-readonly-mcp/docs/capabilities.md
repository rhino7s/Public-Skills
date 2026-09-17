# 业务能力目录

当前源码支持，已完成开发中 MCP 的 AD 目录与 check 成功路径验证。其他账号、权限组合及查询计划仍需按部署环境验证。访问模式和本轮验证见 [访问模式](access-modes.md)、[验证记录](access-modes-validation.md)。

## 配置

在本机配置增加以下部分，将两个名称替换为已建立函数的实际三段名：

```json
"capabilities": {
  "listFunction": "ConfigDB.dbo.list_capabilities",
  "checkFunction": "ConfigDB.dbo.list_capabilities_check",
  "pageSize": 100
}
```

- development 模式不配置目录时仅开放四项通用工具；catalog 模式缺目录则启动失败。
- catalog 模式（AD 或 SQL 密码）必须配置两个函数，每次调用都执行入口检查。development 模式只要求可选的 listFunction，不执行入口 check；procedure 仍需目录授权。
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

catalog 模式的四个工具每次调用都先检查，再执行实际工具；即使 Agent 跳过目录或提交无效业务参数，仍先受统一检查。

检查返回 false、catalog 首页目录为空或明确缺少 SQL 权限时拒绝访问。检查或目录读取失败时停止本次业务操作；检查通过后，业务操作仍可能因实际数据库权限不足而失败。错误类别、对外提示及重试规则统一见 [错误响应](access-modes.md#错误响应)。

check 和实际操作按先后执行，各自使用现有连接池与并发门，不并行占用额外连接，不长期保持检查连接；本版不改变原工具连接生命周期。两次操作并非原子事务，已放行操作不因随后撤权而自动取消。不同数据库访问需要的成本应在部署环境测量。

development 模式不做这项前置检查；目录读取失败返回一般的目录不可用错误，不泄露底层详情。

Agent 首次调用 list_capabilities 不需参数，续取只传 `offset`。页大小由配置决定；structuredContent 包含 has_more 和 next_offset。以完整记录为单位分页，单条过长时明确报错。首次空目录对 catalog 拒绝，越界 offset 返回结束页。分页期间数据变化可能重复或遗漏，必要时从 0 重读。

## 测试与部署边界

测试只允许启动本项目构建的 MCP，禁止使用 Codex 已配置的真实 MCP。自动协议测试使用临时配置和不可连接的本机地址，不读取真实配置。其他单元测试使用替代存储验证 false、异常、恢复授权、分页等行为。

后续由管理员配置开发中 MCP，再验证真实函数结果、AD 权限拒绝及执行计划。脚本 [check-capability-permissions.sql](check-capability-permissions.sql) 仅供管理员在配置库手动诊断，不通过普通 MCP 执行，不更改授权；输出有效权限差异、未验证项和扫描范围。真实数据库联调前，不将语法检查视为权限或性能验证。

历史验证与待验收事项集中在 [验证记录](access-modes-validation.md)。

## Procedure 强制授权

未配置 listFunction 时不注册 execute_procedure。配置后所有账号每次执行都直接查询目录函数的 iname，独立于 AD checkFunction、展示分页和 Agent 历史调用，不缓存授权。skill 的 iname 为空字符串（兼容 NULL/空白）；仅当前身份有效 grant 返回完整三段对象名。函数负责这一契约，MCP 不解析说明文字判断授权。

对象名按数据库、schema、对象分别比较，数据库名通过 DB_ID 解析；schema 和对象名使用目标库目录定序（CATALOG_DEFAULT），不使用数据定序，兼容方括号。未匹配返回 access_denied；查询失败返回安全的不可用/取消状态，不执行业务调用。授权通过后仍核验真实用户 procedure、系统同名冲突及 EXECUTE 权限。检查与执行不是原子事务。无需为 grant 增加对象类型字段。

## 提示词

实际统一指令、工具及参数说明统一维护于 [工具与参数说明](tool-prompts.md)；工具开放范围及授权规则见 [访问模式](access-modes.md)。

## 目录维护

| 项目 | 维护要求 |
|---|---|
| tools_info | skill 的 iname 为空；grant 的 iname 为完整三段对象名，同一业务对象复用既有条目；remark 不对外返回 |
| tools_role_group | 同一 rname、tool_id 不重复；active 控制该条关联 |
| tools_grant | 同一 u_name、rname 不重复；u_name 使用实际 ORIGINAL_LOGIN()，共用 SQL Login 的用户共用目录 |
| id / ord | id 是稳定标识；ord 仅控制展示顺序 |
| 身份与角色变更 | 管理员同步维护授权及角色关联，检查回收后重新使用的账号名；数据库权限仍需单独配置 |

表结构及约束以 [首次建表脚本](create-capabilities-tables.sql) 为准，目录函数以 [函数脚本](create-capabilities-functions.sql) 为准。脚本由管理员按环境执行，不自动创建权限。

## 升级

已升级的数据库无需重建。其他环境按以下顺序迁移：

1. 使用 [摘要迁移脚本](migrate-capability-summaries.sql) 增加可空 summary。
2. 人工补齐全部记录（包括停用记录）的摘要，检查后改为 NOT NULL；不自动复制正文作为摘要。
3. 旧 MCP 仍在使用时，保留其兼容的目录函数输出及非空 desp。
4. 配套部署 [最终函数](create-capabilities-functions.sql) 与新版 MCP 后，再使用空 desp。

回滚须恢复兼容的程序、函数与正文组合；已有空 desp 时不能只换回旧程序。迁移不更改授权。排序和关联 active 字段的迁移见上文。

catalog 的 execute_sql 使用同一有效目录，按本批次直接对象集合匹配 iname；不读取 desp、不受展示分页限制、不递归检查模块依赖。完整规则见 [访问模式](access-modes.md)。
