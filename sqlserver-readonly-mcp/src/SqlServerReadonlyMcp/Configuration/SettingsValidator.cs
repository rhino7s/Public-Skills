using Microsoft.Extensions.Logging;

namespace SqlServerReadonlyMcp.Configuration;

public static class SettingsValidator
{
    public static void Validate(McpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();
        Required(settings.Connection.Server, "connection.server", errors);
        ValidateAuthentication(settings.Connection, errors);
        if (settings.Access is null) errors.Add("access 必须是配置对象。");
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.Access.Mode) && settings.Access.Mode is not (AccessSettings.Catalog or AccessSettings.Development))
                errors.Add("access.mode 只允许 catalog 或 development。");
            if (settings.Access.Mode == AccessSettings.Development && ConnectionAuthenticationModes.Resolve(settings.Connection) == ConnectionAuthenticationModes.WindowsIntegrated)
                errors.Add("Windows 集成认证只支持 catalog 模式；请移除 access.mode 或设为 catalog。");
        }
        ValidateCapabilities(settings, errors);
        Range(settings.Connection.ConnectTimeoutSeconds, 1, 30, "connection.connectTimeoutSeconds", errors);
        Range(settings.Connection.MaxPoolSize, 1, 8, "connection.maxPoolSize", errors);
        Range(settings.Query.TimeoutSeconds, 1, 120, "query.timeoutSeconds", errors);
        Range(settings.Query.MaxRows, 1, 500, "query.maxRows", errors);
        Range(settings.Query.MaxResultSizeKb, 16, 512, "query.maxResultSizeKb", errors);
        Range(settings.Query.MaxConcurrentQueries, 1, 4, "query.maxConcurrentQueries", errors);
        Range(settings.Logging.RetentionDays, 1, 365, "logging.retentionDays", errors);
        Range(settings.Logging.MaxSqlTextChars, 1_024, 1_048_576, "logging.maxSqlTextChars", errors);
        Required(settings.Logging.Directory, "logging.directory", errors);

        if (settings.Connection.MaxPoolSize < settings.Query.MaxConcurrentQueries)
        {
            errors.Add("connection.maxPoolSize 不得小于 query.maxConcurrentQueries。");
        }

        if (!Enum.TryParse<LogLevel>(settings.Logging.MinimumLevel, true, out _))
        {
            errors.Add("logging.minimumLevel 必须是有效的 .NET LogLevel。");
        }

        if (errors.Count > 0)
        {
            throw new SettingsException("配置验证失败：" + string.Join(" ", errors));
        }
    }

    private static void ValidateCapabilities(McpSettings settings, ICollection<string> errors)
    {
        if (settings.Capabilities is null)
        {
            errors.Add("capabilities 必须是配置对象，不可为 null。");
            return;
        }
        var capabilities = settings.Capabilities;
        // Leave room for the extra row used to detect the next page.
        Range(capabilities.PageSize, 1, int.MaxValue - 1, "capabilities.pageSize", errors);
        foreach (var (name, value) in new[] { ("listFunction", capabilities.ListFunction), ("checkFunction", capabilities.CheckFunction) })
        {
            if (value is null) { errors.Add($"capabilities.{name} 不可为 null。"); continue; }
            if (string.IsNullOrWhiteSpace(value)) continue;
            try { _ = CapabilityFunctionName.Parse(value); }
            catch (ArgumentException) { errors.Add($"capabilities.{name} 必须是合法的 database.dbo.function 三段名称。"); }
        }
        if (!capabilities.Enabled && !string.IsNullOrWhiteSpace(capabilities.CheckFunction))
            errors.Add("capabilities.checkFunction 不得单独配置。");
        if (settings.Access is not null && settings.IsCatalogMode)
        {
            if (!capabilities.Enabled) errors.Add("catalog 模式必须配置 capabilities.listFunction。");
            if (string.IsNullOrWhiteSpace(capabilities.CheckFunction)) errors.Add("catalog 模式必须配置 capabilities.checkFunction。");
        }
    }

    private static void ValidateAuthentication(ConnectionSettings settings, ICollection<string> errors)
    {
        var authentication = ConnectionAuthenticationModes.Resolve(settings);
        if (string.Equals(
                authentication,
                ConnectionAuthenticationModes.WindowsIntegrated,
                StringComparison.Ordinal))
        {
            MustBeEmpty(settings.Username, "connection.username", authentication, errors);
            MustBeEmpty(settings.Password, "connection.password", authentication, errors);
            return;
        }

        if (string.Equals(
                authentication,
                ConnectionAuthenticationModes.SqlPassword,
                StringComparison.Ordinal))
        {
            Required(settings.Username, "connection.username", errors);
            Required(settings.Password, "connection.password", errors);
            return;
        }

        errors.Add(
            $"connection.authentication 只允许 '{ConnectionAuthenticationModes.WindowsIntegrated}' 或 " +
            $"'{ConnectionAuthenticationModes.SqlPassword}'。");
    }

    private static void MustBeEmpty(
        string? value,
        string name,
        string authentication,
        ICollection<string> errors)
    {
        if (!string.IsNullOrEmpty(value))
        {
            errors.Add($"{name} 在 connection.authentication='{authentication}' 时必须为空字符串。");
        }
    }

    private static void Required(string value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} 不能为空。");
        }
    }

    private static void Range(int value, int minimum, int maximum, string name, ICollection<string> errors)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add($"{name} 必须介于 {minimum} 与 {maximum} 之间。");
        }
    }
}
