using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;

namespace SqlServerReadonlyMcp.Sql;

public sealed class SqlConnectionFactory
{
    private readonly ConnectionSettings _settings;
    private readonly string _connectionString;

    public SqlConnectionFactory(McpSettings settings)
    {
        _settings = settings.Connection;
        _connectionString = BuildConnectionString(_settings);
    }

    public async Task<SqlConnection> OpenAsync(string? database, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(database) &&
                !string.Equals(database, connection.Database, StringComparison.OrdinalIgnoreCase))
            {
                await connection.ChangeDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static string BuildConnectionString(ConnectionSettings settings)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server,
            InitialCatalog = settings.DefaultDatabase,
            Encrypt = settings.Encrypt,
            TrustServerCertificate = settings.TrustServerCertificate,
            ConnectTimeout = settings.ConnectTimeoutSeconds,
            MaxPoolSize = settings.MaxPoolSize,
            MinPoolSize = 0,
            MultipleActiveResultSets = false,
            PersistSecurityInfo = false,
            ApplicationName = "sqlserver-readonly-mcp",
            Pooling = true,
        };

        var authentication = ConnectionAuthenticationModes.Resolve(settings);
        if (string.Equals(
                authentication,
                ConnectionAuthenticationModes.WindowsIntegrated,
                StringComparison.Ordinal))
        {
            builder.IntegratedSecurity = true;
        }
        else if (string.Equals(
                     authentication,
                     ConnectionAuthenticationModes.SqlPassword,
                     StringComparison.Ordinal))
        {
            builder.UserID = settings.Username;
            builder.Password = settings.Password;
        }
        else
        {
            throw new InvalidOperationException($"不支持的 SQL Server 认证模式：{authentication}");
        }

        return builder.ConnectionString;
    }
}
