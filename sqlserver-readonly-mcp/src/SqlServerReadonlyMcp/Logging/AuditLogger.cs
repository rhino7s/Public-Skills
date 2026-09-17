using SqlServerReadonlyMcp.Configuration;

namespace SqlServerReadonlyMcp.Logging;

public sealed class AuditLogger
{
    private readonly DailyLogWriter _writer;
    private readonly LoggingSettings _settings;

    public AuditLogger(DailyLogWriter writer, McpSettings settings)
    {
        _writer = writer;
        _settings = settings.Logging;
    }

    public void WriteQuery(QueryAuditEvent auditEvent)
    {
        try
        {
            var sql = _settings.IncludeSqlText ? auditEvent.Sql : null;
            var sqlTruncated = false;
            if (sql is not null && sql.Length > _settings.MaxSqlTextChars)
            {
                sql = sql[.._settings.MaxSqlTextChars];
                sqlTruncated = true;
            }

            WriteRecord(new Dictionary<string, object?>
            {
                ["level"] = auditEvent.Status == "success" ? "Information" : "Warning",
                ["eventType"] = "query",
                ["requestId"] = auditEvent.RequestId,
                ["tool"] = auditEvent.Tool,
                ["initialDatabase"] = EmptyAsNull(auditEvent.InitialDatabase),
                ["sql"] = sql,
                ["sqlTruncated"] = sql is null ? null : sqlTruncated,
                ["queueWaitMs"] = auditEvent.QueueWaitMs,
                ["durationMs"] = auditEvent.DurationMs,
                ["resultSetCount"] = auditEvent.ResultSetCount,
                ["returnedRows"] = auditEvent.ReturnedRows,
                ["resultSizeBytes"] = auditEvent.ResultSizeBytes,
                ["truncated"] = auditEvent.Truncated,
                ["truncationReason"] = auditEvent.TruncationReason,
                ["status"] = auditEvent.Status,
                ["sqlErrorNumber"] = auditEvent.SqlErrorNumber,
                ["errorCategory"] = auditEvent.ErrorCategory,
                ["error"] = Limit(auditEvent.Error, 2_048),
            });
        }
        catch
        {
            // Audit failure must not alter tool behavior or write to stdout.
        }
    }

    public void WriteTool(ToolAuditEvent auditEvent)
    {
        try
        {
            WriteRecord(new Dictionary<string, object?>
            {
                ["level"] = auditEvent.Status == "success" ? "Information" : "Warning",
                ["eventType"] = "tool",
                ["requestId"] = auditEvent.RequestId,
                ["tool"] = auditEvent.Tool,
                ["initialDatabase"] = EmptyAsNull(auditEvent.InitialDatabase),
                ["durationMs"] = auditEvent.DurationMs,
                ["status"] = auditEvent.Status,
                ["errorCategory"] = auditEvent.ErrorCategory,
                ["error"] = Limit(auditEvent.Error, 2_048),
            });
        }
        catch
        {
            // Audit failure must not alter tool behavior or write to stdout.
        }
    }

    private void WriteRecord(Dictionary<string, object?> record)
    {
        if (CallTiming.Current is { } timing)
        {
            foreach (var pair in timing.Snapshot()) record[pair.Key] = pair.Value;
            timing.Audited = true;
        }
        else
        {
            foreach (var phase in new[] { "parse_ms", "authorization_ms", "metadata_ms", "execution_ms" }) record[phase] = null;
            record["total_ms"] = record.GetValueOrDefault("durationMs");
        }
        _writer.Write(record);
    }

    private static string? EmptyAsNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Limit(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength
            ? value
            : value[..maximumLength];
}

public sealed record QueryAuditEvent(
    string RequestId,
    string Tool,
    string? InitialDatabase,
    string Sql,
    long QueueWaitMs,
    long DurationMs,
    int ResultSetCount,
    int ReturnedRows,
    int ResultSizeBytes,
    bool Truncated,
    string? TruncationReason,
    string Status,
    int? SqlErrorNumber = null,
    string? ErrorCategory = null,
    string? Error = null);

public sealed record ToolAuditEvent(
    string RequestId,
    string Tool,
    string? InitialDatabase,
    long DurationMs,
    string Status,
    string? ErrorCategory = null,
    string? Error = null);
