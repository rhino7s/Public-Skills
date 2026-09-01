namespace SqlServerReadonlyMcp.Sql;

public sealed record ColumnResult(string Name, string DataType);

public sealed record ResultSetResult(
    IReadOnlyList<ColumnResult> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows);

public sealed record QueryResult(
    bool Success,
    string RequestId,
    IReadOnlyList<ResultSetResult> ResultSets,
    int ReturnedRows,
    int ResultSizeBytes,
    long QueueWaitMs,
    long DurationMs,
    bool Truncated,
    string? TruncationReason,
    string? Guidance,
    ToolError? Error);

public sealed record ToolError(
    string Category,
    string Message,
    int? SqlErrorNumber = null,
    int? SqlErrorState = null,
    int? SqlErrorClass = null);

public sealed record ObjectSearchResult(
    bool Success,
    string RequestId,
    IReadOnlyList<ObjectSearchItem> Objects,
    bool Truncated,
    string? Guidance,
    ToolError? Error);

public sealed record ObjectSearchItem(
    string Database,
    string Schema,
    string Name,
    string Type,
    string TypeDescription,
    bool? CanExecute);

public sealed record ObjectDetailsResult(
    bool Success,
    string RequestId,
    ObjectIdentity? Object,
    bool? CanExecute,
    ObjectPermissionSet? Permissions,
    string? Definition,
    int StartLine,
    int ReturnedLines,
    bool DefinitionHasMore,
    int? NextStartLine,
    IReadOnlyList<ObjectDefinitionMatch> DefinitionMatches,
    int DefinitionMatchCount,
    int ReturnedDefinitionMatchCount,
    bool MatchesHasMore,
    int? NextMatchOffset,
    string? TruncationReason,
    string? Guidance,
    IReadOnlyList<ObjectColumn> Columns,
    bool ColumnsTruncated,
    IReadOnlyList<ObjectIndex> Indexes,
    bool IndexesTruncated,
    IReadOnlyList<ObjectParameter> Parameters,
    ToolError? Error);

public sealed record ObjectPermissionSet(
    bool CanViewDefinition,
    bool CanSelect,
    bool CanExecute,
    bool CanReferences,
    bool? CanInvoke);

public sealed record ObjectDefinitionMatch(
    int Line,
    string Text,
    int OccurrenceCount);

public sealed record ObjectReferenceSearchResult(
    bool Success,
    string RequestId,
    ObjectIdentity? Target,
    string? SearchDatabase,
    IReadOnlyList<ObjectReferenceItem> References,
    bool ReferencesHasMore,
    string? ReferencesTruncationReason,
    int? NextOffset,
    IReadOnlyList<JobReferenceItem> Jobs,
    bool JobsTruncated,
    string? Guidance,
    ToolError? Error);

public sealed record ObjectReferenceItem(
    string Database,
    string Schema,
    string Name,
    string Type,
    string TypeDescription,
    int OccurrenceCount,
    IReadOnlyList<ReferenceMatch> Matches);

public sealed record JobReferenceItem(
    string JobName,
    int StepId,
    string StepName,
    string? Database,
    int OccurrenceCount,
    IReadOnlyList<ReferenceMatch> Matches);

public sealed record ReferenceMatch(
    int Line,
    string Text,
    int OccurrenceCount);

public sealed record ObjectIdentity(
    string Database,
    string Schema,
    string Name,
    string Type,
    string TypeDescription,
    bool IsEncrypted);

public sealed record ObjectParameter(
    string Name,
    string DataType,
    short MaxLength,
    byte Precision,
    byte Scale,
    bool IsOutput);

public sealed record ObjectColumn(
    int Ordinal,
    string Name,
    string DataType,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    string? Collation);

public sealed record ObjectIndex(
    string Name,
    string Type,
    bool IsUnique,
    bool IsPrimaryKey,
    string? FilterDefinition,
    IReadOnlyList<ObjectIndexKeyColumn> KeyColumns,
    IReadOnlyList<string> IncludedColumns);

public sealed record ObjectIndexKeyColumn(
    string Name,
    bool Descending);
