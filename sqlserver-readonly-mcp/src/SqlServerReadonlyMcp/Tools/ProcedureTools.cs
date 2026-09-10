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
        "过程可能修改资料，仅在用户明确要求对应业务动作时使用；输出截断不代表执行中止，execution_unknown 表示未确认执行完成，不得自动重试；" +
        "允许通过数据库对象及 EXECUTE 权限核验的 sp_ 业务 procedure；省略 schema 时使用 dbo。禁止系统过程、系统同名对象、动态 SQL 入口（包括 sp_executesql、sp_prepexec）、变量过程名、sys 架构、xp_ 前缀、EXECUTE AS、四段名和远程执行；三段名中的数据库必须与 database 参数一致。")]
    public async Task<CallToolResult> ExecuteProcedureAsync(
        [Description("单条存储过程调用，例如 EXEC dbo.ExampleProcedure 'a', 1；使用 database.schema.procedure 时，数据库必须与 database 参数一致。")]
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
                    ? $"过程执行结果未确认：{result.Error.Message}"
                    : $"过程执行失败：{result.Error?.Message}");
    }

}
