# MCP 工具与参数说明

本文记录程序提供的统一指令、工具正文与参数说明，不包含能力目录中的动态业务内容。

标题和适用条件供管理员审阅，不发送给 Agent。实际只提供当前配置适用的文本。认证配置、工具矩阵及授权规则统一见 [访问模式](access-modes.md)。

## 1. 统一指令

以下按三种配置列出完整的提示词。标题用于标注适用配置；代码块内为提示词正文。

### catalog

```text
list_capabilities 是当前授权的“能力目录”，业务操作前读取完整“能力目录”摘要，选择适用条目；has_desp=true 时读取 get_capability_details 并遵守说明。
按用户需求限定查询范围。
禁止自行编写持久化修改 SQL。
结果截断或调用失败时，说明已返回结果的范围与限制。
无法连接时，提示“连接失败，请确认网络连接后再试。”，不自动重试。
访问被拒绝时不得绕过限制。
```

### development（目录禁用）

```text
按用户需求限定查询范围。
禁止自行编写持久化修改 SQL。
结果截断或调用失败时，说明已返回结果的范围与限制。
无法连接时，提示“连接失败，请确认网络连接后再试。”，不自动重试。
访问被拒绝时不得绕过限制。
```

### development（目录启用）

```text
list_capabilities 是当前授权的“能力目录”，业务操作前读取完整“能力目录”摘要，选择适用条目；has_desp=true 时读取 get_capability_details 并遵守说明。
按用户需求限定查询范围。
禁止自行编写持久化修改 SQL。
结果截断或调用失败时，说明已返回结果的范围与限制。
无法连接时，提示“连接失败，请确认网络连接后再试。”，不自动重试。
访问被拒绝时不得绕过限制。
```

procedure 的目录限制由其工具说明表达，不在统一指令重复。

## 2. 工具与参数说明

表中“说明”列为提供给 Agent 的参数说明；类型和默认值按现有接口列出。适用条件只供管理员选择对应文本，不写入参数说明。

### execute_sql — catalog

```text
执行只读 SQL；直接引用的持久化数据库对象必须在“能力目录”授权范围内。
支持 JOIN、聚合、CTE、窗口函数、本地临时表和表变量；临时对象仅在本次调用内有效。
不支持 EXEC、持久化修改、全局临时表、系统信息查询、远程数据源及查询提示或表提示（包括 NOLOCK）。
```

| 参数 | 类型／默认值 | 说明 |
|---|---|---|
| sql | string，必填 | 只读 T-SQL 批次；持久化对象使用 database.schema.object 三段名。 |
| database | string，必填 | 连接的初始数据库；SQL 可跨库引用已授权对象。 |

### execute_sql — development（目录禁用）、development（目录启用）

```text
执行只读 T-SQL 批次，支持本地临时表和表变量。每次调用使用独立会话。
不支持 EXEC、持久化修改、全局临时表及远程数据源。
```

| 参数 | 类型／默认值 | 说明 |
|---|---|---|
| sql | string，必填 | 只读 T-SQL 批次。 |
| database | string，必填 | 连接的初始数据库；SQL 可使用 database.schema.object 跨库查询。 |

### execute_procedure — catalog、development（目录启用）

```text
执行“能力目录”中已授权且用户要求的业务 procedure。
execution_unknown 表示执行完成状态未确认，不得自动重试；不得为补取截断结果而重复执行。
```

| 参数 | 类型／默认值 | 说明 |
|---|---|---|
| database | string，必填 | procedure 所在数据库；须与 SQL 中显式指定的数据库一致。 |

`sql` 为 string，必填。按适用条件选择以下一份参数说明：

| 适用条件（仅管理员） | 说明 |
|---|---|
| catalog | 单条静态 EXEC 调用，含业务参数；对象使用 database.schema.procedure 三段名。不支持动态 SQL、变量过程名及远程调用。 |
| development（目录启用） | 单条静态 EXEC 调用，含业务参数；可省略数据库和 schema，省略 schema 使用 dbo。不支持动态 SQL、变量过程名及远程调用。 |

### find_object — development（目录禁用）、development（目录启用）

```text
在指定数据库和 schema 中按名称查找对象，最多返回 20 项。
仅返回可见对象；procedure 的 canExecute 表示数据库执行权限。
```

| 参数 | 类型／默认值 | 说明 |
|---|---|---|
| objectName | string，必填 | 对象名、schema.object 或 database.schema.object；省略 schema 使用 dbo。显式数据库名须与 database 一致。 |
| database | string，必填 | 搜索对象的数据库。 |
| objectTypes | string?，null | 类型筛选，逗号分隔：table、view、procedure、function，或 SQL Server 对象类型代码；省略时不限类型。 |
| exactMatch | bool，true | true 为对象名精确匹配；false 为包含匹配，关键词至少 3 字符。schema 始终精确匹配。 |

### get_object_details — development（目录禁用）、development（目录启用）

```text
读取对象的字段、索引、参数、权限及定义。
指定 definitionSearch 时改为定义关键词搜索，返回匹配行，不返回完整字段、索引及参数。
```

| 参数 | 类型／默认值 | 说明 |
|---|---|---|
| objectName | string，必填 | 对象名、schema.object 或 database.schema.object；省略 schema 使用 dbo。 |
| database | string，必填 | 对象所在数据库；须与 objectName 中显式指定的数据库一致。 |
| startLine | int，1 | 定义起始行，1 起算；续页位置为 nextStartLine。definitionSearch 非空时不使用。 |
| maxLines | int，200 | 最多返回的定义行数，范围 1–800；definitionSearch 非空时不使用。 |
| definitionSearch | string?，null | 定义搜索关键词，不区分大小写；去除首尾空白后最多 256 字符。空白或省略时按 startLine/maxLines 返回定义。 |
| matchOffset | int，0 | 跳过的定义匹配行数；续页位置为 nextMatchOffset。仅用于关键词搜索。 |
| maxMatches | int，20 | 最多返回的定义匹配行数，范围 1–20；仅用于关键词搜索。 |

### find_object_references — development（目录禁用）、development（目录启用）

```text
查找目标对象在指定数据库模块定义中的文本引用候选，可附加搜索 SQL Agent Job Step。
文本命中不区分读、写、执行，可能包含注释、自身定义或动态 SQL；不是完整依赖关系。
```

| 参数 | 类型／默认值 | 说明 |
|---|---|---|
| targetDatabase | string，必填 | 目标对象所在数据库。 |
| targetObject | string，必填 | 对象名或 schema.object，省略 schema 使用 dbo；须为现有 table、view、procedure 或 function。 |
| searchDatabase | string，必填 | 搜索模块定义的数据库。同库匹配 schema.object，目标名至少 4 字符时也匹配裸对象名；跨库只匹配 database.schema.object。 |
| sourceTypes | string?，null | 来源模块类型，逗号分隔：procedure、function、view、trigger；省略时搜索全部四类。 |
| includeJobs | bool，false | 是否附加搜索当前实例的 SQL Agent Job Step；最多 20 项，不受 offset/limit 控制。 |
| offset | int，0 | 跳过的模块候选数，范围 0–1000；续页位置为 nextOffset。 |
| limit | int，50 | 最多返回的模块候选数，范围 1–50。 |

### list_capabilities — catalog、development（目录启用）

标题：读取能力目录摘要。

```text
返回“能力目录”摘要：id、iname、summary、has_desp；iname 为业务对象名称，说明类条目为空。
分页字段为 has_more 和 next_offset。
```

| 参数 | 类型／默认值 | 说明 |
|---|---|---|
| offset | long，0 | 跳过的摘要条数；续页位置为 next_offset。 |

### get_capability_details — catalog、development（目录启用）

```text
返回“能力目录”中指定条目的完整说明 desp；没有正文时 has_desp=false。
```

| 参数 | 类型／默认值 | 说明 |
|---|---|---|
| id | int，必填 | list_capabilities 返回的条目 id，正整数。 |
