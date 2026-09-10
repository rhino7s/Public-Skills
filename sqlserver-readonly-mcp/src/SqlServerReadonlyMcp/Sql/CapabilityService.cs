using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SqlServerReadonlyMcp.Configuration;

namespace SqlServerReadonlyMcp.Sql;

public sealed class CapabilityService(McpSettings settings, ICapabilityStore store, ILogger<CapabilityService> logger)
{
    public bool RequiresCheck => settings.Capabilities.Enabled &&
        ConnectionAuthenticationModes.Resolve(settings.Connection) == ConnectionAuthenticationModes.WindowsIntegrated;

    public async Task<CallToolResult?> CheckAccessAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Canceled();
        if (!RequiresCheck) return null;
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

    public static CallToolResult Unavailable() => Error("access_check_unavailable", "访问检查暂不可用，本次操作未执行；可稍后重试，不得绕过检查。");
    private static CallToolResult Canceled() => Error("canceled", "调用已取消。");
    private static CallToolResult FailureResult(Exception exception, CancellationToken token, bool checking)
    {
        if (token.IsCancellationRequested) return Canceled();
        if (exception is UnauthorizedAccessException || exception is Microsoft.Data.SqlClient.SqlException sql
            && SqlErrorClassifier.Categorize(sql.Number) == "permission_denied") return Denied();
        if (exception is InvalidDataException or ArgumentException || exception is Microsoft.Data.SqlClient.SqlException missing
            && missing.Number is 195 or 201 or 207 or 208 or 4121)
            return Error(checking ? "access_check_unavailable" : "capabilities_unavailable", "能力配置不可用，请联系管理员处理。");
        return checking ? Unavailable() : Error("capabilities_unavailable", "能力目录暂不可用，请稍后重试；本次不得继续业务操作。");
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
            var builder = new StringBuilder();
            var ids = new HashSet<int>();
            (int Ord, int Id)? previous = null;
            var count = 0;
            var hasMore = false;
            // Reserve space for the page footer and the protocol envelope. Count JSON-escaped text,
            // rather than assuming one UTF-16 character equals one byte.
            var usedBytes = 512;
            await foreach (var row in store.ReadAsync(offset, settings.Capabilities.PageSize + 1, maximumBytes, cancellationToken).ConfigureAwait(false))
            {
                if (!ids.Add(row.Id) || previous is { } p && (row.Ord < p.Ord || row.Ord == p.Ord && row.Id <= p.Id)
                    || string.IsNullOrWhiteSpace(row.Description))
                    throw new InvalidDataException("Invalid capability ordering, identity or description.");
                previous = (row.Ord, row.Id);
                if (count == settings.Capabilities.PageSize) { hasMore = true; break; }
                var item = (count == 0 ? string.Empty : "\n\n---\n\n") + row.Description;
                var itemBytes = JsonSerializer.SerializeToUtf8Bytes(item, Program.CreateToolJsonOptions()).Length;
                if (row.Oversized || usedBytes + itemBytes > maximumBytes)
                {
                    if (count == 0) return Error("capability_too_large", "单条能力说明超过返回大小限制，请管理员缩短说明或调整限制。");
                    hasMore = true;
                    break;
                }
                builder.Append(item);
                usedBytes += itemBytes;
                count++;
            }
            if (count == 0 && offset == 0 && RequiresCheck) return Denied();
            var next = hasMore ? (offset + count).ToString(CultureInfo.InvariantCulture) : "null";
            builder.Append(CultureInfo.InvariantCulture, $"\n\n---\n本页返回 {count} 条；has_more={hasMore.ToString().ToLowerInvariant()}；next_offset={next}。");
            if (hasMore) builder.Append("请使用 next_offset 继续读取，当前目录尚未完整。");
            return new() { Content = [new TextContentBlock { Text = builder.ToString() }] };
        }
        catch (Exception exception)
        {
            logger.LogWarning("Capability directory read failed: {Type}; SQL error {Number}.",
                exception.GetType().Name, (exception as Microsoft.Data.SqlClient.SqlException)?.Number);
            return FailureResult(exception, cancellationToken, checking: false);
        }
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
        StructuredContent = JsonSerializer.SerializeToElement(new { code, message }),
    };
}
