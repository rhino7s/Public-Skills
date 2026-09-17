using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [Description("""
        执行只读 T-SQL 批次，支持本地临时表和表变量。每次调用使用独立会话。
        不支持 EXEC、持久化修改、全局临时表及远程数据源。
        """)]
    public async Task<CallToolResult> ExecuteSqlAsync(
        [Description("只读 T-SQL 批次。")]
        string sql,
        [Description("连接的初始数据库；SQL 可使用 database.schema.object 跨库查询。")]
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
                : (result.ResultSets.Count > 0 ? "本次查询未完成，已返回部分结果，不能据此判断整次任务完成。" : "查询失败：") + result.Error?.Message);
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
    [Description("""
        在指定数据库和 schema 中按名称查找对象，最多返回 20 项。
        仅返回可见对象；procedure 的 canExecute 表示数据库执行权限。
        """)]
    public async Task<CallToolResult> FindObjectAsync(
        [Description("对象名、schema.object 或 database.schema.object；省略 schema 使用 dbo。显式数据库名须与 database 一致。")]
        string objectName,
        [Description("搜索对象的数据库。")]
        string database,
        [Description("类型筛选，逗号分隔：table、view、procedure、function，或 SQL Server 对象类型代码；省略时不限类型。")]
        string? objectTypes = null,
        [Description("true 为对象名精确匹配；false 为包含匹配，关键词至少 3 字符。schema 始终精确匹配。")]
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
    [Description("""
        查找目标对象在指定数据库模块定义中的文本引用候选，可附加搜索 SQL Agent Job Step。
        文本命中不区分读、写、执行，可能包含注释、自身定义或动态 SQL；不是完整依赖关系。
        """)]
    public async Task<CallToolResult> FindObjectReferencesAsync(
        [Description("目标对象所在数据库。")]
        string targetDatabase,
        [Description("对象名或 schema.object，省略 schema 使用 dbo；须为现有 table、view、procedure 或 function。")]
        string targetObject,
        [Description("搜索模块定义的数据库。同库匹配 schema.object，目标名至少 4 字符时也匹配裸对象名；跨库只匹配 database.schema.object。")]
        string searchDatabase,
        [Description("来源模块类型，逗号分隔：procedure、function、view、trigger；省略时搜索全部四类。")]
        string? sourceTypes = null,
        [Description("是否附加搜索当前实例的 SQL Agent Job Step；最多 20 项，不受 offset/limit 控制。")]
        bool includeJobs = false,
        [Description("跳过的模块候选数，范围 0–1000；续页位置为 nextOffset。")]
        int offset = 0,
        [Description("最多返回的模块候选数，范围 1–50。")]
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
    [Description("""
        读取对象的字段、索引、参数、权限及定义。
        指定 definitionSearch 时改为定义关键词搜索，返回匹配行，不返回完整字段、索引及参数。
        """)]
    public async Task<CallToolResult> GetObjectDetailsAsync(
        [Description("对象名、schema.object 或 database.schema.object；省略 schema 使用 dbo。")]
        string objectName,
        [Description("对象所在数据库；须与 objectName 中显式指定的数据库一致。")]
        string database,
        [Description("定义起始行，1 起算；续页位置为 nextStartLine。definitionSearch 非空时不使用。")]
        int startLine = 1,
        [Description("最多返回的定义行数，范围 1–800；definitionSearch 非空时不使用。")]
        int maxLines = 200,
        [Description("定义搜索关键词，不区分大小写；去除首尾空白后最多 256 字符。空白或省略时按 startLine/maxLines 返回定义。")]
        string? definitionSearch = null,
        [Description("跳过的定义匹配行数；续页位置为 nextMatchOffset。仅用于关键词搜索。")]
        int matchOffset = 0,
        [Description("最多返回的定义匹配行数，范围 1–20；仅用于关键词搜索。")]
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

    internal static CallToolResult CreateToolResult<T>(T result, bool success, string summary)
    {
        if (success)
            return new()
            {
                Content = [new TextContentBlock { Text = summary }],
                StructuredContent = JsonSerializer.SerializeToElement(result, ToolJsonOptions),
                IsError = false,
            };
        var body = JsonSerializer.SerializeToNode(result, ToolJsonOptions);
        if (body?["error"] is JsonObject error)
        {
            var presented = PublicToolErrors.Present(error.Deserialize<ToolError>(ToolJsonOptions)!);
            body["error"] = JsonSerializer.SerializeToNode(presented, ToolJsonOptions);
            summary = body["resultSets"] is JsonArray { Count: > 0 }
                ? "本次调用未完成，已返回部分结果。" + presented.Message
                : presented.Message;
        }
        return new()
        {
            Content = [new TextContentBlock { Text = summary }],
            StructuredContent = JsonSerializer.SerializeToElement(body, ToolJsonOptions),
            IsError = !success,
        };
    }
}
