using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tools;

[McpServerToolType]
public sealed class ProcedureTools(SqlQueryService queryService)
{
    [McpServerTool(
        Name = "execute_procedure",
        Title = "执行已授权的 SQL Server 存储过程",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(QueryResult))]
    [Description("""
        执行“能力目录”中已授权且用户要求的业务 procedure。
        execution_unknown 表示执行完成状态未确认，不得自动重试；不得为补取截断结果而重复执行。
        """)]
    public async Task<CallToolResult> ExecuteProcedureAsync(
        [Description("单条静态 EXEC 调用，含业务参数；可省略数据库和 schema，省略 schema 使用 dbo。不支持动态 SQL、变量过程名及远程调用。")]
        string sql,
        [Description("procedure 所在数据库；须与 SQL 中显式指定的数据库一致。")]
        string database,
        CancellationToken cancellationToken = default)
    {
        var result = await queryService.ExecuteProcedureAsync(sql, database, cancellationToken).ConfigureAwait(false);
        return SqlServerTools.CreateToolResult(
            result,
            result.Success,
            result.Success
                ? $"过程执行完成：返回 {result.ReturnedRows} 行，{result.ResultSets.Count} 个结果集" +
                  (result.Truncated ? $"；结果已截断（{result.TruncationReason}）。" : "。")
                : result.Error?.Category == "execution_unknown"
                    ? $"过程执行结果未确认：{result.Error.Message}" + (result.ResultSets.Count > 0 ? "已返回部分结果，不能据此判断整次任务完成。" : "")
                    : $"过程执行失败：{result.Error?.Message}");
    }

}
