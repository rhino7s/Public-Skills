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
        "执行只读查询，优先指定字段并用 WHERE、聚合或 TOP 控制范围；精确核对不默认使用 NOLOCK。" +
        "允许本地临时表（#）和表变量，禁止 EXEC、全局临时表及远程数据源。" +
        "每次调用独立执行。本地临时表（#）、变量及事务状态不能跨调用复用；依赖这些状态的 SQL 必须放在同一次调用中完成。")]
    public async Task<CallToolResult> ExecuteSqlAsync(
        [Description("完整 T-SQL 查询批次。")]
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
        Name = "find_object",
        Title = "定位 SQL Server 对象",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ObjectSearchResult))]
    [Description(
        "按名称定位对象，名称不确定时才使用模糊匹配；" +
        "模糊匹配最多返回 20 项。" +
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
        "命中可能来自注释、对象自身定义或动态 SQL，不区分读、写、执行。" +
        "必须查看 matches 并按需读取候选定义后再判断，不得直接称为实际调用方或完整血缘。" +
        "排除名称以 zold 开头的来源模块。")]
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
        [Description("最多返回多少个数据库模块候选，默认 50，硬上限 50。不作用于 Job。")]
        int limit = 50,
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
        "读取明确对象的字段、索引、参数、权限和定义；" +
        "长定义先用 definitionSearch 获取命中行，再按 startLine/maxLines 读取所需上下文。")]
    public async Task<CallToolResult> GetObjectDetailsAsync(
        [Description("对象名、schema.object 或 database.schema.object；省略 schema 时使用 dbo。")]
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

    internal static CallToolResult CreateToolResult<T>(T result, bool success, string summary) => new()
    {
        Content = [new TextContentBlock { Text = summary }],
        StructuredContent = JsonSerializer.SerializeToElement(result, ToolJsonOptions),
        IsError = !success,
    };
}
