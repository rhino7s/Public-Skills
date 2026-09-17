# 访问模式、迁移与 SQL 支持范围

## 配置与升级

认证决定用谁的数据库身份连接；访问模式决定 MCP 开放什么能力。

| 认证 | access.mode | 结果 |
|---|---|---|
| windowsIntegrated | 默认（省略/null/空白）或 catalog | 目录受限 |
| windowsIntegrated | development | 启动错误，不降级 |
| sqlPassword | 默认（省略/null/空白）或 development | 开发查询 |
| sqlPassword | catalog | 目录受限，身份仍是 SQL Login |

目录受限配置示例（名称由管理员提供）：

```json
"access": { "mode": "catalog" },
"capabilities": {
  "listFunction": "ConfigDB.dbo.list_capabilities",
  "checkFunction": "ConfigDB.dbo.list_capabilities_check",
  "pageSize": 100
}
```

AD 还须将 authentication 设为 windowsIntegrated，username/password 清空。随包示例采用 SQL 密码 development；改为 AD 时必须同步删除或更改该 mode。AD 旧配置缺目录时，新版拒绝启动。SQL 密码旧配置省略 mode 仍是 development。无需更改既有目录函数签名或新增数据库字段；配置改变后重启，数据库授权改变则下一次调用生效。

## MCP 工具矩阵

| MCP tool | catalog | development（目录禁用） | development（目录启用） |
|---|:---:|:---:|:---:|
| execute_sql | 提供 | 提供 | 提供 |
| execute_procedure | 提供 | — | 提供 |
| find_object | — | 提供 | 提供 |
| get_object_details | — | 提供 | 提供 |
| find_object_references | — | 提供 | 提供 |
| list_capabilities | 提供 | — | 提供 |
| get_capability_details | 提供 | — | 提供 |
| **合计** | **4** | **4** | **7** |

“目录启用”指配置 capabilities.listFunction；catalog 必须同时配置 listFunction 和 checkFunction。

| 区分项 | 含义 |
|---|---|
| MCP tool | 上表中的固定工具接口 |
| list_capabilities / get_capability_details | 读取能力摘要／详情的两个固定工具 |
| list 中的 grant／skill、summary／desp | 工具返回的动态业务内容，不是新增 MCP tool |

## 提示词层次

完整原文见 [MCP 工具与参数说明](tool-prompts.md)：三种配置的完整统一指令、全部工具说明与参数说明。

三层提示叠加生效，不互相替代；不包含 list 返回的动态业务内容。

| 层次 | 内容结构 | 模式差异 | 源文件 |
|---|---|---|---|
| initialize.instructions | 目录阅读流程、查询范围、结果及失败处理 | 目录启用时包含摘要与详情阅读流程；不介绍模式或认证配置 | McpServerInstructions.cs |
| 工具 description | 用途、使用方式、范围／限制、结果处理 | execute_sql 按访问模式提供定义；其他工具正文共用 | Tools/*.cs |
| 参数 description | 参数格式、范围、默认值、分页规则 | execute_procedure 的 sql 参数按访问模式提供一份适用说明；其余随工具定义提供 | Tools/*.cs |

| 执行入口 | catalog | development |
|---|---|---|
| execute_sql | 完整批次所有直接对象均须获授 | 原通用查询规则与数据库权限，不检查对象 list 白名单 |
| execute_procedure | 目录授权、类型及 EXECUTE 权限核验 | 有目录才提供，同样三项核验 |

## 授权与详情

每次 catalog 工具调用先检查当前身份是否仍有入口资格。execute_sql 再解析完整批次，收集直接持久化对象；全部对象匹配当前有效 list grant、通过类型及实际数据库权限核验，才执行业务 SQL。对象授权按目标库分组批量参数化查询，不读取完整目录、desp 或展示分页，不缓存结果。参数化的是对象名称，业务参数值不参与权限匹配。

正常使用应先读摘要及适用详情。程序不记录“已读 desp”状态；偶发缺正文或跳过详情不改变有效 grant 的授权结果。无 grant 时不能读取对应详情，受目录约束的调用也拒绝。procedure 的强制检查由程序内部执行，Agent 无需先调用元数据工具。

持久化对象必须写 `database.schema.object`，允许 SQL Server 的方括号和转义标识符。数据库名由 SQL Server 解析，schema/name 按目标库目录定序匹配；不以 C# 忽略大小写替代数据库规则。目录名称也必须可解析为完整三段名。

只管直接入口，不递归展开 view、function、procedure 的内部依赖。授权 A view 后可直接查 A；若 SQL 同时直接 JOIN 其未获授底表 B，整个批次拒绝。此机制仅控制通过本 MCP 的使用；数据库公共角色仍需要由管理员提供入口和跨库依赖的实际权限，其他 SQL 客户端不受 MCP list 限制。

对象授权不自动限制业务参数、基金、行或列；需要细分数据范围时，应使用专门的业务入口或另行设计数据权限。获授权模块的内部逻辑及更新属于管理员信任范围。

目录身份取自真实连接的 ORIGINAL_LOGIN()，不接受客户端传入用户名替代身份。多人共用 SQL Login 时共用同一目录，不能据此区分个人。本地程序和配置不构成防篡改边界；本实现不使用服务账号代理，也不自动创建公共角色。

## catalog SQL 支持范围

| 范围 | 规则 |
|---|---|
| 持久化来源 | 本实例用户 table、view、SQL 标量/表值函数；每个直接入口都需授权 |
| 查询结构 | SELECT、JOIN、APPLY、UNION、子查询、CTE、派生表、排序、分页、合法窗口定义 |
| 常见计算 | SUM/COUNT/AVG/MIN/MAX、标准统计聚合；ROW_NUMBER/RANK/DENSE_RANK/NTILE、LAG/LEAD/FIRST_VALUE/LAST_VALUE 等窗口函数；常规日期、数值、字符串函数 |
| 特殊语法 | CAST/CONVERT/TRY_CAST/TRY_CONVERT、COALESCE、NULLIF、CASE、CURRENT_TIMESTAMP；其中用户函数仍收集授权 |
| 临时对象 | 本次调用创建的 #临时表、@表变量；允许局部写入，所有来源与表达式仍校验 |
| 无持久化来源 | 有入口资格即可执行 SELECT 常量和局部计算 |
| 不支持 | 一/两段持久化名、四段远程名、synonym、CLR、序列、用户定义类型、XML schema 集合、外键引用、分区函数、系统目录/信息函数、查询/表提示、OPENQUERY/OPENJSON 等扩展表源、GO、动态执行及持久化写入 |

内置函数采用显式清单，完整实现见 `Security/CatalogSqlAnalyzer.cs` 的 Functions 集合。未知函数/扩展表源/语句会拒绝；不能仅凭语法树解析成功放行。开发模式沿用原有 SQL 安全支持范围，不套用上表中的 catalog 专用限制。

CTE/临时对象名称按精确字符绑定，不能确定局部绑定时拒绝；这可能比不区分大小写数据库更严格，请使用一致拼写。#临时对象不能跨调用复用，不能通过 tempdb 三段名豁免授权。持久化写入及用 #别名伪装写入目标继续由原安全分析器拦截。

## 限制、连接与日志

- 每批最多 128 个精确去重的直接对象、262144 个 SQL 字符。重复引用不重复计数；大小写不同的写法保守分开计数，最终授权仍由 SQL Server 定序决定。
- 入口检查、解析、对象授权及元数据核验共用 15 秒执行前预算，包含排队和连接。解析器为同步实现，字符上限限制输入规模，取消在解析前后及遍历中检查；不承诺解析瞬间可中断。
- 业务阶段共用 query.timeoutSeconds 总预算，包括排队、连接和结果读取；procedure 的连接及核验位于执行前阶段。
- 连接默认 5 秒，可用现有 connectTimeoutSeconds 调整。ConnectRetryCount=0，无应用重试、后台检测或熔断配置。取消及连接清理可能稍晚结束，不承诺精确秒数。
- 连接失败对用户显示“连接失败，请确认网络连接后再试。”，不返回内部连接诊断。Agent 停止本轮操作，用户明确重试时再发起新调用。procedure 已提交后失联仍为 execution_unknown，不自动重放。
- 既有每次调用的审计记录统一增加 parse_ms、authorization_ms、metadata_ms、execution_ms、total_ms，单位毫秒；阶段不重叠，多次同类操作累计。未经过阶段为 null。目录/元数据工具本体计入 execution_ms，入口 check 计入 authorization_ms。
- 不新增日志开关、不增加逐对象记录或数据库往返。includeSqlText 及日志权限要求维持。审计是工具结果生成时的快照，不含向客户端传输或其显示耗时；字段不加入业务输出协议。

超限/撤权/核验故障在执行前拒绝整个批次。业务 SQL 真正执行后发生运行期错误，仍保留已交付的前面结果，并报告调用失败；不能把这些结果视为完整响应。检查与执行不是原子事务，已经放行的在途请求不会因之后撤权自动取消。

## 部分结果与统计

- 运行期失败保留已交付的结果集；正在读取、尚未交付的结果集不计入响应。已交付结果的截断标志保留。
- returnedRows 只统计实际交付的行；resultSizeBytes 按实际 resultSets 的 UTF-8 序列化字节计算。没有结果集时为 0，空结果集仍包含列信息。
- 失败且已有结果时明确提示部分结果；空行结果或正常截断本身不等于执行失败。procedure 完成状态未知时仍按 execution_unknown 处理，不自动重试。

## 错误响应

文字响应与结构化错误使用同一说明。参数和语法错误保留调用方修正输入所需的信息；SQL Server 原始异常、内部依赖对象及 SQL 错误编号不在对外错误中返回。原有错误字段保持不变，SQL 诊断字段对外为 null，内部审计维持原分类及现有诊断记录。

| 情形 | 对外行为 |
|---|---|
| 明确的目录拒绝、数据库权限不足 | 统一 access_denied／“用户没有访问权限” |
| 连接或登录失败 | “连接失败，请确认网络连接后再试。”；保留原有类别 |
| 无法完成授权检查、无法读取目录 | 保留 access_check_unavailable／capabilities_unavailable；不冒充权限拒绝或网络故障 |
| 参数、语法及操作范围限制 | 保留现有类别与具体修正规则 |
| SQL 运行期错误、程序异常 | 固定的简洁说明，不透传异常原文 |
| procedure 已提交而完成状态未知 | execution_unknown 优先于底层原因；不自动重试 |

已经交付的部分结果、统计、截断及续页信息保持不变。未知工具仍由 MCP 协议层处理，不新增分类或改变授权流程。

## 文档与维护

| 文档 | 职责 |
|---|---|
| 本文 | 配置、工具矩阵、授权边界、SQL 支持范围、超时与日志 |
| [工具与参数说明](tool-prompts.md) | 与源码同步的实际提示词正文及参数说明 |
| [验证记录](access-modes-validation.md) | 已完成的验证、结果限制与待验收事项 |

提示词调整应同步工具和参数 Description、本文对应规则及提示词文档，并验证各配置的 initialize.instructions 和 tools/list 输出。已实施的计划与修订稿不再单独维护。

## 验证

本轮可复现结果及环境阻塞见 [验证记录](access-modes-validation.md)。真实环境测试仅使用项目构建产物；数据库角色配置、测试对象创建及授权调整交由管理员，不由 MCP 升级自动修改。

access.mode 的空值规则：省略、null、空字符串、纯空白均使用 authentication 对应默认模式；AD 为 catalog，SQL 密码为 development。非空值仅接受 catalog/development，AD 显式 development 仍拒绝。
