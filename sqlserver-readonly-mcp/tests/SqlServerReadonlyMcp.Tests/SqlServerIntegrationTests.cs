using System.Text.Json;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class SqlServerIntegrationTests
{
    private const string ConfigVariable = "SQLSERVER_MCP_INTEGRATION_CONFIG";
    private const string ExecutableVariable = "SQLSERVER_MCP_INTEGRATION_EXE";
    private const string QueryDatabaseVariable = "SQLSERVER_MCP_INTEGRATION_QUERY_DATABASE";
    private const string TargetDatabaseVariable = "SQLSERVER_MCP_INTEGRATION_TARGET_DATABASE";
    private const string TargetObjectVariable = "SQLSERVER_MCP_INTEGRATION_TARGET_OBJECT";
    private const string SearchDatabaseVariable = "SQLSERVER_MCP_INTEGRATION_SEARCH_DATABASE";
    private const string DetailsDatabaseVariable = "SQLSERVER_MCP_INTEGRATION_DETAILS_DATABASE";
    private const string DetailsObjectVariable = "SQLSERVER_MCP_INTEGRATION_DETAILS_OBJECT";
    private const string DetailsSearchVariable = "SQLSERVER_MCP_INTEGRATION_DETAILS_SEARCH";

    public static bool IsReferenceSearchConfigured =>
        OptionalEnvironmentVariable(ConfigVariable) is not null &&
        OptionalEnvironmentVariable(TargetDatabaseVariable) is not null &&
        OptionalEnvironmentVariable(TargetObjectVariable) is not null &&
        OptionalEnvironmentVariable(SearchDatabaseVariable) is not null;

    public static bool IsDefinitionSearchConfigured =>
        OptionalEnvironmentVariable(ConfigVariable) is not null &&
        OptionalEnvironmentVariable(DetailsDatabaseVariable) is not null &&
        OptionalEnvironmentVariable(DetailsObjectVariable) is not null &&
        OptionalEnvironmentVariable(DetailsSearchVariable) is not null;

    public static bool IsAccessCheckConfigured =>
        OptionalEnvironmentVariable(ConfigVariable) is not null;

    public static bool IsQueryConfigured =>
        OptionalEnvironmentVariable(ConfigVariable) is not null &&
        OptionalEnvironmentVariable(QueryDatabaseVariable) is not null;

    [Fact(
        Timeout = 60_000,
        Skip = "未配置真实库查询测试。",
        SkipUnless = nameof(IsQueryConfigured))]
    public async Task RealServerExecutesReadOnlyQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = CreateTransport(RequiredEnvironmentVariable(ConfigVariable), "sqlserver-readonly-mcp-query-integration");
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        var result = await client.CallToolAsync(
            "execute_sql",
            new Dictionary<string, object?>
            {
                ["database"] = RequiredEnvironmentVariable(QueryDatabaseVariable),
                ["sql"] = "SELECT CAST(1 AS int) AS connection_probe;",
            },
            cancellationToken: cancellationToken);
        var content = Assert.NotNull(result.StructuredContent);
        AssertToolSucceeded(result, content);
        Assert.False(content.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, content.GetProperty("returnedRows").GetInt32());
        Assert.Equal(1, content.GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetInt32());
    }

    [Fact(
        Timeout = 180_000,
        Skip = "未配置真实库权限检查。",
        SkipUnless = nameof(IsAccessCheckConfigured))]
    public async Task AccessCheckRunsAsCurrentConnectionIdentity()
    {
        var configPath = RequiredEnvironmentVariable(ConfigVariable);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(File.Exists(configPath), $"找不到集成测试配置：{configPath}");

        var settings = SettingsLoader.Load(configPath);
        var connectionString = SqlConnectionFactory.BuildConnectionString(settings.Connection);
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "check-access.sql");

        Assert.True(File.Exists(scriptPath), $"找不到权限检查脚本：{scriptPath}");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var scopeCommand = connection.CreateCommand())
        {
            scopeCommand.CommandText =
                "EXEC sys.sp_set_session_context " +
                "@key=N'sqlserver_readonly_mcp.accessCheckDatabase', @value=@database;";
            scopeCommand.Parameters.AddWithValue("@database", connection.Database);
            await scopeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(scriptPath, cancellationToken);
        command.CommandTimeout = 120;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("currentSession", reader.GetString(reader.GetOrdinal("CheckMode")));
        Assert.False(reader.IsDBNull(reader.GetOrdinal("LoginName")));
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(reader.GetOrdinal("LoginName"))));

        Assert.True(await reader.NextResultAsync(cancellationToken));
        Assert.NotEqual(-1, reader.GetOrdinal("DatabaseName"));
        Assert.NotEqual(-1, reader.GetOrdinal("DatabaseUser"));
        Assert.NotEqual(-1, reader.GetOrdinal("role_sys"));
        Assert.NotEqual(-1, reader.GetOrdinal("grant"));
        Assert.NotEqual(-1, reader.GetOrdinal("execute_details"));
        var checkErrorOrdinal = reader.GetOrdinal("check_error");

        Assert.True(await reader.ReadAsync(cancellationToken), "权限检查没有返回目标数据库结果。");
        var checkError = reader.IsDBNull(checkErrorOrdinal)
            ? null
            : reader.GetString(checkErrorOrdinal);
        Assert.True(string.IsNullOrWhiteSpace(checkError), $"权限检查失败：{checkError}");
        Assert.Equal(
            connection.Database,
            reader.GetString(reader.GetOrdinal("DatabaseName")),
            ignoreCase: true);
        Assert.False(reader.IsDBNull(reader.GetOrdinal("DatabaseUser")));
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(reader.GetOrdinal("DatabaseUser"))));
        Assert.False(await reader.ReadAsync(cancellationToken), "单数据库检查不应返回额外数据库。");
    }

    [Fact(
        Timeout = 120_000,
        Skip = "未配置 find_object_references 真实库测试案例。",
        SkipUnless = nameof(IsReferenceSearchConfigured))]
    public async Task RealServerSupportsObjectReferenceSearch()
    {
        var configPath = RequiredEnvironmentVariable(ConfigVariable);
        var targetDatabase = RequiredEnvironmentVariable(TargetDatabaseVariable);
        var targetObject = RequiredEnvironmentVariable(TargetObjectVariable);
        var searchDatabase = RequiredEnvironmentVariable(SearchDatabaseVariable);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(File.Exists(configPath), $"找不到集成测试配置：{configPath}");

        var transport = CreateTransport(configPath, "sqlserver-readonly-mcp-reference-integration");

        await using var client = await McpClient.CreateAsync(
            transport,
            cancellationToken: cancellationToken);

        var firstPage = await client.CallToolAsync(
            "find_object_references",
            new Dictionary<string, object?>
            {
                ["targetDatabase"] = targetDatabase,
                ["targetObject"] = targetObject,
                ["searchDatabase"] = searchDatabase,
                ["includeJobs"] = false,
                ["limit"] = 1,
            },
            cancellationToken: cancellationToken);
        var firstPageContent = Assert.NotNull(firstPage.StructuredContent);

        AssertToolSucceeded(firstPage, firstPageContent);
        Assert.True(firstPageContent.GetProperty("success").GetBoolean());
        Assert.Equal(
            targetDatabase,
            firstPageContent.GetProperty("target").GetProperty("database").GetString(),
            ignoreCase: true);
        Assert.Equal(
            searchDatabase,
            firstPageContent.GetProperty("searchDatabase").GetString(),
            ignoreCase: true);
        AssertCompactText(firstPage, "references");

        var references = firstPageContent.GetProperty("references");
        AssertNoObsoleteSourceModules(references);
        if (OptionalBoolean("SQLSERVER_MCP_INTEGRATION_REQUIRE_REFERENCE"))
        {
            Assert.NotEmpty(references.EnumerateArray());
        }

        var currentPageContent = firstPageContent;
        var currentOffset = 0;
        while (currentPageContent.GetProperty("referencesHasMore").GetBoolean())
        {
            var nextOffsetElement = currentPageContent.GetProperty("nextOffset");
            if (nextOffsetElement.ValueKind == JsonValueKind.Null)
            {
                Assert.Equal(
                    "max_offset",
                    currentPageContent.GetProperty("referencesTruncationReason").GetString());
                break;
            }

            var nextOffset = nextOffsetElement.GetInt32();
            Assert.True(nextOffset > currentOffset, "nextOffset 必须向后推进。");
            var nextPage = await client.CallToolAsync(
                "find_object_references",
                new Dictionary<string, object?>
                {
                    ["targetDatabase"] = targetDatabase,
                    ["targetObject"] = targetObject,
                    ["searchDatabase"] = searchDatabase,
                    ["offset"] = nextOffset,
                    ["limit"] = 50,
                },
                cancellationToken: cancellationToken);

            currentPageContent = Assert.NotNull(nextPage.StructuredContent);
            AssertToolSucceeded(nextPage, currentPageContent);
            AssertNoObsoleteSourceModules(currentPageContent.GetProperty("references"));
            currentOffset = nextOffset;
        }

        if (OptionalBoolean("SQLSERVER_MCP_INTEGRATION_INCLUDE_JOBS"))
        {
            var withJobs = await client.CallToolAsync(
                "find_object_references",
                new Dictionary<string, object?>
                {
                    ["targetDatabase"] = targetDatabase,
                    ["targetObject"] = targetObject,
                    ["searchDatabase"] = searchDatabase,
                    ["includeJobs"] = true,
                    ["limit"] = 1,
                },
                cancellationToken: cancellationToken);
            var withJobsContent = Assert.NotNull(withJobs.StructuredContent);

            AssertToolSucceeded(withJobs, withJobsContent);
        }

        var missingTarget = await client.CallToolAsync(
            "find_object_references",
            new Dictionary<string, object?>
            {
                ["targetDatabase"] = targetDatabase,
                ["targetObject"] = $"dbo.__mcp_missing_{Guid.NewGuid():N}",
                ["searchDatabase"] = searchDatabase,
            },
            cancellationToken: cancellationToken);
        var missingTargetContent = Assert.NotNull(missingTarget.StructuredContent);

        Assert.True(missingTarget.IsError);
        Assert.False(missingTargetContent.GetProperty("success").GetBoolean());
        Assert.Equal(
            "target_not_found",
            missingTargetContent.GetProperty("error").GetProperty("category").GetString());
    }

    private static void AssertNoObsoleteSourceModules(JsonElement references)
    {
        Assert.All(
            references.EnumerateArray(),
            reference => Assert.False(
                reference.GetProperty("name").GetString()?.StartsWith(
                    "zold",
                    StringComparison.OrdinalIgnoreCase) == true,
                $"不应返回 zold 来源模块：{reference.GetProperty("name").GetString()}"));
    }

    [Fact(
        Timeout = 120_000,
        Skip = "未配置 get_object_details 真实库定义搜索测试案例。",
        SkipUnless = nameof(IsDefinitionSearchConfigured))]
    public async Task RealServerSupportsDefinitionSearchPagination()
    {
        var configPath = RequiredEnvironmentVariable(ConfigVariable);
        var database = RequiredEnvironmentVariable(DetailsDatabaseVariable);
        var objectName = RequiredEnvironmentVariable(DetailsObjectVariable);
        var definitionSearch = RequiredEnvironmentVariable(DetailsSearchVariable);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(File.Exists(configPath), $"找不到集成测试配置：{configPath}");

        var transport = CreateTransport(configPath, "sqlserver-readonly-mcp-details-integration");
        await using var client = await McpClient.CreateAsync(
            transport,
            cancellationToken: cancellationToken);

        await AssertDefinitionPaginationAsync(
            client,
            database,
            objectName,
            definitionSearch,
            cancellationToken);
    }

    private static StdioClientTransport CreateTransport(string configPath, string name)
    {
        var executable = OptionalEnvironmentVariable(ExecutableVariable);
        if (executable is not null)
        {
            Assert.True(Path.IsPathFullyQualified(executable) && File.Exists(executable),
                "集成测试指定的发布程序不存在或不是绝对路径；禁止回退到测试程序集。");
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = executable ?? "dotnet",
            Arguments = executable is null
                ? [typeof(SqlServerReadonlyMcp.Program).Assembly.Location, "--config", configPath]
                : ["--config", configPath],
            WorkingDirectory = Path.GetDirectoryName(configPath),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });
    }

    private static async Task AssertDefinitionPaginationAsync(
        McpClient client,
        string database,
        string objectName,
        string definitionSearch,
        CancellationToken cancellationToken)
    {
        var firstPage = await client.CallToolAsync(
            "get_object_details",
            new Dictionary<string, object?>
            {
                ["database"] = database,
                ["objectName"] = objectName,
                ["definitionSearch"] = definitionSearch,
                ["maxMatches"] = 1,
            },
            cancellationToken: cancellationToken);
        var content = Assert.NotNull(firstPage.StructuredContent);

        AssertToolSucceeded(firstPage, content);
        Assert.True(content.GetProperty("definitionMatchCount").GetInt32() > 0);
        Assert.Equal(1, content.GetProperty("returnedDefinitionMatchCount").GetInt32());
        AssertCompactText(firstPage, "definitionMatches");

        if (!content.GetProperty("matchesHasMore").GetBoolean())
        {
            return;
        }

        var nextOffset = content.GetProperty("nextMatchOffset").GetInt32();
        var nextPage = await client.CallToolAsync(
            "get_object_details",
            new Dictionary<string, object?>
            {
                ["database"] = database,
                ["objectName"] = objectName,
                ["definitionSearch"] = definitionSearch,
                ["matchOffset"] = nextOffset,
                ["maxMatches"] = 1,
            },
            cancellationToken: cancellationToken);
        var nextContent = Assert.NotNull(nextPage.StructuredContent);

        AssertToolSucceeded(nextPage, nextContent);
        Assert.Equal(1, nextContent.GetProperty("returnedDefinitionMatchCount").GetInt32());
    }

    private static void AssertToolSucceeded(CallToolResult result, JsonElement content)
    {
        var error = content.TryGetProperty("error", out var errorElement)
            ? errorElement.GetRawText()
            : "响应缺少 error 字段";
        Assert.False(result.IsError, error);
        Assert.True(content.GetProperty("success").GetBoolean(), error);
    }

    private static void AssertCompactText(CallToolResult result, string structuredPropertyName)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.DoesNotContain(structuredPropertyName, text, StringComparison.Ordinal);
    }

    private static string RequiredEnvironmentVariable(string name) =>
        OptionalEnvironmentVariable(name) ?? throw new InvalidOperationException($"缺少环境变量：{name}");

    private static string? OptionalEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool OptionalBoolean(string name) =>
        bool.TryParse(OptionalEnvironmentVariable(name), out var value) && value;
}
