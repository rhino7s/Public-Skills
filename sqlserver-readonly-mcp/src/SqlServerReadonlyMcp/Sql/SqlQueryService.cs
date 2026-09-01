using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Logging;
using SqlServerReadonlyMcp.Security;

namespace SqlServerReadonlyMcp.Sql;

public sealed class SqlQueryService
{
    private const string LimitGuidance =
        "结果达到 MCP 返回限制，当前内容不代表完整数据。请改写 SQL：增加 WHERE 条件、聚合，或使用较小的 TOP 后重新查询。";
    private const string ProcedureLimitGuidance =
        "存储过程结果达到 MCP 返回限制，当前内容不代表完整数据。请缩小参数范围，或改用 execute_sql 做聚合查询。";

    private readonly QuerySettings _settings;
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly QueryConcurrencyGate _concurrencyGate;
    private readonly SqlSafetyAnalyzer _safetyAnalyzer;
    private readonly AuditLogger _auditLogger;

    public SqlQueryService(
        McpSettings settings,
        SqlConnectionFactory connectionFactory,
        QueryConcurrencyGate concurrencyGate,
        SqlSafetyAnalyzer safetyAnalyzer,
        AuditLogger auditLogger)
    {
        _settings = settings.Query;
        _connectionFactory = connectionFactory;
        _concurrencyGate = concurrencyGate;
        _safetyAnalyzer = safetyAnalyzer;
        _auditLogger = auditLogger;
    }

    public async Task<QueryResult> ExecuteAsync(
        string sql,
        string database,
        CancellationToken cancellationToken) =>
        await ExecuteCoreAsync(
            sql,
            database,
            "execute_sql",
            _safetyAnalyzer.Analyze(sql),
            LimitGuidance,
            cancellationToken).ConfigureAwait(false);

    public async Task<QueryResult> ExecuteProcedureAsync(
        string sql,
        string database,
        CancellationToken cancellationToken) =>
        await ExecuteCoreAsync(
            sql,
            database,
            "execute_procedure",
            _safetyAnalyzer.AnalyzeProcedureCall(sql, database),
            ProcedureLimitGuidance,
            cancellationToken).ConfigureAwait(false);

    private async Task<QueryResult> ExecuteCoreAsync(
        string sql,
        string database,
        string tool,
        SqlSafetyResult safety,
        string limitGuidance,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var totalStopwatch = Stopwatch.StartNew();
        var queueWaitMilliseconds = 0L;
        var resultSets = new List<ResultSetResult>();
        var returnedRows = 0;
        var resultSizeBytes = 0;
        var truncated = false;
        string? truncationReason = null;

        if (string.IsNullOrWhiteSpace(database))
        {
            totalStopwatch.Stop();
            var rejected = Failure(
                requestId,
                resultSets,
                returnedRows,
                resultSizeBytes,
                queueWaitMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                new ToolError("invalid_input", "database 不可为空。"));
            WriteAudit(rejected, tool, database, sql, "invalid_input");
            return rejected;
        }

        if (!safety.IsAllowed)
        {
            totalStopwatch.Stop();
            var rejected = Failure(
                requestId,
                resultSets,
                returnedRows,
                resultSizeBytes,
                queueWaitMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                new ToolError("safety_rejection", safety.Message ?? "SQL 被安全规则拒绝。"));
            WriteAudit(rejected, tool, database, sql, safety.Code);
            return rejected;
        }

        try
        {
            using var lease = await _concurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            queueWaitMilliseconds = lease.WaitMilliseconds;

            await using var connection = await _connectionFactory
                .OpenAsync(database.Trim(), cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandType = CommandType.Text;
            command.CommandTimeout = _settings.TimeoutSeconds;

            await using var reader = await command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);

            var maximumBytes = checked(_settings.MaxResultSizeKb * 1024);
            var estimatedBytes = 2;
            var stopReading = false;

            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }

                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => new ColumnResult(reader.GetName(index), reader.GetDataTypeName(index)))
                    .ToArray();
                var rows = new List<IReadOnlyList<object?>>();
                estimatedBytes += JsonSerializer.SerializeToUtf8Bytes(columns).Length + 24;
                if (estimatedBytes > maximumBytes)
                {
                    truncated = true;
                    truncationReason = "max_result_size";
                    stopReading = true;
                    command.Cancel();
                    break;
                }

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (returnedRows >= _settings.MaxRows)
                    {
                        truncated = true;
                        truncationReason = "max_rows";
                        stopReading = true;
                        break;
                    }

                    var remainingBytes = maximumBytes - estimatedBytes;
                    if (remainingBytes <= 128)
                    {
                        truncated = true;
                        truncationReason = "max_result_size";
                        stopReading = true;
                        break;
                    }

                    var row = new object?[reader.FieldCount];
                    var rowContainsTruncatedCell = false;
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        var cellBudget = Math.Max(1, remainingBytes / (reader.FieldCount - index));
                        row[index] = ReadValue(reader, index, cellBudget, out var cellWasTruncated);
                        rowContainsTruncatedCell |= cellWasTruncated;
                    }

                    var rowBytes = JsonSerializer.SerializeToUtf8Bytes(row).Length + 1;
                    if (estimatedBytes + rowBytes > maximumBytes)
                    {
                        truncated = true;
                        truncationReason = "max_result_size";
                        stopReading = true;
                        break;
                    }

                    rows.Add(row);
                    returnedRows++;
                    estimatedBytes += rowBytes;
                    if (rowContainsTruncatedCell)
                    {
                        truncated = true;
                        truncationReason = "large_cell";
                        stopReading = true;
                        break;
                    }
                }

                resultSets.Add(new ResultSetResult(columns, rows));
                if (stopReading)
                {
                    command.Cancel();
                    break;
                }
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            resultSizeBytes = JsonSerializer.SerializeToUtf8Bytes(resultSets).Length;
            totalStopwatch.Stop();
            var success = new QueryResult(
                true,
                requestId,
                resultSets,
                returnedRows,
                resultSizeBytes,
                queueWaitMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                truncated,
                truncationReason,
                truncated ? limitGuidance : null,
                null);
            WriteAudit(success, tool, database, sql, null);
            return success;
        }
        catch (QueryQueueTimeoutException exception)
        {
            totalStopwatch.Stop();
            queueWaitMilliseconds = exception.WaitMilliseconds;
            var busy = Failure(
                requestId,
                resultSets,
                returnedRows,
                resultSizeBytes,
                queueWaitMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                new ToolError("busy", exception.Message));
            WriteAudit(busy, tool, database, sql, "busy");
            return busy;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            totalStopwatch.Stop();
            var canceled = Failure(
                requestId,
                resultSets,
                returnedRows,
                resultSizeBytes,
                queueWaitMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                new ToolError("canceled", "查询已由调用方取消。"));
            WriteAudit(canceled, tool, database, sql, "canceled");
            return canceled;
        }
        catch (SqlException exception)
        {
            totalStopwatch.Stop();
            var error = SqlErrorClassifier.Create(exception);
            var failed = Failure(
                requestId,
                resultSets,
                returnedRows,
                resultSizeBytes,
                queueWaitMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                error);
            WriteAudit(failed, tool, database, sql, error.Category);
            return failed;
        }
        catch (Exception exception)
        {
            totalStopwatch.Stop();
            var failed = Failure(
                requestId,
                resultSets,
                returnedRows,
                resultSizeBytes,
                queueWaitMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                new ToolError("internal_error", Limit(exception.Message, 2_048)));
            WriteAudit(failed, tool, database, sql, "internal_error");
            return failed;
        }
    }

    private static object? ReadValue(
        SqlDataReader reader,
        int ordinal,
        int remainingBytes,
        out bool wasTruncated)
    {
        wasTruncated = false;
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var fieldType = reader.GetFieldType(ordinal);
        if (fieldType == typeof(string))
        {
            var maximumCharacters = Math.Clamp(remainingBytes / 4, 1, 65_536);
            using var textReader = reader.GetTextReader(ordinal);
            var buffer = new char[Math.Min(maximumCharacters + 1, 65_537)];
            var read = textReader.ReadBlock(buffer, 0, buffer.Length);
            if (read > maximumCharacters)
            {
                wasTruncated = true;
                return new TruncatedCell(new string(buffer, 0, maximumCharacters), true);
            }

            return new string(buffer, 0, read);
        }

        if (fieldType == typeof(byte[]))
        {
            var maximumBytes = Math.Clamp(remainingBytes / 2, 1, 49_152);
            var buffer = new byte[maximumBytes + 1];
            var read = reader.GetBytes(ordinal, 0, buffer, 0, buffer.Length);
            var length = (int)Math.Min(read, maximumBytes);
            wasTruncated = read > maximumBytes;
            return new BinaryCell(Convert.ToBase64String(buffer, 0, length), read > maximumBytes);
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long longValue => longValue.ToString(CultureInfo.InvariantCulture),
            ulong unsignedLongValue => unsignedLongValue.ToString(CultureInfo.InvariantCulture),
            decimal decimalValue => decimalValue.ToString(CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
            double doubleValue when !double.IsFinite(doubleValue) => doubleValue.ToString(CultureInfo.InvariantCulture),
            float floatValue when !float.IsFinite(floatValue) => floatValue.ToString(CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    private static QueryResult Failure(
        string requestId,
        IReadOnlyList<ResultSetResult> resultSets,
        int returnedRows,
        int resultSizeBytes,
        long queueWaitMilliseconds,
        long durationMilliseconds,
        ToolError error) =>
        new(
            false,
            requestId,
            resultSets,
            returnedRows,
            resultSizeBytes,
            queueWaitMilliseconds,
            durationMilliseconds,
            false,
            null,
            null,
            error);

    private void WriteAudit(
        QueryResult result,
        string tool,
        string database,
        string sql,
        string? errorCategory)
    {
        _auditLogger.WriteQuery(new QueryAuditEvent(
            result.RequestId,
            tool,
            database,
            sql,
            result.QueueWaitMs,
            result.DurationMs,
            result.ResultSets.Count,
            result.ReturnedRows,
            result.ResultSizeBytes,
            result.Truncated,
            result.TruncationReason,
            result.Success ? "success" : "error",
            result.Error?.SqlErrorNumber,
            errorCategory ?? result.Error?.Category,
            result.Error?.Message));
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private sealed record TruncatedCell(string Value, bool Truncated);

    private sealed record BinaryCell(string Base64, bool Truncated);
}
