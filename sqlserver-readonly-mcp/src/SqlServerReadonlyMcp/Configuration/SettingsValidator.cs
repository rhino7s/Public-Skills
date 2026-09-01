using Microsoft.Extensions.Logging;

namespace SqlServerReadonlyMcp.Configuration;

public static class SettingsValidator
{
    public static void Validate(McpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();
        Required(settings.Connection.Server, "connection.server", errors);
        Required(settings.Connection.Username, "connection.username", errors);
        Required(settings.Connection.Password, "connection.password", errors);
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
