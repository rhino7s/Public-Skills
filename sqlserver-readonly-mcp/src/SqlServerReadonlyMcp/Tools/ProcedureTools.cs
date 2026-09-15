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
    [Description(
        "仅允许执行当前身份能力目录明确授权的业务 procedure，且必须由用户明确要求对应业务动作。" +
        "执行前通过 find_object 或 get_object_details 确认 canExecute=true；参数不明确时先用 get_object_details 读取。" +
        "execution_unknown 表示未确认执行完成，不得自动重试；不得为补取截断结果而重复执行。")]
    public async Task<CallToolResult> ExecuteProcedureAsync(
        [Description("单条静态 EXEC 调用，例如 EXEC dbo.ExampleProcedure 'a', 1；省略 schema 时使用 dbo。不接受动态 SQL、变量过程名或远程调用。")]
        string sql,
        [Description("明确的初始数据库；SQL 使用三段名时，其中的数据库必须与此参数一致。")]
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
