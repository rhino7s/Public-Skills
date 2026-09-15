# 用户能力目录实施方案

状态：核心 MCP 配置、工具注册、统一前置检查与摘要分页已实现；自动测试通过后仍需管理员配置进行真实环境联调。运行方式见 [使用说明](capabilities.md)。配套 [建表脚本](create-capabilities-tables.sql)、[函数创建脚本](create-capabilities-functions.sql) 和 [权限核查脚本](check-capability-permissions.sql) 不自动部署或授权；用户已自行建立表和函数，本次不更改数据库。

## 目标与边界

保留现有通用 SQL Server MCP，通过可选的数据库函数提供当前用户的业务说明，并对启用目录的 Windows 集成认证用户执行统一访问检查。

- 不配置目录时，保持现有行为，不注册 `list_capabilities`。
- 启用目录后，AD 与 SQL Login 的规则均由管理员维护的目录内容提供。普通用户和开发人员通过分配不同说明表达使用范围，不另设两套程序。
- 后台 `skill`、`grant` 都是 Agent 使用的知识内容。类型仅用于后台维护、对象关联及后续权限审阅，Agent 不需要辨别类型。
- SQL Server 负责实际数据库权限。目录不会自动 GRANT、REVOKE 或 DENY。
- AD 前置检查是用户级入口控制，不是逐对象白名单。一条有效说明即能通过检查；具体业务范围由说明引导，SQL 权限仍决定实际访问；execute_procedure 另对所有账号实施逐对象目录授权。
- 本地配置可被修改时，用户可以关闭目录；本机制不能代替数据库权限，也不能限制其他数据库客户端。

## 配置与认证

```json
{
  "capabilities": {
    "listFunction": "ConfigDB.dbo.list_capabilities",
    "checkFunction": "ConfigDB.dbo.list_capabilities_check",
    "pageSize": 100
  }
}
```

- 配置可整体省略。`listFunction` 和 `checkFunction` 都为空时关闭目录。
- `listFunction` 配置后注册目录工具；Windows 集成认证同时要求 `checkFunction`，缺失则启动报配置错误。
- SQL 密码认证可省略检查函数；即使设置，也不执行 AD 专属检查。
- 禁止只设置检查函数。函数配置仅接受当前实例的三段对象名，schema 固定 `dbo`，不接受 SQL 片段或四段名称。
- `pageSize` 默认为 100，要求正整数；它是记录数上限，仍受现有响应大小限制。Agent 不能覆盖该值。
- 配置修改后重启生效。无目录缓存、检查缓存、版本控制或热更新。
- 用户名保存实际登录名：AD 使用完整 `DOMAIN\\user`，SQL 认证使用 SQL Login 名称，不使用数据库用户别名。账号注销或名称复用时由管理员清理旧配置。
- 所有公开函数从 `ORIGINAL_LOGIN()` 获取身份，不接受 Agent 指定用户名。

## 配置表

表放在独立配置数据库的 `dbo` 下。管理员在执行脚本前选择并确认目标配置数据库。当前建表文件保留管理员手工编辑的查询及 RETURN；部署时应选中实际 DDL 区段，并排除文件末尾的孤立字符 `s`，不可直接把整个文件作为自动部署脚本。

| 表 | 字段及用途 |
| --- | --- |
| `tools_info` | `id int identity`；`itype varchar(5)`；`iname varchar(150)`；`summary nvarchar(1000) NOT NULL`；`desp nvarchar(max) NOT NULL`；内部 `remark nvarchar(max)`；`upd_time datetime DEFAULT GETDATE()`；`active bit` |
| `tools_role_group` | `rid int identity`；`rname varchar(50)`；`tool_id int`；`ord int NOT NULL`，管理员显式填写；`active bit NOT NULL DEFAULT (1)`；内部 `remark`；`upd_time` |
| `tools_grant` | `gid int identity`；`u_name nvarchar(128)`；`rname varchar(50)`；`ord int NOT NULL DEFAULT (0)`；内部 `remark`；`upd_time`；`active bit` |

约束和维护规则：

- `itype` 仅为小写 `skill` 或 `grant`。`skill.iname` 必须为空字符串；`grant.iname` 为完整 `数据库.dbo.对象`，总长度不超过 150。
- 一项对象仅一笔 `grant`，停用后重新启用原记录；不同 `skill` 通过 id 区分。
- `(rname, tool_id)` 唯一，`tool_id` 外键关联 `tools_info.id`；不做级联删除。
- `(u_name, rname)` 唯一。`rname` 在角色成员表中不唯一，因此不建立 `tools_grant.rname` 外键；孤立角色交给后续检查脚本报告。
- 角色成员表不设 active，不用即删除；两张有 active 的表默认启用。
- 不做角色更名同步、更新时间触发器。更新时管理员自行维护 upd_time；时间只供维护追踪。
- `remark` 永不进入公开函数结果及 Agent 内容。
- 对象存在性、支持类型和跨数据库名称语义交给后续核查；简化后的建表脚本不使用 CHECK 验证类型、名称形态或说明，管理员按上述规则维护。目录函数忽略 skill/grant 之外的类型，检查函数复用同样规则。名称引用应使用规范形式，避免同一对象的不同拼写形成重复条目。
- 目录库定序影响用户名、角色名和对象名的唯一性比较。部署前确认与实例及业务库名称语义兼容；不通过统一转小写合并不同对象。

## 有效能力与顺序

有效记录必须同时满足 `tools_grant.active=1`、`tools_role_group.active=1` 和 `tools_info.active=1`，通过 rname、tool_id 关联。停用单条角色组关联仅影响经该关联授予的能力；同一能力仍有其他有效授权路径时继续可见，排序从剩余有效路径选取。

- 多角色重复赋予同一 id，按 `tools_grant.ord → tools_role_group.ord` 成对选取最靠前位置，合并为一条；禁止分别 MIN 后拼接。
- 两张表的原始 ord 可为任意 int（含负数、重复值和不连续值），分别作为角色组及组内排序权重。
- 去重后以 `(grant ord ASC, role group ord ASC, id ASC)` 生成最终连续序号，转换为 int ord；MCP 继续按 `(ord ASC, id ASC)` 分页。
- 不修改管理员的原始 ord。返回 ord 为从 1 开始的展示序号，不替代稳定 id 或分页 offset；授权变化后展示序号可能变化。已有环境先执行 migrate-capability-grant-order.sql，再部署目录函数。
- 单个角色授权停用但其他有效角色仍赋予同一能力时，该能力仍有效。

## 数据库函数及权限

| 函数 | 形式 | 职责 |
| --- | --- | --- |
| `dbo.list_capabilities_base(@u_name nvarchar(128))` | 表值函数 | 返回指定用户的有效、去重目录；管理员直接调用 |
| `dbo.list_capabilities()` | 无参数表值函数 | 固定传入 ORIGINAL_LOGIN，返回本人目录 |
| `dbo.list_capabilities_check()` | 无参数标量函数，RETURNS bit | 使用相同有效性规则，通过 EXISTS 判断本人是否有任一有效 skill 或 grant |

检查函数不读取完整 desp，不排序，不检查业务对象的 SQL 权限。实现时验证其查询计划，避免物化完整说明；有效性关联规则应复用，避免 check 与 list 各维护一套不同规则。

普通用户需具备配置库有效 CONNECT 权限（可通过已有用户或组映射取得）、目录表值函数 SELECT、检查标量函数 EXECUTE。普通用户不授予 base 或底表直接读取权限，也不授予配置库 db_datareader 或 schema 级 SELECT。函数及底表保持同一所有者，使用同库所有权链；不引入跨库提权或 EXECUTE AS OWNER。

base 只模拟目录，不模拟目标用户实际 SQL 权限。MCP 仅知道配置的两个公开函数，无需知道 base 或任何底表名称。

## 公开目录契约

当前摘要/详情契约以 [使用说明](capabilities.md) 与 [实施计划](cb-practice-improvement-plan.md) 为准：函数返回 id、ord、iname、summary、has_desp、desp 六列。summary 必须非空白；desp 非 NULL，按原文返回，has_desp 按 SQL 的 desp <> N'' 转为 bit，不进行额外空白归一化。两者均不自动添加类型标题。

list_capabilities 只选取摘要字段并返回 structuredContent；get_capability_details(id) 从同一无参数函数筛选当前身份的单条完整说明。content.text 只作简短摘要。remark、itype、账号与角色关联不公开。固定提示词要求先读完整摘要目录，再读适用且有详情的条目；业务范围继续由目录维护。

## 分页与工具行为

目录使用 `list_capabilities(offset=0)`；无用户名、关键词、类型或页大小参数。详情使用 `get_capability_details(id)`，id 为正整数。

- 首次调用不需传参数；后续按响应中的 next_offset 继续读取。
- 最多读取 pageSize 条并额外探测一条以确定 has_more。
- structuredContent 列出本页条数、has_more 和 next_offset（结束为 null），不暗示未读取页面已完整提供。
- 先应用现有响应大小限制，预留分页尾部空间，在完整记录间结束本页；next_offset 按实际返回记录数推进。
- 不静默截断单条说明。下一条本身无法放入空页时明确报错，避免返回零条且反复续取。
- 越界 offset 返回空页且 has_more=false；不能因为当前页为空认定用户无权限。
- 不提供跨调用快照；目录在分页期间改变可能导致跨页遗漏或重复，必要时从 offset=0 重读。

对象类型、参数、字段通过原有 get_object_details 获取；过程执行复用 execute_procedure，表、View 和函数查询复用 execute_sql。

## AD 统一前置检查

所有现有五项工具及目录工具共用访问检查组件，不能靠 Agent 主动先调用目录。内部直接调用配置函数，避免经 execute_sql 递归。

1. 未启用目录或 SQL 密码认证：跳过 AD 检查。
2. 启用目录且 Windows 集成认证：调用无参数 checkFunction。
3. 仅返回非空 bit 1 时放行；0、NULL、查询错误或契约不合法统一拒绝。包括没有配置库 CONNECT、没有检查函数 EXECUTE、函数不存在、超时或连接失败：这些情况在 MCP 前置检查层视为未通过，不能因无法检查而放行。SQL 权限错误不是函数成功返回 false，内部日志须保留这一区别，对外统一拒绝。
4. 放行后执行原工具操作；list_capabilities 再执行分页目录查询。

因此 AD 目录调用是一次 check 查询加一次 list 查询；AD 其他调用是一次 check 加原工具操作。每次调用重新检查，不为检查额外并行占用连接。本版检查与原工具顺序使用连接池，各自释放连接和并发槽，不改变原工具连接生命周期。目录读取也失败时不返回部分说明或底层详情，返回对应的不可用或取消状态并记内部诊断。

对外区分：false 或明确权限不足为 `access_denied`；检查故障为 `access_check_unavailable`（临时故障可稍后重试，配置/契约错误联系管理员）；调用方取消为 `canceled`；目录读取故障为 `capabilities_unavailable`。检查未通过时均不执行业务操作，不允许绕过。仅内部日志记录具体原因，遵守现有日志脱敏规则。初始化和工具发现保持工作；所有业务调用被拦截。不缓存拒绝，恢复授权后下次可重新通过。

检查通过和业务执行不是原子授权事务；撤销后下一次检查拒绝，不自动取消已经放行的操作。check/list 两次查询间也可能发生变化：check 通过后 list 返回空集时，AD 返回统一拒绝（排除分页越界情形），不继续探索其他路径。

## 管理员权限核查（最后阶段）

单独只读诊断脚本，不自动授权撤权、不实际执行业务过程：

- 检查账号、孤立角色、三段名、对象存在性、对象类型及配置与有效权限差异。
- 停用记录先计算多角色最终集合，不能逐行视为撤权要求。
- 表和 View 检查 SELECT，标量函数检查 EXECUTE，表值函数检查 SELECT，过程检查 EXECUTE；部分列授权等情况明确报告，不简单推断全表可读。
- 扫描同实例全部数据库、全部 schema 的业务过程，报告目录外的有效 EXECUTE；系统对象单独分类。
- 考虑角色、AD 组及更高层权限，不只读取显式 GRANT。无法模拟、数据库不可用或权限不足都报告未验证。
- 跨库报告解决定序冲突，实际对象匹配遵循目标数据库定序。
- 开发人员可能只复用说明而无过程权限，报告权限差异而非一律配置错误。

## 实施与验收

### 测试实例边界

Codex 当前已配置真实可用的 MCP，但本项目的验证禁止调用该真实 MCP。所有 MCP 协议、工具和权限行为测试必须通过本项目构建并启动的开发中 MCP 进程执行；不得通过已安装连接器或其他常驻 MCP 代替验证。需要真实数据库验证时，也必须由本项目开发进程使用明确的测试配置连接，并遵守既有集成测试流程。

记录被测程序路径、构建版本或产物哈希及所用配置路径，不输出凭证。修改后重新构建并启动进程，不能把旧进程的测试结果算作当前修改的验证。没有合适测试配置或权限时应报告尚未验证，不转用 Codex 配置的真实 MCP。

### 实施顺序与检查项

1. 已提供方案、三表 DDL、三个函数脚本和最小权限示例；保留用户手工简化的建表文件。
2. 已实现 MCP 配置校验、目录读取、统一检查、可选注册和摘要分页。
3. 已提供自动单元与开发进程协议测试、使用说明和独立管理员核查脚本。
4. 待管理员配置开发中 MCP 后进行真实数据库验证与性能测量；本次不调用已有真实 MCP，不改实际表、函数或权限。

验收覆盖：关闭功能兼容旧行为；AD 所有入口被统一拦截；SQL 账号不误拦截；动态撤销恢复；check 异常拒绝；多角色去重和任意 ord；原文 Markdown 保留；长记录与分页边界；底表和 base 不可直读；无 remark 泄漏；两种目录说明（受控用户、开发人员）；实际检查延迟。

权限失败必须有独立用例：AD 开启目录，检查函数存在但账号没有 EXECUTE 时，所有业务工具均返回 access_denied／用户没有访问权限，且不执行后续目录查询或业务操作；不得退回通用模式或尝试其他身份。实际撤权测试仅在明确用于测试的账号与环境中进行，不修改当前真实 MCP 的账号权限。

建表验证应包括手工选择正确 DDL、首次部署、约束和索引检查；简化脚本不是幂等迁移脚本，不直接重复执行。函数脚本使用 CREATE OR ALTER，要求 SQL Server 2016 SP1 及以上。未实际连接 SQL Server 验证前，不宣称部署或权限隔离已测试通过。

## 执行授权收紧（后续修订，优先于前述通用执行行为）

未配置 listFunction 时隐藏 execute_procedure，保留四项通用工具；配置后增加目录及 procedure 工具。目录函数新增 iname：skill 返回空白，有效 grant 返回当前身份获授权的三段对象名。所有认证模式每次执行都强制查询该字段，无匹配或无法核验均不执行。该查询不受目录分页限制、不使用缓存；授权通过后再核验对象类型与 SQL 权限。无需新增表字段或对象类型列。
