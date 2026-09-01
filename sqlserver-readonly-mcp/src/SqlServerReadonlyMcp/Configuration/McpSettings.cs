namespace SqlServerReadonlyMcp.Configuration;

public sealed class McpSettings
{
    public ConnectionSettings Connection { get; init; } = new();

    public QuerySettings Query { get; init; } = new();

    public LoggingSettings Logging { get; init; } = new();
}

public sealed class ConnectionSettings
{
    public string Server { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string DefaultDatabase { get; init; } = string.Empty;

    public bool Encrypt { get; init; } = true;

    public bool TrustServerCertificate { get; init; }

    public int ConnectTimeoutSeconds { get; init; } = 10;

    public int MaxPoolSize { get; init; } = 4;
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

    public bool IncludeSqlText { get; init; } = true;

    public int MaxSqlTextChars { get; init; } = 65_536;
}
