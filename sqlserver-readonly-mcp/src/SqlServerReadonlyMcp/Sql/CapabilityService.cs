using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Logging;

namespace SqlServerReadonlyMcp.Sql;

public sealed class CapabilityService(McpSettings settings, ICapabilityStore store, ILogger<CapabilityService> logger)
{
    public bool RequiresCheck => settings.IsCatalogMode;

    public async Task<CallToolResult?> CheckAccessAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Canceled();
        if (!RequiresCheck) return null;
        if (!settings.Capabilities.Enabled) return Unavailable();
        try
        {
            var allowed = await store.CheckAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowed) logger.LogWarning("Capability access check returned false.");
            return allowed ? null : Denied();
        }
        catch (Exception exception)
        {
            // Do not return SQL diagnostics, credentials, catalog names or function names to clients.
            logger.LogWarning("Capability access check failed: {Type}; SQL error {Number}.",
                exception.GetType().Name, (exception as Microsoft.Data.SqlClient.SqlException)?.Number);
            return FailureResult(exception, cancellationToken, checking: true);
        }
    }

    public async Task<ToolError?> CheckProcedureAccessAsync(string database, string schema, string name, CancellationToken token)
    {
        if (!settings.Capabilities.Enabled) return new("access_denied", "用户没有访问权限");
        try
        {
            token.ThrowIfCancellationRequested();
            var granted = await store.IsProcedureGrantedAsync(database, schema, name, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return granted ? null : new("access_denied", "用户没有访问权限");
        }
        catch (Exception exception)
        {
            logger.LogWarning("Procedure catalog authorization failed: {Type}; SQL error {Number}.",
                exception.GetType().Name, (exception as Microsoft.Data.SqlClient.SqlException)?.Number);
            var failure = FailureResult(exception, token, checking: true).StructuredContent!.Value;
            return new(failure.GetProperty("code").GetString()!, failure.GetProperty("message").GetString()!);
        }
    }

    public static CallToolResult Unavailable() => Error("access_check_unavailable", "暂时无法确认访问权限，本次操作未执行。");
    public static CallToolResult CheckTimedOut() => Error("access_check_unavailable", "访问检查超时，本次操作未执行；请稍后再试。");
    private static CallToolResult Canceled() => Error("canceled", "调用已取消。");
    private static CallToolResult FailureResult(Exception exception, CancellationToken token, bool checking)
    {
        if (token.IsCancellationRequested) return CallTiming.Current is { CallerToken.IsCancellationRequested: false }
            ? Error(checking ? "access_check_unavailable" : "capabilities_unavailable", "操作超时，本次调用已停止，请稍后再试。") : Canceled();
        if (exception is UnauthorizedAccessException || exception is Microsoft.Data.SqlClient.SqlException sql
            && SqlErrorClassifier.Categorize(sql.Number) == "permission_denied") return Denied();
        if (exception is InvalidDataException or ArgumentException || exception is Microsoft.Data.SqlClient.SqlException missing
            && missing.Number is 195 or 201 or 207 or 208 or 4121)
            return Error(checking ? "access_check_unavailable" : "capabilities_unavailable",
                checking ? "暂时无法确认访问权限，本次操作未执行。" : "能力目录暂时无法读取。");
        if (exception is Microsoft.Data.SqlClient.SqlException connection && SqlErrorClassifier.IsConnectionFailure(connection))
            return Error(checking ? "access_check_unavailable" : "capabilities_unavailable", PublicToolErrors.ConnectionFailed);
        if (exception is TimeoutException or Microsoft.Data.SqlClient.SqlException { Number: -2 })
            return Error(checking ? "access_check_unavailable" : "capabilities_unavailable", "请求超时，本次操作未执行。");
        return Error(checking ? "access_check_unavailable" : "capabilities_unavailable",
            checking ? "暂时无法确认访问权限，本次操作未执行。" : "能力目录暂时无法读取。");
    }

    public static CallToolResult Denied() => Error("access_denied", "用户没有访问权限");

    public async Task<CallToolResult> ListAsync(long offset, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Canceled();
        if (offset < 0 || offset > long.MaxValue - settings.Capabilities.PageSize)
            return Error("invalid_input", "offset 必须为可续取的非负整数。");
        if (!settings.Capabilities.Enabled) return Error("capabilities_disabled", "能力目录未启用。");
        try
        {
            var maximumBytes = checked(settings.Query.MaxResultSizeKb * 1024);
            var items = new List<CapabilityItem>();
            var ids = new HashSet<int>();
            (int Ord, int Id)? previous = null;
            var hasMore = false;
            await foreach (var row in store.ReadAsync(offset, settings.Capabilities.PageSize + 1, maximumBytes, cancellationToken).ConfigureAwait(false))
            {
                if (row.Id <= 0 || !ids.Add(row.Id) || previous is { } p && (row.Ord < p.Ord || row.Ord == p.Ord && row.Id <= p.Id)
                    || string.IsNullOrWhiteSpace(row.Summary))
                    throw new InvalidDataException("Invalid capability ordering, identity or summary.");
                previous = (row.Ord, row.Id);
                if (items.Count == settings.Capabilities.PageSize) { hasMore = true; break; }
                items.Add(new(row.Id, row.ObjectName, row.Summary, row.HasDescription));
                if (row.Oversized || Math.Max(ResponseBytes(Page(items, offset, true)), ResponseBytes(Page(items, offset, false))) > maximumBytes)
                {
                    items.RemoveAt(items.Count - 1);
                    if (items.Count == 0) return TooLarge();
                    hasMore = true;
                    break;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count == 0 && offset == 0 && RequiresCheck) return Denied();
            return Page(items, offset, hasMore);
        }
        catch (Exception exception) { return DirectoryFailure(exception, cancellationToken); }
    }

    public async Task<CallToolResult> DetailsAsync(int id, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Canceled();
        if (id <= 0) return Error("invalid_input", "id 必须为正整数。");
        if (!settings.Capabilities.Enabled) return Error("capabilities_disabled", "能力目录未启用。");
        try
        {
            var maximumBytes = checked(settings.Query.MaxResultSizeKb * 1024);
            var row = await store.ReadDetailsAsync(id, maximumBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (row is null) return Denied();
            if (row.Id != id || string.IsNullOrWhiteSpace(row.Summary) || row.Description is null
                || row.HasDescription && row.Description.Length == 0)
                throw new InvalidDataException("Invalid capability details.");
            if (row.Oversized) return TooLarge();
            var result = Success(new CapabilityDetailsResult(row.Id, row.ObjectName, row.Summary, row.HasDescription, row.Description),
                row.HasDescription ? "能力详情已读取。" : "该条目无详情，以摘要为完整说明。");
            return ResponseBytes(result) > maximumBytes ? TooLarge() : result;
        }
        catch (Exception exception) { return DirectoryFailure(exception, cancellationToken); }
    }

    private CallToolResult DirectoryFailure(Exception exception, CancellationToken token)
    {
        logger.LogWarning("Capability directory read failed: {Type}; SQL error {Number}.",
            exception.GetType().Name, (exception as Microsoft.Data.SqlClient.SqlException)?.Number);
        return FailureResult(exception, token, checking: false);
    }

    private static CallToolResult TooLarge() => Error("capability_too_large", "单条能力内容超过返回大小限制，请管理员缩短内容或调整限制。");
    private static int ResponseBytes(CallToolResult result) => JsonSerializer.SerializeToUtf8Bytes(result, Program.CreateToolJsonOptions()).Length;
    private static CallToolResult Page(IReadOnlyList<CapabilityItem> items, long offset, bool hasMore) =>
        Success(new CapabilityPage(items, hasMore, hasMore ? offset + items.Count : null),
            $"本页返回 {items.Count} 条能力摘要。" + (hasMore ? "请按 next_offset 继续读取。" : "摘要目录已读至末页。"));
    private static CallToolResult Success<T>(T value, string text) => new()
    {
        IsError = false,
        Content = [new TextContentBlock { Text = text }],
        StructuredContent = JsonSerializer.SerializeToElement(value, Program.CreateToolJsonOptions()),
    };

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
        StructuredContent = JsonSerializer.SerializeToElement(new { code, message }),
    };
}
