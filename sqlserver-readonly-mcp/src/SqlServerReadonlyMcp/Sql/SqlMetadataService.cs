using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Logging;

namespace SqlServerReadonlyMcp.Sql;

public sealed class SqlMetadataService
{
    private const int MaximumSearchResults = 20;
    private const int MaximumDefinitionLines = 800;
    private const int MaximumDefinitionSearchLength = 256;
    private const int MaximumDefinitionSearchMatches = 20;
    private const int MaximumReferenceResults = 50;
    private const int MaximumReferenceOffset = 1_000;
    private const int MaximumJobReferenceResults = 20;
    private const int MaximumColumns = 512;
    private const int MaximumIndexes = 128;
    private const int MaximumIndexColumns = 4_096;

    private readonly QuerySettings _querySettings;
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly QueryConcurrencyGate _concurrencyGate;
    private readonly AuditLogger _auditLogger;

    public SqlMetadataService(
        McpSettings settings,
        SqlConnectionFactory connectionFactory,
        QueryConcurrencyGate concurrencyGate,
        AuditLogger auditLogger)
    {
        _querySettings = settings.Query;
        _connectionFactory = connectionFactory;
        _concurrencyGate = concurrencyGate;
        _auditLogger = auditLogger;
    }

    public async Task<ObjectSearchResult> FindObjectAsync(
        string objectName,
        string database,
        string? objectTypes,
        bool exactMatch,
        CancellationToken cancellationToken)
    {
        var requestId = NewRequestId();
        var stopwatch = Stopwatch.StartNew();
        var objects = new List<ObjectSearchItem>();

        if (string.IsNullOrWhiteSpace(objectName))
        {
            return FinishSearch(requestId, database, stopwatch, objects, false,
                new ToolError("invalid_input", "objectName 不可为空。"));
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            return FinishSearch(requestId, database, stopwatch, objects, false,
                new ToolError("invalid_input", "database 不可为空。"));
        }

        try
        {
            var name = MultipartName.Parse(objectName);
            if (!string.IsNullOrWhiteSpace(name.Database) &&
                !string.Equals(database, name.Database, StringComparison.OrdinalIgnoreCase))
            {
                return FinishSearch(requestId, database, stopwatch, objects, false,
                    new ToolError("invalid_input", "objectName 中的数据库与 database 参数不一致。"));
            }

            if (!exactMatch && name.Object.Length < 3)
            {
                return FinishSearch(requestId, database, stopwatch, objects, false,
                    new ToolError("invalid_input", "模糊匹配的对象名称至少需要 3 个字符。"));
            }

            var schema = string.IsNullOrWhiteSpace(name.Schema) ? "dbo" : name.Schema;
            using var lease = await _concurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var types = ParseObjectTypes(objectTypes);
            var truncated = false;

            await using var connection = await _connectionFactory
                .OpenAsync(database.Trim(), cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _querySettings.TimeoutSeconds;
            command.CommandText = """
                SELECT TOP (@limit)
                    DB_NAME() AS database_name,
                    s.name AS schema_name,
                    o.name AS object_name,
                    o.type,
                    o.type_desc,
                    CASE WHEN o.type IN (N'P', N'PC') THEN
                        CONVERT(bit, COALESCE(HAS_PERMS_BY_NAME(
                            QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                            N'OBJECT',
                            N'EXECUTE'), 0))
                    END AS can_execute
                FROM sys.objects AS o
                INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                WHERE o.is_ms_shipped = 0
                  AND s.name = @schema
                  AND (@exact = 1 AND o.name = @name OR @exact = 0 AND o.name LIKE @pattern ESCAPE N'~')
                  AND (@filterTypes = 0 OR o.type IN (SELECT value FROM STRING_SPLIT(@types, N',')))
                ORDER BY o.name, o.type;
                """;
            command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = MaximumSearchResults + 1 });
            command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = schema });
            command.Parameters.Add(new SqlParameter("@exact", SqlDbType.Bit) { Value = exactMatch });
            command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = name.Object });
            command.Parameters.Add(new SqlParameter("@pattern", SqlDbType.NVarChar, 4000)
            {
                Value = $"%{EscapeLikePattern(name.Object)}%",
            });
            command.Parameters.Add(new SqlParameter("@filterTypes", SqlDbType.Bit) { Value = types.Count > 0 });
            command.Parameters.Add(new SqlParameter("@types", SqlDbType.NVarChar, 100)
            {
                Value = string.Join(',', types),
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (objects.Count >= MaximumSearchResults)
                {
                    truncated = true;
                    break;
                }

                objects.Add(new ObjectSearchItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3).TrimEnd(),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetBoolean(5)));
            }

            var guidance = objects.Count == 0
                ? "当前账号未发现匹配对象；对象可能不存在，也可能因缺少元数据可见性而不可见，不能据此断言对象不存在。"
                : null;
            return FinishSearch(requestId, database, stopwatch, objects, truncated, null, guidance);
        }
        catch (QueryQueueTimeoutException exception)
        {
            return FinishSearch(requestId, database, stopwatch, objects, false,
                new ToolError("busy", exception.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return FinishSearch(requestId, database, stopwatch, objects, false,
                new ToolError("canceled", "对象搜索已由调用方取消。"));
        }
        catch (SqlException exception)
        {
            return FinishSearch(requestId, database, stopwatch, objects, false, SqlError(exception));
        }
        catch (Exception exception)
        {
            return FinishSearch(requestId, database, stopwatch, objects, false,
                new ToolError("internal_error", Limit(exception.Message)));
        }
    }

    public async Task<ObjectReferenceSearchResult> FindObjectReferencesAsync(
        string targetDatabase,
        string targetObject,
        string searchDatabase,
        string? sourceTypes,
        bool includeJobs,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestId = NewRequestId();
        var stopwatch = Stopwatch.StartNew();
        var references = new List<ObjectReferenceItem>();
        var jobs = new List<JobReferenceItem>();
        ObjectIdentity? target = null;
        var actualSearchDatabase = string.IsNullOrWhiteSpace(searchDatabase) ? null : searchDatabase.Trim();

        if (string.IsNullOrWhiteSpace(targetDatabase) ||
            string.IsNullOrWhiteSpace(targetObject) ||
            actualSearchDatabase is null)
        {
            return FinishReferenceSearch(
                requestId,
                actualSearchDatabase,
                stopwatch,
                target,
                references,
                false,
                null,
                jobs,
                false,
                new ToolError("invalid_input", "targetDatabase、targetObject 和 searchDatabase 均不可为空。"));
        }

        if (offset < 0 || offset > MaximumReferenceOffset)
        {
            return FinishReferenceSearch(
                requestId,
                actualSearchDatabase,
                stopwatch,
                target,
                references,
                false,
                null,
                jobs,
                false,
                new ToolError("invalid_input", $"offset 范围必须为 0 至 {MaximumReferenceOffset}。"));
        }

        var actualLimit = Math.Clamp(limit, 1, MaximumReferenceResults);
        try
        {
            var name = MultipartName.Parse(targetObject);
            if (!string.IsNullOrWhiteSpace(name.Database))
            {
                return FinishReferenceSearch(
                    requestId,
                    actualSearchDatabase,
                    stopwatch,
                    target,
                    references,
                    false,
                    null,
                    jobs,
                    false,
                    new ToolError("invalid_input", "targetObject 不可包含数据库名；请使用独立的 targetDatabase 参数。"));
            }

            var schema = string.IsNullOrWhiteSpace(name.Schema) ? "dbo" : name.Schema;
            var types = ParseReferenceSourceTypes(sourceTypes);
            using var lease = await _concurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var targetMatches = await ResolveObjectAsync(
                name.Object,
                schema,
                targetDatabase.Trim(),
                cancellationToken).ConfigureAwait(false);
            if (targetMatches.Count == 0)
            {
                return FinishReferenceSearch(
                    requestId,
                    actualSearchDatabase,
                    stopwatch,
                    target,
                    references,
                    false,
                    null,
                    jobs,
                    false,
                    new ToolError(
                        "target_not_found",
                        "在 targetDatabase 中找不到指定对象；对象可能不存在，也可能因缺少元数据可见性而不可见。"));
            }

            if (targetMatches.Count > 1)
            {
                return FinishReferenceSearch(
                    requestId,
                    actualSearchDatabase,
                    stopwatch,
                    target,
                    references,
                    false,
                    null,
                    jobs,
                    false,
                    new ToolError("ambiguous_target", "目标对象不唯一，请明确指定 schema.object。"));
            }

            target = targetMatches[0];
            if (!IsReferenceTargetType(target.Type))
            {
                return FinishReferenceSearch(
                    requestId,
                    actualSearchDatabase,
                    stopwatch,
                    target,
                    references,
                    false,
                    null,
                    jobs,
                    false,
                    new ToolError("unsupported_target_type", "目标对象只支持 table、view、procedure 或 function。"));
            }

            var pattern = ObjectReferenceSearchHelper.CreatePattern(target, actualSearchDatabase);
            var referencesHasMore = await ReadObjectReferencesAsync(
                target,
                actualSearchDatabase,
                types,
                pattern,
                offset,
                actualLimit,
                references,
                cancellationToken).ConfigureAwait(false);
            var jobsTruncated = false;
            if (includeJobs)
            {
                jobsTruncated = await ReadJobReferencesAsync(
                    target,
                    actualSearchDatabase,
                    pattern,
                    jobs,
                    cancellationToken).ConfigureAwait(false);
            }

            var pagination = ObjectReferenceSearchHelper.CreatePagination(
                offset,
                references.Count,
                referencesHasMore,
                MaximumReferenceOffset);
            var guidance = references.Count == 0 && jobs.Count == 0
                ? "未发现文本命中候选；这不排除动态 SQL、同义词或运行时拼接产生的引用。"
                : pagination.TruncationReason == "max_offset"
                    ? "仍有其他数据库模块命中，但已达到最大分页范围；请缩小来源类型或查询范围。"
                : referencesHasMore
                    ? "仍有其他数据库模块命中；请按 nextOffset 继续读取。"
                    : null;
            return FinishReferenceSearch(
                requestId,
                actualSearchDatabase,
                stopwatch,
                target,
                references,
                referencesHasMore,
                pagination.NextOffset,
                jobs,
                jobsTruncated,
                null,
                guidance,
                pagination.TruncationReason);
        }
        catch (FormatException exception)
        {
            return FinishReferenceSearch(
                requestId,
                actualSearchDatabase,
                stopwatch,
                target,
                references,
                false,
                null,
                jobs,
                false,
                new ToolError("invalid_input", exception.Message));
        }
        catch (QueryQueueTimeoutException exception)
        {
            references.Clear();
            jobs.Clear();
            return FinishReferenceSearch(
                requestId,
                actualSearchDatabase,
                stopwatch,
                target,
                references,
                false,
                null,
                jobs,
                false,
                new ToolError("busy", exception.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            references.Clear();
            jobs.Clear();
            return FinishReferenceSearch(
                requestId,
                actualSearchDatabase,
                stopwatch,
                target,
                references,
                false,
                null,
                jobs,
                false,
                new ToolError("canceled", "文本搜索已由调用方取消。"));
        }
        catch (SqlException exception)
        {
            references.Clear();
            jobs.Clear();
            return FinishReferenceSearch(
                requestId,
                actualSearchDatabase,
                stopwatch,
                target,
                references,
                false,
                null,
                jobs,
                false,
                SqlError(exception));
        }
        catch (Exception exception)
        {
            references.Clear();
            jobs.Clear();
            return FinishReferenceSearch(
                requestId,
                actualSearchDatabase,
                stopwatch,
                target,
                references,
                false,
                null,
                jobs,
                false,
                new ToolError("internal_error", Limit(exception.Message)));
        }
    }

    public async Task<ObjectDetailsResult> GetObjectDetailsAsync(
        string objectName,
        string database,
        int startLine,
        int maxLines,
        string? definitionSearch,
        int matchOffset,
        int maxMatches,
        CancellationToken cancellationToken)
    {
        var requestId = NewRequestId();
        var stopwatch = Stopwatch.StartNew();
        var actualStartLine = Math.Max(1, startLine);
        var actualMaxLines = Math.Clamp(maxLines, 1, MaximumDefinitionLines);
        var actualDefinitionSearch = string.IsNullOrWhiteSpace(definitionSearch)
            ? null
            : definitionSearch.Trim();
        var actualMatchOffset = Math.Max(0, matchOffset);
        var actualMaxMatches = Math.Clamp(maxMatches, 1, MaximumDefinitionSearchMatches);

        if (string.IsNullOrWhiteSpace(objectName))
        {
            return FinishDefinition(requestId, database, stopwatch, actualStartLine,
                new ToolError("invalid_input", "objectName 不可为空。"));
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            return FinishDefinition(requestId, database, stopwatch, actualStartLine,
                new ToolError("invalid_input", "database 不可为空。"));
        }

        if (actualDefinitionSearch?.Length > MaximumDefinitionSearchLength)
        {
            return FinishDefinition(requestId, database, stopwatch, actualStartLine,
                new ToolError("invalid_input", $"definitionSearch 最多 {MaximumDefinitionSearchLength} 个字符。"));
        }

        try
        {
            using var lease = await _concurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var name = MultipartName.Parse(objectName);
            var effectiveDatabase = database.Trim();
            if (!string.IsNullOrWhiteSpace(name.Database))
            {
                if (!string.Equals(effectiveDatabase, name.Database, StringComparison.OrdinalIgnoreCase))
                {
                    return FinishDefinition(requestId, database, stopwatch, actualStartLine,
                        new ToolError("invalid_input", "objectName 中的数据库与 database 参数不一致。"));
                }
            }

            var matches = await ResolveObjectAsync(
                name.Object,
                string.IsNullOrWhiteSpace(name.Schema) ? "dbo" : name.Schema,
                effectiveDatabase,
                cancellationToken).ConfigureAwait(false);
            if (matches.Count == 0)
            {
                return FinishDefinition(requestId, effectiveDatabase, stopwatch, actualStartLine,
                    new ToolError("not_found", "在账号可访问的数据库中找不到指定对象。"));
            }

            if (matches.Count > 1)
            {
                var candidates = string.Join("、", matches.Take(10).Select(item =>
                    $"[{item.Database}].[{item.Schema}].[{item.Name}]"));
                return FinishDefinition(requestId, effectiveDatabase, stopwatch, actualStartLine,
                    new ToolError("ambiguous_object", $"对象名称不唯一，请提供 database/schema：{candidates}"));
            }

            var match = matches[0];
            await using var connection = await _connectionFactory
                .OpenAsync(match.Database, cancellationToken)
                .ConfigureAwait(false);
            var details = await ReadObjectDetailsAsync(
                connection,
                match,
                includeMetadata: actualDefinitionSearch is null,
                cancellationToken).ConfigureAwait(false);
            if (details.Definition is null)
            {
                if (IsSqlModule(match.Type) && !match.IsEncrypted)
                {
                    return FinishDefinition(
                        requestId,
                        match.Database,
                        stopwatch,
                        actualStartLine,
                        new ToolError(
                            "definition_unavailable",
                            "对象存在，但 SQL Server 未返回定义。请确认账号具有 VIEW DEFINITION；加密对象也无法读取。"),
                        match,
                        canExecute: details.CanExecute,
                        columns: details.Columns,
                        columnsTruncated: details.ColumnsTruncated,
                        indexes: details.Indexes,
                        indexesTruncated: details.IndexesTruncated,
                        parameters: details.Parameters,
                        permissions: details.Permissions);
                }

                return FinishDefinition(
                    requestId,
                    match.Database,
                    stopwatch,
                    actualStartLine,
                    null,
                    match,
                    canExecute: details.CanExecute,
                    null,
                    0,
                    false,
                    null,
                    details.Columns,
                    details.ColumnsTruncated,
                    details.Indexes,
                    details.IndexesTruncated,
                    details.Parameters,
                    permissions: details.Permissions);
            }

            var lines = NormalizeLines(details.Definition);
            var definitionByteBudget = Math.Max(4_096, _querySettings.MaxResultSizeKb * 768);
            if (actualDefinitionSearch is not null)
            {
                var selection = DefinitionSearchHelper.Select(
                    lines,
                    actualDefinitionSearch,
                    actualMatchOffset,
                    actualMaxMatches,
                    definitionByteBudget);
                if (selection.OversizedFirstMatch)
                {
                    return FinishDefinition(
                        requestId,
                        match.Database,
                        stopwatch,
                        1,
                        new ToolError(
                            "definition_match_too_large",
                            "当前定义匹配行已超过返回上限；请使用更精确的 definitionSearch。"),
                        match,
                        canExecute: details.CanExecute,
                        columns: details.Columns,
                        columnsTruncated: details.ColumnsTruncated,
                        indexes: details.Indexes,
                        indexesTruncated: details.IndexesTruncated,
                        parameters: details.Parameters,
                        permissions: details.Permissions);
                }

                var matchGuidance = selection.MatchCount == 0
                    ? "定义中未找到该关键词；可换用字段名、表名、参数名或计算表达式中的关键字。"
                    : selection.Matches.Count == 0
                        ? "matchOffset 已超过定义匹配范围；请从 0 或较小的 offset 重新读取。"
                    : selection.HasMore
                        ? "定义仍有其他匹配行；请按 nextMatchOffset 继续读取，或使用 startLine/maxLines 读取指定范围。"
                        : null;
                return FinishDefinition(
                    requestId,
                    match.Database,
                    stopwatch,
                    1,
                    null,
                    match,
                    canExecute: details.CanExecute,
                    returnedLines: selection.Matches.Count,
                    columns: details.Columns,
                    columnsTruncated: details.ColumnsTruncated,
                    indexes: details.Indexes,
                    indexesTruncated: details.IndexesTruncated,
                    parameters: details.Parameters,
                    permissions: details.Permissions,
                    definitionMatches: selection.Matches,
                    definitionMatchCount: selection.MatchCount,
                    matchesHasMore: selection.HasMore,
                    nextMatchOffset: selection.NextMatchOffset,
                    truncationReason: selection.TruncationReason,
                    guidance: matchGuidance);
            }

            var selectedLines = SelectLines(
                lines,
                actualStartLine - 1,
                actualMaxLines,
                definitionByteBudget,
                out var oversizedFirstLine);
            if (oversizedFirstLine)
            {
                return FinishDefinition(
                    requestId,
                    match.Database,
                    stopwatch,
                    actualStartLine,
                    new ToolError(
                        "definition_line_too_large",
                        "对象定义的单行已超过返回上限。请用 execute_sql 对 sys.sql_modules.definition 使用 SUBSTRING 分段读取。"),
                    match,
                    canExecute: details.CanExecute,
                    columns: details.Columns,
                    columnsTruncated: details.ColumnsTruncated,
                    indexes: details.Indexes,
                    indexesTruncated: details.IndexesTruncated,
                    parameters: details.Parameters,
                    permissions: details.Permissions);
            }

            var definition = string.Join(Environment.NewLine, selectedLines);
            var hasMore = actualStartLine - 1 + selectedLines.Length < lines.Length;
            return FinishDefinition(
                requestId,
                match.Database,
                stopwatch,
                actualStartLine,
                null,
                match,
                details.CanExecute,
                definition,
                selectedLines.Length,
                hasMore,
                hasMore ? actualStartLine + selectedLines.Length : null,
                details.Columns,
                details.ColumnsTruncated,
                details.Indexes,
                details.IndexesTruncated,
                details.Parameters,
                permissions: details.Permissions,
                guidance: hasMore
                    ? "定义尚未返回完整；请按 nextStartLine 继续读取，或使用 definitionSearch 定位相关片段。"
                    : null);
        }
        catch (FormatException exception)
        {
            return FinishDefinition(requestId, database, stopwatch, actualStartLine,
                new ToolError("invalid_input", exception.Message));
        }
        catch (QueryQueueTimeoutException exception)
        {
            return FinishDefinition(requestId, database, stopwatch, actualStartLine,
                new ToolError("busy", exception.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return FinishDefinition(requestId, database, stopwatch, actualStartLine,
                new ToolError("canceled", "定义读取已由调用方取消。"));
        }
        catch (SqlException exception)
        {
            return FinishDefinition(requestId, database, stopwatch, actualStartLine, SqlError(exception));
        }
        catch (Exception exception)
        {
            return FinishDefinition(requestId, database, stopwatch, actualStartLine,
                new ToolError("internal_error", Limit(exception.Message)));
        }
    }

    private async Task<bool> ReadObjectReferencesAsync(
        ObjectIdentity target,
        string searchDatabase,
        IReadOnlyList<string> sourceTypes,
        Regex pattern,
        int offset,
        int limit,
        List<ObjectReferenceItem> references,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenAsync(searchDatabase, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _querySettings.TimeoutSeconds;
        command.CommandText = """
            SELECT
                DB_NAME(),
                s.name,
                o.name,
                o.type,
                o.type_desc,
                m.definition
            FROM sys.sql_modules AS m
            INNER JOIN sys.objects AS o ON o.object_id = m.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND LOWER(LEFT(o.name, 4)) <> N'zold'
              AND o.type IN (SELECT value FROM STRING_SPLIT(@types, N','))
              AND m.definition LIKE @pattern ESCAPE N'~'
            ORDER BY s.name, o.name, o.type;
            """;
        command.Parameters.Add(new SqlParameter("@types", SqlDbType.NVarChar, 100)
        {
            Value = string.Join(',', sourceTypes),
        });
        command.Parameters.Add(new SqlParameter("@pattern", SqlDbType.NVarChar, 4000)
        {
            Value = $"%{EscapeLikePattern(target.Name)}%",
        });

        var matchedSources = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var selection = ObjectReferenceSearchHelper.FindMatches(reader.GetString(5), pattern);
            if (selection.OccurrenceCount == 0)
            {
                continue;
            }

            if (matchedSources++ < offset)
            {
                continue;
            }

            if (references.Count >= limit)
            {
                command.Cancel();
                return true;
            }

            references.Add(new ObjectReferenceItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3).TrimEnd(),
                reader.GetString(4),
                selection.OccurrenceCount,
                selection.Matches));
        }

        return false;
    }

    private async Task<bool> ReadJobReferencesAsync(
        ObjectIdentity target,
        string searchDatabase,
        Regex pattern,
        List<JobReferenceItem> jobs,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenAsync(searchDatabase, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _querySettings.TimeoutSeconds;
        command.CommandText = """
            SELECT
                j.name,
                js.step_id,
                js.step_name,
                js.database_name,
                js.command
            FROM msdb.dbo.sysjobs AS j
            INNER JOIN msdb.dbo.sysjobsteps AS js ON js.job_id = j.job_id
            WHERE js.database_name = @searchDatabase
              AND js.command LIKE @pattern ESCAPE N'~'
            ORDER BY j.name, js.step_id;
            """;
        command.Parameters.Add(new SqlParameter("@searchDatabase", SqlDbType.NVarChar, 128)
        {
            Value = searchDatabase,
        });
        command.Parameters.Add(new SqlParameter("@pattern", SqlDbType.NVarChar, 4000)
        {
            Value = $"%{EscapeLikePattern(target.Name)}%",
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var selection = ObjectReferenceSearchHelper.FindMatches(reader.GetString(4), pattern);
            if (selection.OccurrenceCount == 0)
            {
                continue;
            }

            if (jobs.Count >= MaximumJobReferenceResults)
            {
                command.Cancel();
                return true;
            }

            jobs.Add(new JobReferenceItem(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                selection.OccurrenceCount,
                selection.Matches));
        }

        return false;
    }

    private async Task<List<ObjectIdentity>> ResolveObjectAsync(
        string objectName,
        string schema,
        string database,
        CancellationToken cancellationToken)
    {
        var matches = new List<ObjectIdentity>();

        await using var connection = await _connectionFactory
            .OpenAsync(database, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _querySettings.TimeoutSeconds;
        command.CommandText = """
            SELECT TOP (11)
                DB_NAME(), s.name, o.name, o.type, o.type_desc,
                CONVERT(bit, COALESCE(OBJECTPROPERTYEX(o.object_id, 'IsEncrypted'), 0))
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN sys.sql_modules AS sm ON sm.object_id = o.object_id
            WHERE o.is_ms_shipped = 0
              AND o.name = @name
              AND s.name = @schema
            ORDER BY o.type;
            """;
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = objectName });
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = schema });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            matches.Add(new ObjectIdentity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3).TrimEnd(),
                reader.GetString(4),
                reader.GetBoolean(5)));
            if (matches.Count > 10)
            {
                return matches;
            }
        }

        return matches;
    }

    private async Task<DefinitionDetails> ReadObjectDetailsAsync(
        SqlConnection connection,
        ObjectIdentity identity,
        bool includeMetadata,
        CancellationToken cancellationToken)
    {
        string? definition;
        bool? canExecute;
        ObjectPermissionSet permissions;
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = _querySettings.TimeoutSeconds;
            command.CommandText = """
                SELECT
                    sm.definition,
                    CONVERT(bit, COALESCE(HAS_PERMS_BY_NAME(
                        QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                        N'OBJECT', N'VIEW DEFINITION'), 0)) AS can_view_definition,
                    CONVERT(bit, COALESCE(HAS_PERMS_BY_NAME(
                        QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                        N'OBJECT', N'SELECT'), 0)) AS can_select,
                    CONVERT(bit, COALESCE(HAS_PERMS_BY_NAME(
                        QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                        N'OBJECT', N'EXECUTE'), 0)) AS can_execute,
                    CONVERT(bit, COALESCE(HAS_PERMS_BY_NAME(
                        QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                        N'OBJECT', N'REFERENCES'), 0)) AS can_references
                FROM sys.objects AS o
                INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                LEFT JOIN sys.sql_modules AS sm ON sm.object_id = o.object_id
                WHERE s.name = @schema AND o.name = @name AND o.type = @type;
                """;
            AddIdentityParameters(command, identity);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("对象在详情读取期间已不存在。");
            }

            definition = reader.IsDBNull(0) ? null : reader.GetString(0);
            var canViewDefinition = reader.GetBoolean(1);
            var canSelect = reader.GetBoolean(2);
            var rawCanExecute = reader.GetBoolean(3);
            var canReferences = reader.GetBoolean(4);
            var canInvoke = identity.Type switch
            {
                "P" or "PC" or "FN" or "FS" => rawCanExecute,
                "IF" or "TF" or "FT" => canSelect,
                _ => (bool?)null,
            };
            permissions = new ObjectPermissionSet(
                canViewDefinition,
                canSelect,
                rawCanExecute,
                canReferences,
                canInvoke);
            canExecute = identity.Type is "P" or "PC" ? rawCanExecute : null;
        }

        var columns = includeMetadata
            ? await ReadColumnsAsync(connection, identity, cancellationToken).ConfigureAwait(false)
            : new MetadataList<ObjectColumn>([], false);
        var indexes = includeMetadata
            ? await ReadIndexesAsync(connection, identity, cancellationToken).ConfigureAwait(false)
            : new MetadataList<ObjectIndex>([], false);

        var parameters = new List<ObjectParameter>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = _querySettings.TimeoutSeconds;
            command.CommandText = """
                SELECT p.name, TYPE_NAME(p.user_type_id), p.max_length, p.precision, p.scale, p.is_output
                FROM sys.parameters AS p
                WHERE p.object_id = OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@name))
                ORDER BY p.parameter_id;
                """;
            command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = identity.Schema });
            command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = identity.Name });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                parameters.Add(new ObjectParameter(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.GetByte(3),
                    reader.GetByte(4),
                    reader.GetBoolean(5)));
            }
        }

        return new DefinitionDetails(
            definition,
            canExecute,
            permissions,
            columns.Items,
            columns.Truncated,
            indexes.Items,
            indexes.Truncated,
            parameters);
    }

    private async Task<MetadataList<ObjectColumn>> ReadColumnsAsync(
        SqlConnection connection,
        ObjectIdentity identity,
        CancellationToken cancellationToken)
    {
        var columns = new List<ObjectColumn>();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _querySettings.TimeoutSeconds;
        command.CommandText = """
            SELECT TOP (@limit)
                c.column_id,
                c.name,
                type_info.name AS type_name,
                type_info.is_user_defined,
                SCHEMA_NAME(type_info.schema_id) AS type_schema,
                c.max_length,
                c.precision,
                c.scale,
                c.is_nullable,
                c.is_identity,
                c.is_computed,
                c.collation_name
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            INNER JOIN sys.columns AS c ON c.object_id = o.object_id
            INNER JOIN sys.types AS type_info ON type_info.user_type_id = c.user_type_id
            WHERE s.name = @schema AND o.name = @name AND o.type = @type
            ORDER BY c.column_id;
            """;
        AddIdentityParameters(command, identity);
        command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = MaximumColumns + 1 });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new ObjectColumn(
                reader.GetInt32(0),
                reader.GetString(1),
                FormatDataType(
                    reader.GetString(2),
                    reader.GetBoolean(3),
                    reader.GetString(4),
                    reader.GetInt16(5),
                    reader.GetByte(6),
                    reader.GetByte(7)),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        var truncated = columns.Count > MaximumColumns;
        if (truncated)
        {
            columns.RemoveAt(columns.Count - 1);
        }

        return new MetadataList<ObjectColumn>(columns, truncated);
    }

    private async Task<MetadataList<ObjectIndex>> ReadIndexesAsync(
        SqlConnection connection,
        ObjectIdentity identity,
        CancellationToken cancellationToken)
    {
        if (identity.Type is not ("U" or "V"))
        {
            return new MetadataList<ObjectIndex>([], false);
        }

        var builders = new List<IndexBuilder>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = _querySettings.TimeoutSeconds;
            command.CommandText = """
                SELECT TOP (@limit)
                    i.index_id,
                    i.name,
                    i.type_desc,
                    i.is_unique,
                    i.is_primary_key,
                    i.filter_definition
                FROM sys.objects AS o
                INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                INNER JOIN sys.indexes AS i ON i.object_id = o.object_id
                WHERE s.name = @schema AND o.name = @name AND o.type = @type
                  AND i.index_id > 0
                  AND i.name IS NOT NULL
                  AND i.is_disabled = 0
                  AND i.is_hypothetical = 0
                ORDER BY i.index_id;
                """;
            AddIdentityParameters(command, identity);
            command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = MaximumIndexes + 1 });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                builders.Add(new IndexBuilder(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var truncated = builders.Count > MaximumIndexes;
        if (truncated)
        {
            builders.RemoveAt(builders.Count - 1);
        }

        if (builders.Count == 0)
        {
            return new MetadataList<ObjectIndex>([], truncated);
        }

        var buildersById = builders.ToDictionary(item => item.IndexId);
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = _querySettings.TimeoutSeconds;
            command.CommandText = """
                SELECT TOP (@limit)
                    i.index_id,
                    c.name,
                    ic.key_ordinal,
                    ic.is_descending_key,
                    ic.is_included_column,
                    ic.index_column_id
                FROM sys.objects AS o
                INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                INNER JOIN sys.indexes AS i ON i.object_id = o.object_id
                INNER JOIN sys.index_columns AS ic
                    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                INNER JOIN sys.columns AS c
                    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE s.name = @schema AND o.name = @name AND o.type = @type
                  AND i.index_id > 0
                  AND i.name IS NOT NULL
                  AND i.is_disabled = 0
                  AND i.is_hypothetical = 0
                ORDER BY
                    i.index_id,
                    CASE WHEN ic.key_ordinal > 0 THEN 0 ELSE 1 END,
                    ic.key_ordinal,
                    ic.index_column_id;
                """;
            AddIdentityParameters(command, identity);
            command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = MaximumIndexColumns + 1 });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rowCount = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowCount++;
                if (rowCount > MaximumIndexColumns)
                {
                    truncated = true;
                    break;
                }

                if (!buildersById.TryGetValue(reader.GetInt32(0), out var builder))
                {
                    continue;
                }

                var columnName = reader.GetString(1);
                var keyOrdinal = reader.GetByte(2);
                if (keyOrdinal > 0)
                {
                    builder.KeyColumns.Add(new ObjectIndexKeyColumn(columnName, reader.GetBoolean(3)));
                }
                else if (reader.GetBoolean(4))
                {
                    builder.IncludedColumns.Add(columnName);
                }
            }
        }

        var indexes = builders.Select(builder => new ObjectIndex(
            builder.Name,
            builder.Type,
            builder.IsUnique,
            builder.IsPrimaryKey,
            builder.FilterDefinition,
            builder.KeyColumns,
            builder.IncludedColumns)).ToArray();
        return new MetadataList<ObjectIndex>(indexes, truncated);
    }

    private static string FormatDataType(
        string typeName,
        bool isUserDefined,
        string typeSchema,
        short maxLength,
        byte precision,
        byte scale)
    {
        if (isUserDefined)
        {
            return $"[{typeSchema.Replace("]", "]]", StringComparison.Ordinal)}]." +
                $"[{typeName.Replace("]", "]]", StringComparison.Ordinal)}]";
        }

        return typeName.ToLowerInvariant() switch
        {
            "char" or "varchar" or "binary" or "varbinary" =>
                $"{typeName}({FormatLength(maxLength)})",
            "nchar" or "nvarchar" =>
                $"{typeName}({FormatLength(maxLength < 0 ? maxLength : (short)(maxLength / 2))})",
            "decimal" or "numeric" => $"{typeName}({precision}, {scale})",
            "datetime2" or "datetimeoffset" or "time" => $"{typeName}({scale})",
            "float" => $"{typeName}({precision})",
            _ => typeName,
        };
    }

    private static string FormatLength(short length) =>
        length < 0 ? "max" : length.ToString(CultureInfo.InvariantCulture);

    private static void AddIdentityParameters(SqlCommand command, ObjectIdentity identity)
    {
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = identity.Schema });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = identity.Name });
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.Char, 2) { Value = identity.Type });
    }

    private ObjectSearchResult FinishSearch(
        string requestId,
        string? database,
        Stopwatch stopwatch,
        IReadOnlyList<ObjectSearchItem> objects,
        bool truncated,
        ToolError? error,
        string? guidance = null)
    {
        stopwatch.Stop();
        WriteToolAudit(requestId, "find_object", database, stopwatch.ElapsedMilliseconds, error);
        return new ObjectSearchResult(error is null, requestId, objects, truncated, guidance, error);
    }

    private ObjectReferenceSearchResult FinishReferenceSearch(
        string requestId,
        string? searchDatabase,
        Stopwatch stopwatch,
        ObjectIdentity? target,
        IReadOnlyList<ObjectReferenceItem> references,
        bool referencesHasMore,
        int? nextOffset,
        IReadOnlyList<JobReferenceItem> jobs,
        bool jobsTruncated,
        ToolError? error,
        string? guidance = null,
        string? referencesTruncationReason = null)
    {
        stopwatch.Stop();
        WriteToolAudit(requestId, "find_object_references", searchDatabase, stopwatch.ElapsedMilliseconds, error);
        return new ObjectReferenceSearchResult(
            error is null,
            requestId,
            target,
            searchDatabase,
            references,
            referencesHasMore,
            referencesTruncationReason,
            nextOffset,
            jobs,
            jobsTruncated,
            guidance,
            error);
    }

    private ObjectDetailsResult FinishDefinition(
        string requestId,
        string? database,
        Stopwatch stopwatch,
        int startLine,
        ToolError? error,
        ObjectIdentity? identity = null,
        bool? canExecute = null,
        string? definition = null,
        int returnedLines = 0,
        bool hasMore = false,
        int? nextStartLine = null,
        IReadOnlyList<ObjectColumn>? columns = null,
        bool columnsTruncated = false,
        IReadOnlyList<ObjectIndex>? indexes = null,
        bool indexesTruncated = false,
        IReadOnlyList<ObjectParameter>? parameters = null,
        ObjectPermissionSet? permissions = null,
        IReadOnlyList<ObjectDefinitionMatch>? definitionMatches = null,
        int definitionMatchCount = 0,
        bool matchesHasMore = false,
        int? nextMatchOffset = null,
        string? truncationReason = null,
        string? guidance = null)
    {
        stopwatch.Stop();
        WriteToolAudit(requestId, "get_object_details", database, stopwatch.ElapsedMilliseconds, error);
        return new ObjectDetailsResult(
            error is null,
            requestId,
            identity,
            canExecute,
            permissions,
            definition,
            startLine,
            returnedLines,
            hasMore,
            nextStartLine,
            definitionMatches ?? [],
            definitionMatchCount,
            definitionMatches?.Count ?? 0,
            matchesHasMore,
            nextMatchOffset,
            truncationReason,
            guidance,
            columns ?? [],
            columnsTruncated,
            indexes ?? [],
            indexesTruncated,
            parameters ?? [],
            error);
    }

    private void WriteToolAudit(
        string requestId,
        string tool,
        string? database,
        long durationMilliseconds,
        ToolError? error) =>
        _auditLogger.WriteTool(new ToolAuditEvent(
            requestId,
            tool,
            database,
            durationMilliseconds,
            error is null ? "success" : "error",
            error?.Category,
            error?.Message));

    private static IReadOnlyList<string> ParseObjectTypes(string? objectTypes)
    {
        if (string.IsNullOrWhiteSpace(objectTypes))
        {
            return [];
        }

        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["table"] = ["U"],
            ["view"] = ["V"],
            ["procedure"] = ["P", "PC"],
            ["stored_procedure"] = ["P", "PC"],
            ["function"] = ["FN", "IF", "TF", "FS", "FT"],
        };
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in objectTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (aliases.TryGetValue(value, out var mapped))
            {
                result.UnionWith(mapped);
            }
            else if (value.Length is 1 or 2 && value.All(char.IsLetter))
            {
                result.Add(value.ToUpperInvariant());
            }
        }

        return result.ToArray();
    }

    private static IReadOnlyList<string> ParseReferenceSourceTypes(string? sourceTypes)
    {
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["procedure"] = ["P", "PC"],
            ["sp"] = ["P", "PC"],
            ["function"] = ["FN", "IF", "TF", "FS", "FT"],
            ["fnval"] = ["FN", "FS"],
            ["fntb"] = ["IF", "TF", "FT"],
            ["view"] = ["V"],
            ["trigger"] = ["TR", "TA"],
        };
        var requested = string.IsNullOrWhiteSpace(sourceTypes)
            ? ["procedure", "function", "view", "trigger"]
            : sourceTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in requested)
        {
            if (!aliases.TryGetValue(value, out var mapped))
            {
                throw new FormatException(
                    $"sourceTypes 不支持 '{value}'；只允许 procedure、function、view、trigger。");
            }

            result.UnionWith(mapped);
        }

        if (result.Count == 0)
        {
            throw new FormatException("sourceTypes 至少需要一种类型。");
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("~", "~~", StringComparison.Ordinal)
            .Replace("%", "~%", StringComparison.Ordinal)
            .Replace("_", "~_", StringComparison.Ordinal)
            .Replace("[", "~[", StringComparison.Ordinal);

    private static string[] NormalizeLines(string definition) =>
        definition.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static string[] SelectLines(
        IReadOnlyList<string> lines,
        int skip,
        int maximumLines,
        int maximumBytes,
        out bool oversizedFirstLine)
    {
        var result = new List<string>();
        var usedBytes = 0;
        oversizedFirstLine = false;
        foreach (var line in lines.Skip(skip).Take(maximumLines))
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (usedBytes + lineBytes > maximumBytes)
            {
                oversizedFirstLine = result.Count == 0;
                break;
            }

            result.Add(line);
            usedBytes += lineBytes;
        }

        return result.ToArray();
    }

    private static bool IsSqlModule(string type) => type is
        "P" or "PC" or "V" or "TR" or "FN" or "IF" or "TF" or "FS" or "FT";

    private static bool IsReferenceTargetType(string type) => type is
        "U" or "V" or "P" or "PC" or "FN" or "IF" or "TF" or "FS" or "FT";

    private static ToolError SqlError(SqlException exception) => SqlErrorClassifier.Create(exception);

    private static string Limit(string value) => value.Length <= 2_048 ? value : value[..2_048];

    private static string NewRequestId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private sealed record DefinitionDetails(
        string? Definition,
        bool? CanExecute,
        ObjectPermissionSet Permissions,
        IReadOnlyList<ObjectColumn> Columns,
        bool ColumnsTruncated,
        IReadOnlyList<ObjectIndex> Indexes,
        bool IndexesTruncated,
        IReadOnlyList<ObjectParameter> Parameters);

    private sealed record MetadataList<T>(IReadOnlyList<T> Items, bool Truncated);

    private sealed class IndexBuilder
    {
        public IndexBuilder(
            int indexId,
            string name,
            string type,
            bool isUnique,
            bool isPrimaryKey,
            string? filterDefinition)
        {
            IndexId = indexId;
            Name = name;
            Type = type;
            IsUnique = isUnique;
            IsPrimaryKey = isPrimaryKey;
            FilterDefinition = filterDefinition;
        }

        public int IndexId { get; }

        public string Name { get; }

        public string Type { get; }

        public bool IsUnique { get; }

        public bool IsPrimaryKey { get; }

        public string? FilterDefinition { get; }

        public List<ObjectIndexKeyColumn> KeyColumns { get; } = [];

        public List<string> IncludedColumns { get; } = [];
    }

    private sealed record MultipartName(string? Database, string? Schema, string Object)
    {
        public static MultipartName Parse(string value)
        {
            var parts = Split(value.Trim());
            return parts.Count switch
            {
                1 => new(null, null, parts[0]),
                2 => new(null, parts[0], parts[1]),
                3 => new(parts[0], parts[1], parts[2]),
                _ => throw new FormatException("objectName 必须是 object、schema.object 或 database.schema.object。"),
            };
        }

        private static List<string> Split(string value)
        {
            var result = new List<string>();
            var current = new System.Text.StringBuilder();
            var bracketed = false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '[')
                {
                    bracketed = true;
                    continue;
                }

                if (character == ']' && bracketed)
                {
                    if (index + 1 < value.Length && value[index + 1] == ']')
                    {
                        current.Append(']');
                        index++;
                        continue;
                    }

                    bracketed = false;
                    continue;
                }

                if (character == '.' && !bracketed)
                {
                    AddPart(result, current);
                    continue;
                }

                current.Append(character);
            }

            if (bracketed)
            {
                throw new FormatException("objectName 的方括号未闭合。");
            }

            AddPart(result, current);
            return result;
        }

        private static void AddPart(List<string> result, System.Text.StringBuilder current)
        {
            var part = current.ToString().Trim();
            current.Clear();
            if (part.Length == 0)
            {
                throw new FormatException("objectName 含有空的名称部分。");
            }

            result.Add(part);
        }
    }
}
