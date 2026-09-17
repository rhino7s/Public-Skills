using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tools;

[McpServerToolType]
public sealed class CatalogQueryTools(SqlQueryService queryService)
{
    [McpServerTool(Name = "execute_sql", Title = "查询已授权的业务对象", ReadOnly = true,
        Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(QueryResult))]
    [Description("""
        执行只读 SQL；直接引用的持久化数据库对象必须在“能力目录”授权范围内。
        支持 JOIN、聚合、CTE、窗口函数、本地临时表和表变量；临时对象仅在本次调用内有效。
        不支持 EXEC、持久化修改、全局临时表、系统信息查询、远程数据源及查询提示或表提示（包括 NOLOCK）。
        """)]
    public async Task<CallToolResult> ExecuteSqlAsync(
        [Description("只读 T-SQL 批次；持久化对象使用 database.schema.object 三段名。")]
        string sql,
        [Description("连接的初始数据库；SQL 可跨库引用已授权对象。")]
        string database,
        CancellationToken cancellationToken = default)
    {
        var result = await queryService.ExecuteAsync(sql, database, cancellationToken).ConfigureAwait(false);
        return SqlServerTools.CreateToolResult(result, result.Success,
            result.Success ? $"查询完成：返回 {result.ReturnedRows} 行。" + (result.Truncated ? "结果已截断，请缩小查询。" : "")
                : (result.ResultSets.Count > 0 ? "本次查询未完成，已返回部分结果。" : "") + result.Error?.Message);
    }
}
