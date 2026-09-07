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
            DefaultDatabase = "ExampleDatabase",
        };

        var connectionString = SqlConnectionFactory.BuildConnectionString(settings);
        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.True(builder.IntegratedSecurity);
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
            DefaultDatabase = "ExampleDatabase",
        };

        var connectionString = SqlConnectionFactory.BuildConnectionString(settings);
        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.False(builder.IntegratedSecurity);
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
            SqlConnectionFactory.BuildConnectionString(settings));

        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("readonly_test", builder.UserID);
    }
}
