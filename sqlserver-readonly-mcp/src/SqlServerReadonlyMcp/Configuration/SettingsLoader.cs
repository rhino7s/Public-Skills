using System.Text.Json;

namespace SqlServerReadonlyMcp.Configuration;

public static class SettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string ResolveConfigPath(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--config", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new SettingsException("--config 后必须提供配置文件路径。");
            }

            return Path.GetFullPath(args[index + 1]);
        }

        var environmentPath = Environment.GetEnvironmentVariable("SQLSERVER_MCP_CONFIG");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        return Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
    }

    public static McpSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new SettingsException($"配置文件不存在：{path}");
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<McpSettings>(json, JsonOptions)
                ?? throw new SettingsException("配置文件内容为空。");
            SettingsValidator.Validate(settings);
            return settings;
        }
        catch (JsonException exception)
        {
            throw new SettingsException($"配置文件不是有效 JSON：{exception.Message}", exception);
        }
    }
}

public sealed class SettingsException : Exception
{
    public SettingsException(string message)
        : base(message)
    {
    }

    public SettingsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
