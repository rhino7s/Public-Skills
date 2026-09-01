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

    private static string BuildConnectionString(ConnectionSettings settings)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server,
            UserID = settings.Username,
            Password = settings.Password,
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

        return builder.ConnectionString;
    }
}
