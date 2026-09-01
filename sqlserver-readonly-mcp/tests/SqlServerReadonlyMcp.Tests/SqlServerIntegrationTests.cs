using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SqlServerReadonlyMcp.Tests;

public sealed class SqlServerIntegrationTests
{
    private const string ConfigVariable = "SQLSERVER_MCP_INTEGRATION_CONFIG";
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

    private static StdioClientTransport CreateTransport(string configPath, string name) => new(
        new StdioClientTransportOptions
        {
            Name = name,
            Command = "dotnet",
            Arguments = [typeof(SqlServerReadonlyMcp.Program).Assembly.Location, "--config", configPath],
            WorkingDirectory = Path.GetDirectoryName(configPath),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });

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
