using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;

namespace SqlServerReadonlyMcp.Sql;

public sealed class SqlConnectionFactory
{
    private readonly ConnectionSettings _settings;

    public SqlConnectionFactory(McpSettings settings)
    {
        _settings = settings.Connection;
    }

    public async Task<SqlConnection> OpenAsync(string database, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(BuildConnectionString(_settings, database));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static string BuildConnectionString(ConnectionSettings settings, string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server,
            InitialCatalog = database,
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
