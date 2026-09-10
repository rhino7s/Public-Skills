using System.Data;
using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Security;

namespace SqlServerReadonlyMcp.Sql;

internal sealed record ProcedureVerification(string? Schema, string? Name, ToolError? Error);

internal static class ProcedureTargetVerifier
{
    // Query in the same database and connection used for execution. Catalog comparison uses its collation.
    internal const string VerificationSql = """
        SELECT s.name, p.name, p.type, p.is_ms_shipped,
            CONVERT(bit, COALESCE(HAS_PERMS_BY_NAME(QUOTENAME(s.name) + N'.' + QUOTENAME(p.name), N'OBJECT', N'EXECUTE'), 0)),
            CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.system_objects x WHERE x.name = p.name) THEN 1 ELSE 0 END)
        FROM sys.procedures p
        JOIN sys.schemas s ON s.schema_id = p.schema_id
        WHERE s.name = @schema AND p.name = @name;
        """;

    internal static async Task<ProcedureVerification> VerifyAsync(SqlConnection connection, ProcedureCallTarget target,
        int timeoutSeconds, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = VerificationSql;
        command.CommandTimeout = timeoutSeconds;
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = target.Schema;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = target.Name;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            return new(null, null, new ToolError("procedure_not_available", "目标不是当前数据库中可见的业务 procedure；未执行。"));
        return Validate(reader.GetString(0), reader.GetString(1), reader.GetString(2).TrimEnd(),
            reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5));
    }

    internal static ProcedureVerification Validate(string schema, string name, string type,
        bool isSystem, bool canExecute, bool systemNameCollision)
    {
        if (isSystem || systemNameCollision || type is not ("P" or "PC") ||
            schema.Equals("sys", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase))
            return new(null, null, new ToolError("safety_rejection", "目标为系统过程、系统同名对象或不支持的过程类型；未执行。"));
        if (!canExecute)
            return new(null, null, new ToolError("permission_denied", "当前账号没有目标 procedure 的执行权限；未执行。"));
        return new(schema, name, null);
    }
}
