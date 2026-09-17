using System.Data;
using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Logging;
using SqlServerReadonlyMcp.Security;

namespace SqlServerReadonlyMcp.Sql;

public sealed class CatalogAccessService(McpSettings settings, SqlConnectionFactory factory, QueryConcurrencyGate gate)
{
    public async Task<ToolError?> VerifyAsync(IReadOnlyList<CatalogObject> objects, CancellationToken token)
    {
        try { return await VerifyCoreAsync(objects, token).ConfigureAwait(false); }
        catch (SqlException exception)
        {
            var connection = SqlErrorClassifier.IsConnectionFailure(exception);
            return new("access_check_unavailable", connection
                ? PublicToolErrors.ConnectionFailed
                : "访问检查暂不可用，本次操作未执行，请联系管理员处理。", exception.Number);
        }
    }

    private async Task<ToolError?> VerifyCoreAsync(IReadOnlyList<CatalogObject> objects, CancellationToken token)
    {
        if (!settings.Capabilities.Enabled) return new("access_denied", "当前没有访问权限。");
        if (objects.Count == 0) return null;
        if (objects.Count > CatalogSqlAnalyzer.MaximumObjects) return new("invalid_input", "查询对象过多，请缩小批次。");
        // 所有 grant 都通过后才检查元数据；不探查未授权对象。不采用展示分页或正文内容。
        foreach (var metadata in new[] { false, true })
        {
            using var timing = CallTiming.Current?.Measure(metadata ? "metadata" : "authorization");
            foreach (var group in objects.GroupBy(o => o.Database, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                using var lease = await gate.EnterAsync(token).ConfigureAwait(false);
                await using var connection = await factory.OpenAsync(group.Key, token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                var entries = group.ToArray();
                command.CommandTimeout = CallTiming.PreflightSeconds;
                command.CommandText = BuildQuery(entries.Length, metadata);
                for (var i = 0; i < entries.Length; i++)
                {
                    command.Parameters.Add($"@s{i}", SqlDbType.NVarChar, 128).Value = entries[i].Schema;
                    command.Parameters.Add($"@n{i}", SqlDbType.NVarChar, 128).Value = entries[i].Name;
                    command.Parameters.Add($"@f{i}", SqlDbType.Bit).Value = entries[i].IsFunction;
                }
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var count = 0;
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (reader.GetInt32(0) != count || !reader.GetBoolean(1))
                        return new(metadata ? "object_not_available" : "access_denied",
                            metadata ? "请求对象的类型或实际权限不符合要求，未执行。" : "请求包含未授权对象，整个批次未执行。");
                    count++;
                }
                if (count != entries.Length) return new("access_check_unavailable", "访问检查暂不可用，本次操作未执行。");
            }
        }
        return null;
    }

    internal string BuildQuery(int count, bool metadata)
    {
        var values = string.Join(",", Enumerable.Range(0, count).Select(i => $"({i},@s{i},@n{i},@f{i})"));
        var exists = metadata ? """
            SELECT 1 FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE s.name = r.schema_name COLLATE CATALOG_DEFAULT
              AND o.name = r.object_name COLLATE CATALOG_DEFAULT
              AND o.is_ms_shipped = 0
              AND ((r.is_function = 0 AND o.type IN ('U','V')) OR (r.is_function = 1 AND o.type IN ('FN','IF','TF')))
              AND HAS_PERMS_BY_NAME(QUOTENAME(s.name) + N'.' + QUOTENAME(o.name), N'OBJECT',
                  CASE WHEN o.type = 'FN' THEN N'EXECUTE' ELSE N'SELECT' END) = 1
            """ : $"""
            SELECT 1 FROM {CapabilityFunctionName.Parse(settings.Capabilities.ListFunction).Sql}() AS c
            WHERE PARSENAME(c.iname,4) IS NULL AND PARSENAME(c.iname,3) IS NOT NULL
              AND DB_ID(PARSENAME(c.iname,3)) = DB_ID()
              AND PARSENAME(c.iname,2) COLLATE CATALOG_DEFAULT = r.schema_name COLLATE CATALOG_DEFAULT
              AND PARSENAME(c.iname,1) COLLATE CATALOG_DEFAULT = r.object_name COLLATE CATALOG_DEFAULT
            """;
        return $"SELECT r.ordinal, CONVERT(bit, CASE WHEN EXISTS ({exists}) THEN 1 ELSE 0 END) AS allowed "
            + $"FROM (VALUES {values}) AS r(ordinal,schema_name,object_name,is_function) ORDER BY r.ordinal;";
    }
}
