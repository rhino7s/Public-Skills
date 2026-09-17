namespace SqlServerReadonlyMcp.Configuration;

public sealed class McpSettings
{
    public ConnectionSettings Connection { get; init; } = new();

    public QuerySettings Query { get; init; } = new();

    public LoggingSettings Logging { get; init; } = new();

    public CapabilitySettings Capabilities { get; init; } = new();
    public AccessSettings Access { get; init; } = new();
    public bool IsCatalogMode => ConnectionAuthenticationModes.Resolve(Connection) == ConnectionAuthenticationModes.WindowsIntegrated
        || Access.Mode == AccessSettings.Catalog;
}

public sealed class AccessSettings
{
    public const string Catalog = "catalog";
    public const string Development = "development";
    public string? Mode { get; init; }
}

public sealed class CapabilitySettings
{
    public string ListFunction { get; init; } = string.Empty;
    public string CheckFunction { get; init; } = string.Empty;
    public int PageSize { get; init; } = 100;
    public bool Enabled => !string.IsNullOrWhiteSpace(ListFunction);
}

public sealed class ConnectionSettings
{
    public string? Authentication { get; init; }

    public string Server { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public bool Encrypt { get; init; } = true;

    public bool TrustServerCertificate { get; init; }

    public int ConnectTimeoutSeconds { get; init; } = 5;

    public int MaxPoolSize { get; init; } = 4;
}

public static class ConnectionAuthenticationModes
{
    public const string WindowsIntegrated = "windowsIntegrated";

    public const string SqlPassword = "sqlPassword";

    public static string Resolve(ConnectionSettings settings) =>
        settings.Authentication ?? SqlPassword;
}

public sealed class QuerySettings
{
    public int TimeoutSeconds { get; init; } = 60;

    public int MaxRows { get; init; } = 200;

    public int MaxResultSizeKb { get; init; } = 256;

    public int MaxConcurrentQueries { get; init; } = 2;
}

public sealed class LoggingSettings
{
    public string Directory { get; init; } = "logs";

    public int RetentionDays { get; init; } = 20;

    public string MinimumLevel { get; init; } = "Information";

    public bool IncludeSqlText { get; init; } = false;

    public int MaxSqlTextChars { get; init; } = 65_536;
}
