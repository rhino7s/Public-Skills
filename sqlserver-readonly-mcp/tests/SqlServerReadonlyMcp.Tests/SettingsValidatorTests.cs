using SqlServerReadonlyMcp.Configuration;

namespace SqlServerReadonlyMcp.Tests;

public sealed class SettingsValidatorTests
{
    [Fact]
    public void AcceptsLegacyCredentialsWithoutAuthenticationMode()
    {
        var settings = ValidSettings();

        SettingsValidator.Validate(settings);
    }

    [Fact]
    public void AcceptsWindowsIntegratedWithBlankCredentialFields()
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings
            {
                Authentication = ConnectionAuthenticationModes.WindowsIntegrated,
                Server = "sql.internal",
                Username = string.Empty,
                Password = string.Empty,
            },
        };

        SettingsValidator.Validate(settings);
    }

    [Fact]
    public void MissingAuthenticationDefaultsToSqlPassword()
    {
        var settings = new ConnectionSettings { Server = "sql.internal" };

        Assert.Equal(
            ConnectionAuthenticationModes.SqlPassword,
            ConnectionAuthenticationModes.Resolve(settings));
    }

    [Fact]
    public void MissingAuthenticationAndBlankCredentialsAreRejected()
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings { Server = "sql.internal" },
        };

        var exception = Assert.Throws<SettingsException>(() => SettingsValidator.Validate(settings));

        Assert.Contains("connection.username", exception.Message, StringComparison.Ordinal);
        Assert.Contains("connection.password", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsCredentialsInWindowsIntegratedMode()
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings
            {
                Authentication = ConnectionAuthenticationModes.WindowsIntegrated,
                Server = "sql.internal",
                Username = "domain-user",
                Password = "not-a-real-secret",
            },
        };

        var exception = Assert.Throws<SettingsException>(() => SettingsValidator.Validate(settings));

        Assert.Contains("connection.username", exception.Message, StringComparison.Ordinal);
        Assert.Contains("connection.password", exception.Message, StringComparison.Ordinal);
        Assert.Contains("必须为空字符串", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownAuthenticationMode()
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings
            {
                Authentication = "unknown",
                Server = "sql.internal",
            },
        };

        var exception = Assert.Throws<SettingsException>(() => SettingsValidator.Validate(settings));

        Assert.Contains("connection.authentication", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("SQLPASSWORD")]
    public void RejectsNonCanonicalAuthenticationMode(string authentication)
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings
            {
                Authentication = authentication,
                Server = "sql.internal",
                Username = "readonly_agent",
                Password = "not-a-real-secret",
            },
        };

        var exception = Assert.Throws<SettingsException>(() => SettingsValidator.Validate(settings));

        Assert.Contains("connection.authentication", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsWhitespaceSqlCredentials()
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings
            {
                Authentication = ConnectionAuthenticationModes.SqlPassword,
                Server = "sql.internal",
                Username = " ",
                Password = "\t",
            },
        };

        var exception = Assert.Throws<SettingsException>(() => SettingsValidator.Validate(settings));

        Assert.Contains("connection.username", exception.Message, StringComparison.Ordinal);
        Assert.Contains("connection.password", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsBlankCredentialsAndUnsafeLimitRanges()
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings
            {
                Authentication = ConnectionAuthenticationModes.SqlPassword,
            },
            Query = new QuerySettings { MaxRows = 0, MaxResultSizeKb = 8 },
        };

        var exception = Assert.Throws<SettingsException>(() => SettingsValidator.Validate(settings));

        Assert.Contains("connection.server", exception.Message, StringComparison.Ordinal);
        Assert.Contains("connection.username", exception.Message, StringComparison.Ordinal);
        Assert.Contains("connection.password", exception.Message, StringComparison.Ordinal);
        Assert.Contains("query.maxRows", exception.Message, StringComparison.Ordinal);
        Assert.Contains("query.maxResultSizeKb", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsValuesAboveInternalResourceLimits()
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings
            {
                Server = "sql.internal",
                Username = "readonly_agent",
                Password = "not-a-real-secret",
                ConnectTimeoutSeconds = 31,
                MaxPoolSize = 9,
            },
            Query = new QuerySettings
            {
                TimeoutSeconds = 121,
                MaxRows = 501,
                MaxResultSizeKb = 513,
                MaxConcurrentQueries = 5,
            },
        };

        var exception = Assert.Throws<SettingsException>(() => SettingsValidator.Validate(settings));

        Assert.Contains("connection.connectTimeoutSeconds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("connection.maxPoolSize", exception.Message, StringComparison.Ordinal);
        Assert.Contains("query.timeoutSeconds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("query.maxRows", exception.Message, StringComparison.Ordinal);
        Assert.Contains("query.maxResultSizeKb", exception.Message, StringComparison.Ordinal);
        Assert.Contains("query.maxConcurrentQueries", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsExactInternalResourceLimits()
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings
            {
                Server = "sql.internal",
                Username = "readonly_agent",
                Password = "not-a-real-secret",
                ConnectTimeoutSeconds = 30,
                MaxPoolSize = 8,
            },
            Query = new QuerySettings
            {
                TimeoutSeconds = 120,
                MaxRows = 500,
                MaxResultSizeKb = 512,
                MaxConcurrentQueries = 4,
            },
        };

        SettingsValidator.Validate(settings);
    }

    [Fact]
    public void RejectsPoolSmallerThanConcurrencyLimit()
    {
        var settings = new McpSettings
        {
            Connection = new ConnectionSettings
            {
                Server = "sql.internal",
                Username = "readonly_agent",
                Password = "not-a-real-secret",
                MaxPoolSize = 2,
            },
            Query = new QuerySettings { MaxConcurrentQueries = 3 },
        };

        var exception = Assert.Throws<SettingsException>(() => SettingsValidator.Validate(settings));

        Assert.Contains(
            "connection.maxPoolSize 不得小于 query.maxConcurrentQueries",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static McpSettings ValidSettings() => new()
    {
        Connection = new ConnectionSettings
        {
            Server = "sql.internal",
            Username = "readonly_agent",
            Password = "not-a-real-secret",
        },
    };
}
