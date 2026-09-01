using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tools;

[McpServerToolType]
public sealed class SqlServerTools
{
    private static readonly JsonSerializerOptions ToolJsonOptions = Program.CreateToolJsonOptions();

    private readonly SqlQueryService _queryService;
    private readonly SqlMetadataService _metadataService;

    public SqlServerTools(SqlQueryService queryService, SqlMetadataService metadataService)
    {
        _queryService = queryService;
        _metadataService = metadataService;
    }

    [McpServerTool(
        Name = "execute_sql",
        Title = "执行 SQL Server 只读查询",
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(QueryResult))]
    [Description(
        "执行受限查询以核对资料或业务逻辑。应指定字段并使用 WHERE、聚合或较小的 TOP 控制范围；" +
        "允许变量、CTE、跨库三段名，以及对本地临时表或表变量写入；" +
        "禁止持久化 DML、嵌套 DML 数据源、DDL、USE、EXEC/EXECUTE（包括 INSERT ... EXEC）、NEXT VALUE FOR、全局临时表，" +
        "以及四段名、OPENQUERY、OPENROWSET、OPENDATASOURCE 等显式远程或 Ad Hoc 数据源。" +
        "精确核对不默认使用 NOLOCK。")]
    public async Task<CallToolResult> ExecuteSqlAsync(
        [Description("完整 T-SQL 查询批次；优先指定字段并控制范围。持久化修改、INSERT ... EXEC、嵌套 DML、序列取号及显式远程或 Ad Hoc 数据源会在连接数据库前被拒绝。")]
        string sql,
        [Description("明确的初始数据库；SQL 内仍可使用 database.schema.object 跨库查询。")]
        string database,
        CancellationToken cancellationToken = default)
    {
        var result = await _queryService.ExecuteAsync(sql, database, cancellationToken).ConfigureAwait(false);
        return CreateToolResult(
            result,
            result.Success,
            result.Success
                ? $"查询完成：返回 {result.ReturnedRows} 行，{result.ResultSets.Count} 个结果集" +
                  (result.Truncated ? $"；结果已截断（{result.TruncationReason}）。" : "。")
                : $"查询失败：{result.Error?.Message}");
    }

    [McpServerTool(
        Name = "execute_procedure",
        Title = "执行已授权的 SQL Server 存储过程",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(QueryResult))]
    [Description(
        "执行一条静态命名、已审核且当前账号 canExecute=true 的存储过程调用。" +
        "过程可能修改资料，仅在用户明确要求对应业务动作时使用；" +
        "禁止动态 SQL、变量过程名、sp_executesql、EXECUTE AS、四段名和远程执行；三段名中的数据库必须与 database 参数一致。")]
    public async Task<CallToolResult> ExecuteProcedureAsync(
        [Description("单条存储过程调用，例如 EXEC dbo.ExampleProcedure 'a', 1；使用 database.schema.procedure 时，数据库必须与 database 参数一致。")]
        string sql,
        [Description("明确的初始数据库；SQL 使用三段名时，其中的数据库必须与此参数一致。")]
        string database,
        CancellationToken cancellationToken = default)
    {
        var result = await _queryService.ExecuteProcedureAsync(sql, database, cancellationToken).ConfigureAwait(false);
        return CreateToolResult(
            result,
            result.Success,
            result.Success
                ? $"过程执行完成：返回 {result.ReturnedRows} 行，{result.ResultSets.Count} 个结果集" +
                  (result.Truncated ? $"；结果已截断（{result.TruncationReason}）。" : "。")
                : $"过程执行失败：{result.Error?.Message}");
    }

    [McpServerTool(
        Name = "find_object",
        Title = "定位 SQL Server 对象",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ObjectSearchResult))]
    [Description(
        "在指定数据库定位明确对象。默认精确匹配，仅在名称不确定时使用模糊匹配；" +
        "省略 schema 时使用 dbo，模糊匹配关键词至少 3 个字符、最多返回 20 项。" +
        "存储过程额外返回 canExecute；无结果只表示当前账号未发现该对象，不能证明对象不存在。")]
    public async Task<CallToolResult> FindObjectAsync(
        [Description("对象名、schema.object 或 database.schema.object；省略 schema 时默认为 dbo。")]
        string objectName,
        [Description("只在该数据库中定位对象。")]
        string database,
        [Description("可选类型，逗号分隔：table, view, procedure, function，或 SQL Server 对象类型代码。")]
        string? objectTypes = null,
        [Description("true 为对象名完全匹配；false 为包含匹配，且关键词至少 3 个字符。")]
        bool exactMatch = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _metadataService
            .FindObjectAsync(objectName, database, objectTypes, exactMatch, cancellationToken)
            .ConfigureAwait(false);
        return CreateToolResult(
            result,
            result.Success,
            result.Success
                ? $"对象定位完成：返回 {result.Objects.Count} 个对象" +
                  (result.Truncated ? "；仍有其他候选。" : "。")
                : $"对象定位失败：{result.Error?.Message}");
    }

    [McpServerTool(
        Name = "find_object_references",
        Title = "查找 SQL Server 对象文本引用候选",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ObjectReferenceSearchResult))]
    [Description(
        "先确认 targetDatabase 中的目标对象，再搜索 searchDatabase 的模块定义；includeJobs=true 时附加搜索 SQL Agent Job Step。" +
        "结果仅是目标对象名称的原始文本命中候选，不判断注释、对象自身定义、动态 SQL 是否执行，也不标注读取、写入或执行类型。" +
        "必须查看 matches 并按需读取候选定义后再判断，不得直接称为实际调用方或完整血缘。" +
        "来源模块自动排除名称以 zold 开头的废弃对象；具体匹配范围及分页硬上限见参数说明。")]
    public async Task<CallToolResult> FindObjectReferencesAsync(
        [Description("目标对象所在的单一数据库；仅用于精确确认目标，可与 searchDatabase 不同。")]
        string targetDatabase,
        [Description("目标对象名或 schema.object；省略 schema 时使用 dbo。不可包含数据库名，且必须在 targetDatabase 精确解析为现有 table、view、procedure 或 function。")]
        string targetObject,
        [Description("只搜索这个单一数据库内的模块定义；与 targetDatabase 相同时匹配 schema.object，目标名至少 4 字符时也匹配裸对象名；不同时只匹配明确的 database.schema.object 三段名。")]
        string searchDatabase,
        [Description("可选的来源模块类型，逗号分隔：procedure、function、view、trigger；默认搜索全部四类。")]
        string? sourceTypes = null,
        [Description("是否附加搜索当前 SQL Server 实例的 SQL Agent Job Step；默认 false。Job 结果固定最多 20 项，不受 offset/limit 控制。")]
        bool includeJobs = false,
        [Description("跳过多少个数据库模块候选；用于按 nextOffset 续查，默认 0，最大 1000。不作用于 Job。达到硬上限且仍有结果时，nextOffset 为 null，并返回 referencesTruncationReason=max_offset。")]
        int offset = 0,
        [Description("最多返回多少个数据库模块候选，默认 20，硬上限 50。不作用于 Job。")]
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _metadataService.FindObjectReferencesAsync(
            targetDatabase,
            targetObject,
            searchDatabase,
            sourceTypes,
            includeJobs,
            offset,
            limit,
            cancellationToken).ConfigureAwait(false);
        return CreateToolResult(
            result,
            result.Success,
            result.Success
                ? $"文本搜索完成：返回 {result.References.Count} 个数据库模块、{result.Jobs.Count} 个 Job Step" +
                  (result.ReferencesHasMore || result.JobsTruncated ? "；结果尚未完整。" : "。")
                : $"文本搜索失败：{result.Error?.Message}");
    }

    [McpServerTool(
        Name = "get_object_details",
        Title = "读取 SQL Server 对象详情",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ObjectDetailsResult))]
    [Description(
        "读取明确对象的字段、索引、参数、权限和定义。省略 schema 时使用 dbo；" +
        "长定义先用 definitionSearch 获取命中行，再按 startLine/maxLines 读取所需上下文。")]
    public async Task<CallToolResult> GetObjectDetailsAsync(
        [Description("对象名称：object、schema.object 或 database.schema.object。")]
        string objectName,
        [Description("明确的数据库；objectName 使用三段名时，其中的数据库必须与此参数一致。")]
        string database,
        [Description("定义从第几行开始返回，1 起算。")]
        int startLine = 1,
        [Description("定义最多返回多少行，默认 200，硬上限 800；definitionSearch 非空时忽略。")]
        int maxLines = 200,
        [Description("可选：在完整定义中按关键词做不区分大小写的定位，只返回轻量匹配行。")]
        string? definitionSearch = null,
        [Description("跳过多少个定义匹配行，默认 0；按 nextMatchOffset 续查。")]
        int matchOffset = 0,
        [Description("最多返回多少个定义匹配行，默认 20，硬上限 20。")]
        int maxMatches = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _metadataService.GetObjectDetailsAsync(
            objectName,
            database,
            startLine,
            maxLines,
            definitionSearch,
            matchOffset,
            maxMatches,
            cancellationToken).ConfigureAwait(false);
        return CreateToolResult(
            result,
            result.Success,
            result.Success
                ? $"对象详情读取完成：返回 {result.ReturnedLines} 行，{result.ReturnedDefinitionMatchCount} 个定义匹配" +
                  (result.DefinitionHasMore || result.MatchesHasMore ? "；仍有后续内容。" : "。")
                : $"对象详情读取失败：{result.Error?.Message}");
    }

    private static CallToolResult CreateToolResult<T>(T result, bool success, string summary) => new()
    {
        Content = [new TextContentBlock { Text = summary }],
        StructuredContent = JsonSerializer.SerializeToElement(result, ToolJsonOptions),
        IsError = !success,
    };
}
