using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class SqlConnectionFactoryTests
{
    [Fact]
    public void WindowsIntegratedUsesCurrentWindowsIdentityWithoutCredentials()
    {
        var settings = new ConnectionSettings
        {
            Authentication = ConnectionAuthenticationModes.WindowsIntegrated,
            Server = "sql.internal",
            Username = string.Empty,
            Password = string.Empty,
        };

        var connectionString = SqlConnectionFactory.BuildConnectionString(settings, "ExampleDatabase");
        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.True(builder.IntegratedSecurity);
        Assert.Equal(0, builder.ConnectRetryCount);
        Assert.Equal(5, builder.ConnectTimeout);
        Assert.Equal("ExampleDatabase", builder.InitialCatalog);
        Assert.Empty(builder.UserID);
        Assert.Empty(builder.Password);
        Assert.DoesNotContain("User ID=", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlPasswordUsesConfiguredCredentialsWithoutIntegratedSecurity()
    {
        var settings = new ConnectionSettings
        {
            Authentication = ConnectionAuthenticationModes.SqlPassword,
            Server = "sql.internal",
            Username = "readonly_test",
            Password = "not-a-real-secret",
        };

        var connectionString = SqlConnectionFactory.BuildConnectionString(settings, "ExampleDatabase");
        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("ExampleDatabase", builder.InitialCatalog);
        Assert.Equal("readonly_test", builder.UserID);
        Assert.Equal("not-a-real-secret", builder.Password);
    }

    [Fact]
    public void LegacyCredentialsWithoutAuthenticationModeUseSqlPassword()
    {
        var settings = new ConnectionSettings
        {
            Server = "sql.internal",
            Username = "readonly_test",
            Password = "not-a-real-secret",
        };

        var builder = new SqlConnectionStringBuilder(
            SqlConnectionFactory.BuildConnectionString(settings, "ExampleDatabase"));

        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("readonly_test", builder.UserID);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingTargetIsRejectedBeforeConnection(string? database)
    {
        var factory = new SqlConnectionFactory(new McpSettings());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => factory.OpenAsync(database!, CancellationToken.None));
    }

    [Fact]
    public void EachTargetIsExplicitAndCannotInjectConnectionOptions()
    {
        var settings = new ConnectionSettings { Server = "test.invalid", Authentication = "windowsIntegrated" };
        var first = new SqlConnectionStringBuilder(SqlConnectionFactory.BuildConnectionString(settings, "CatalogOne"));
        var second = new SqlConnectionStringBuilder(SqlConnectionFactory.BuildConnectionString(settings, "CatalogTwo;Integrated Security=false"));
        Assert.Equal("CatalogOne", first.InitialCatalog);
        Assert.Equal("CatalogTwo;Integrated Security=false", second.InitialCatalog);
        Assert.True(second.IntegratedSecurity);
    }

    [Fact]
    public void LegacyDefaultDatabaseIsIgnoredByLoader()
    {
        var path = Path.Combine(Path.GetTempPath(), "legacy-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                { "connection": { "server": "test.invalid", "authentication": "windowsIntegrated",
                  "defaultDatabase": "MustNotBeUsed" },
                  "capabilities": { "listFunction": "D.dbo.list", "checkFunction": "D.dbo.check" } }
                """);
            var settings = SettingsLoader.Load(path);
            var connection = new SqlConnectionStringBuilder(SqlConnectionFactory.BuildConnectionString(settings.Connection, "RequestedCatalog"));
            Assert.Equal("RequestedCatalog", connection.InitialCatalog);
            Assert.DoesNotContain("MustNotBeUsed", connection.ConnectionString);
        }
        finally { File.Delete(path); }
    }
}
