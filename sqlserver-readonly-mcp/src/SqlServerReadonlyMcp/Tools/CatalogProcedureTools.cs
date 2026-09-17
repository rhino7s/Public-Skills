using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tools;

[McpServerToolType]
public sealed class CatalogProcedureTools(SqlQueryService queryService)
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
    public Task<CallToolResult> ExecuteProcedureAsync(
        [Description("单条静态 EXEC 调用，含业务参数；对象使用 database.schema.procedure 三段名。不支持动态 SQL、变量过程名及远程调用。")]
        string sql,
        [Description("procedure 所在数据库；须与 SQL 中显式指定的数据库一致。")]
        string database,
        CancellationToken cancellationToken = default)
        => new ProcedureTools(queryService).ExecuteProcedureAsync(sql, database, cancellationToken);
}
