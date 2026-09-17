using System.Data;
using System.Data.Common;
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
        "存储过程结果达到 MCP 返回限制，当前内容不代表完整数据。procedure 已执行完成，但返回内容不完整；不得仅为获取剩余结果而重复执行该业务动作。";

    private readonly QuerySettings _settings;
    private readonly bool _catalogMode;
    private readonly CatalogSqlAnalyzer _catalogAnalyzer;
    private readonly CatalogAccessService _catalogAccess;
    private readonly CapabilityService _capabilities;
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly QueryConcurrencyGate _concurrencyGate;
    private readonly SqlSafetyAnalyzer _safetyAnalyzer;
    private readonly AuditLogger _auditLogger;

    public SqlQueryService(
        McpSettings settings,
        SqlConnectionFactory connectionFactory,
        QueryConcurrencyGate concurrencyGate,
        SqlSafetyAnalyzer safetyAnalyzer,
        AuditLogger auditLogger,
        CapabilityService capabilities,
        CatalogSqlAnalyzer? catalogAnalyzer = null,
        CatalogAccessService? catalogAccess = null)
    {
        _settings = settings.Query;
        _catalogMode = settings.IsCatalogMode;
        _catalogAnalyzer = catalogAnalyzer ?? new CatalogSqlAnalyzer();
        _catalogAccess = catalogAccess ?? new CatalogAccessService(settings, connectionFactory, concurrencyGate);
        _capabilities = capabilities;
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
            ProcedureLimitGuidance,
            cancellationToken).ConfigureAwait(false);

    private async Task<QueryResult> ExecuteCoreAsync(
        string sql,
        string database,
        string tool,
        string limitGuidance,
        CancellationToken cancellationToken)
    {
        using var ownedTiming = CallTiming.Current is null ? new CallTiming(cancellationToken) : null;
        var timing = CallTiming.Current!;
        var requestId = timing.RequestId;
        var totalStopwatch = Stopwatch.StartNew();
        var queueWaitMilliseconds = 0L;
        var resultSets = new List<ResultSetResult>();
        var returnedRows = 0;
        var resultSizeBytes = 0;
        var truncated = false;
        string? truncationReason = null;
        var procedureStarted = false;

        try
        {
            if (ownedTiming is not null && _capabilities.RequiresCheck)
            {
                using var auth = timing.Measure("authorization");
                var rejection = await _capabilities.CheckAccessAsync(timing.Preflight.Token).ConfigureAwait(false);
                if (rejection is not null)
                    {
                    var error = (timing.Preflight.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                        ? CapabilityService.CheckTimedOut() : rejection).StructuredContent!.Value;
                    return Reject(new(error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!));
                }
            }
            if (string.IsNullOrWhiteSpace(database)) return Reject(new("invalid_input", "database 不可为空。"));
            SqlSafetyResult safety;
            CatalogAnalysis? analysis = null;
            ProcedureCallTarget? target = null;
            using (timing.Measure("parse"))
            {
                if (_catalogMode && sql.Length > CatalogSqlAnalyzer.MaximumSqlCharacters)
                    return Reject(new("safety_rejection", "SQL 超过长度限制，请缩小批次。"));
                safety = tool == "execute_procedure"
                    ? _safetyAnalyzer.AnalyzeProcedureCall(sql, database, out target) : _safetyAnalyzer.Analyze(sql);
                if (safety.IsAllowed && _catalogMode)
                {
                    analysis = _catalogAnalyzer.Analyze(sql, tool == "execute_procedure", timing.Preflight.Token);
                    safety = analysis.Safety;
                }
            }
            if (!safety.IsAllowed) return Reject(new("safety_rejection", safety.Message ?? "SQL 被安全规则拒绝。"));
            if (_catalogMode) timing.Preflight.Token.ThrowIfCancellationRequested();
            if (tool == "execute_procedure")
            {
                using var auth = timing.Measure("authorization");
                var denial = await _capabilities.CheckProcedureAccessAsync(database.Trim(), target!.Schema, target.Name, timing.Preflight.Token).ConfigureAwait(false);
                if (timing.Preflight.IsCancellationRequested) timing.Preflight.Token.ThrowIfCancellationRequested();
                if (denial is not null) return Reject(denial);
            }
            else if (_catalogMode)
            {
                var denial = await _catalogAccess.VerifyAsync(analysis!.Objects, timing.Preflight.Token).ConfigureAwait(false);
                if (denial is not null) return Reject(denial);
            }

            using var executionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (tool != "execute_procedure") executionDeadline.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
            using var initialPhase = timing.Measure(tool == "execute_procedure" ? "metadata" : "execution");
            var preparationToken = tool == "execute_procedure" ? timing.Preflight.Token : executionDeadline.Token;
            using var lease = await _concurrencyGate.EnterAsync(preparationToken).ConfigureAwait(false);
            queueWaitMilliseconds = lease.WaitMilliseconds;

            await using var connection = await _connectionFactory
                .OpenAsync(database.Trim(), preparationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            if (tool == "execute_procedure")
            {
                var verification = await ProcedureTargetVerifier.VerifyAsync(connection, target!, CallTiming.PreflightSeconds, preparationToken,
                    analysis?.Objects.Single().Database).ConfigureAwait(false);
                if (verification.Error is { } error)
                {
                    totalStopwatch.Stop();
                    var rejected = Failure(requestId, resultSets, returnedRows, resultSizeBytes,
                        queueWaitMilliseconds, totalStopwatch.ElapsedMilliseconds, error);
                    WriteAudit(rejected, tool, database, sql, error.Category);
                    return rejected;
                }
                command.CommandText = target!.Qualify(sql, connection.Database, verification.Schema!, verification.Name!);
            }
            else command.CommandText = sql;
            command.CommandType = CommandType.Text;
            command.CommandTimeout = _settings.TimeoutSeconds;

            if (tool == "execute_procedure")
            {
                initialPhase.Dispose();
                executionDeadline.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
            }
            using var executionPhase = tool == "execute_procedure" ? timing.Measure("execution") : null;
            var executionToken = executionDeadline.Token;
            procedureStarted = tool == "execute_procedure";
            await using var reader = await command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, executionToken)
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
                    if (procedureStarted) await DrainAsync(reader, executionToken).ConfigureAwait(false);
                    else command.Cancel();
                    break;
                }

                while (await reader.ReadAsync(executionToken).ConfigureAwait(false))
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
                    if (procedureStarted) await DrainAsync(reader, executionToken).ConfigureAwait(false);
                    else command.Cancel();
                    break;
                }
            }
            while (await reader.NextResultAsync(executionToken).ConfigureAwait(false));

            totalStopwatch.Stop();
            var success = FinalizeDelivery(new QueryResult(
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
                null));
            WriteAudit(success, tool, database, sql, null);
            return success;
        }
        catch (Exception exception) when (procedureStarted)
        {
            totalStopwatch.Stop();
            var cause = cancellationToken.IsCancellationRequested ? "调用方取消" :
                exception is OperationCanceledException || exception is SqlException { Number: -2 } ? "执行超时" : "执行或结果读取失败";
            var failed = Failure(requestId, resultSets, returnedRows,
                resultSizeBytes, queueWaitMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                new ToolError("execution_unknown", $"{cause}，procedure 未确认执行完成，可能已有部分操作生效；不得自动重试。",
                    (exception as SqlException)?.Number, (exception as SqlException)?.State, (exception as SqlException)?.Class))
                with { Truncated = truncated, TruncationReason = truncationReason };
            WriteAudit(failed, tool, database, sql, "execution_unknown");
            return failed;
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
        catch (OperationCanceledException)
        {
            return Reject(new("timeout", "操作超时，本次调用已停止，请稍后再试。"));
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
        QueryResult Reject(ToolError error)
        {
            totalStopwatch.Stop();
            var result = Failure(requestId, resultSets, returnedRows, resultSizeBytes, queueWaitMilliseconds,
                totalStopwatch.ElapsedMilliseconds, error);
            WriteAudit(result, tool, database, sql, error.Category);
            return result;
        }

    }

    // Consume all remaining result sets without retaining values, so late SQL errors are observed.
    internal static async Task DrainAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                cancellationToken.ThrowIfCancellationRequested();
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
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

    internal static QueryResult FinalizeDelivery(QueryResult result) => result with
    {
        ReturnedRows = result.ResultSets.Sum(set => set.Rows.Count),
        ResultSizeBytes = result.ResultSets.Count == 0 ? 0 : JsonSerializer.SerializeToUtf8Bytes(result.ResultSets, Program.CreateToolJsonOptions()).Length,
    };

    private static QueryResult Failure(
        string requestId,
        IReadOnlyList<ResultSetResult> resultSets,
        int returnedRows,
        int resultSizeBytes,
        long queueWaitMilliseconds,
        long durationMilliseconds,
        ToolError error) =>
        FinalizeDelivery(new QueryResult(
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
            error));

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
