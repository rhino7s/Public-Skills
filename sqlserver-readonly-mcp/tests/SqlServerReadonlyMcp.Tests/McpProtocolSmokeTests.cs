using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class McpProtocolSmokeTests : IDisposable
{
    private const string LatestProtocolVersion = "2026-07-28";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "sqlserver-readonly-mcp-protocol-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ToolSerializerPreservesNullPropertiesRequiredByOutputSchema()
    {
        var result = new ObjectSearchResult(
            true,
            "request-id",
            Array.Empty<ObjectSearchItem>(),
            false,
            null,
            null);

        var content = JsonSerializer.SerializeToElement(
            result,
            SqlServerReadonlyMcp.Program.CreateToolJsonOptions());

        Assert.True(content.TryGetProperty("error", out var error));
        Assert.Equal(JsonValueKind.Null, error.ValueKind);
    }

    [Fact(Timeout = 30_000)]
    public async Task NegotiatesLatestProtocolAndPublishesFiveTools()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_temporaryDirectory);
        var configPath = Path.Combine(_temporaryDirectory, "appsettings.local.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            connection = new
            {
                server = "invalid.example.local",
                username = "readonly_test",
                password = "not-a-real-secret",
            },
            logging = new
            {
                directory = Path.Combine(_temporaryDirectory, "logs"),
            },
        }), cancellationToken);

        var serverAssembly = typeof(SqlServerReadonlyMcp.Program).Assembly.Location;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sqlserver-readonly-mcp-smoke",
            Command = "dotnet",
            Arguments = [serverAssembly, "--config", configPath],
            WorkingDirectory = _temporaryDirectory,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });

        await using var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ProtocolVersion = LatestProtocolVersion },
            cancellationToken: cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

        Assert.Equal(LatestProtocolVersion, client.NegotiatedProtocolVersion);
        Assert.Equal(McpServerInstructions.Text, client.ServerInstructions);
        Assert.Equal(
            ["execute_procedure", "execute_sql", "find_object", "find_object_references", "get_object_details"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        var procedureTool = Assert.Single(tools, tool => tool.Name == "execute_procedure");
        Assert.False(procedureTool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(procedureTool.ProtocolTool.Annotations?.DestructiveHint);
        Assert.Contains("必须与 database 参数一致", procedureTool.ProtocolTool.Description);

        var executeSqlTool = Assert.Single(tools, tool => tool.Name == "execute_sql");
        Assert.Contains("NEXT VALUE FOR", executeSqlTool.ProtocolTool.Description);
        Assert.Contains("OPENROWSET", executeSqlTool.ProtocolTool.Description);

        var detailsTool = Assert.Single(tools, tool => tool.Name == "get_object_details");
        var detailsSchema = Assert.NotNull(detailsTool.ProtocolTool.OutputSchema);
        var detailsProperties = detailsSchema.GetProperty("properties");
        Assert.True(detailsProperties.TryGetProperty("canExecute", out _));
        Assert.True(detailsProperties.TryGetProperty("permissions", out _));
        Assert.True(detailsProperties.TryGetProperty("definitionMatches", out _));
        Assert.True(detailsProperties.TryGetProperty("definitionHasMore", out _));
        Assert.True(detailsProperties.TryGetProperty("matchesHasMore", out _));
        Assert.True(detailsProperties.TryGetProperty("nextMatchOffset", out _));
        Assert.True(detailsProperties.TryGetProperty("truncationReason", out _));
        Assert.False(detailsProperties.TryGetProperty("definitionMatchesTruncated", out _));
        Assert.True(detailsProperties.TryGetProperty("guidance", out _));
        Assert.True(detailsProperties.TryGetProperty("columns", out _));
        Assert.True(detailsProperties.TryGetProperty("indexes", out _));
        Assert.False(detailsProperties.TryGetProperty("dependencies", out _));
        var detailInputs = detailsTool.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Equal(200, detailInputs.GetProperty("maxLines").GetProperty("default").GetInt32());
        Assert.Equal(0, detailInputs.GetProperty("matchOffset").GetProperty("default").GetInt32());
        Assert.False(detailInputs.TryGetProperty("contextLines", out _));
        Assert.Equal(20, detailInputs.GetProperty("maxMatches").GetProperty("default").GetInt32());

        var findTool = Assert.Single(tools, tool => tool.Name == "find_object");
        var requiredFindArguments = findTool.ProtocolTool.InputSchema
            .GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains("database", requiredFindArguments);
        Assert.True(findTool.ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("exactMatch")
            .GetProperty("default")
            .GetBoolean());

        var referenceTool = Assert.Single(tools, tool => tool.Name == "find_object_references");
        Assert.Equal("查找 SQL Server 对象文本引用候选", referenceTool.ProtocolTool.Title);
        Assert.Contains("不得直接称为实际调用方", referenceTool.ProtocolTool.Description);
        Assert.Contains("zold", referenceTool.ProtocolTool.Description, StringComparison.OrdinalIgnoreCase);
        var referenceSchema = Assert.NotNull(referenceTool.ProtocolTool.OutputSchema);
        var referenceProperties = referenceSchema.GetProperty("properties");
        Assert.True(referenceProperties.TryGetProperty("referencesTruncationReason", out _));
        var referenceInputs = referenceTool.ProtocolTool.InputSchema.GetProperty("properties");
        var requiredReferenceArguments = referenceTool.ProtocolTool.InputSchema
            .GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains("targetDatabase", requiredReferenceArguments);
        Assert.Contains("targetObject", requiredReferenceArguments);
        Assert.Contains("searchDatabase", requiredReferenceArguments);
        Assert.Contains(
            "可与 searchDatabase 不同",
            referenceInputs.GetProperty("targetDatabase").GetProperty("description").GetString());
        Assert.Contains(
            "目标名至少 4 字符时也匹配裸对象名",
            referenceInputs.GetProperty("searchDatabase").GetProperty("description").GetString());
        Assert.Contains(
            "不同时只匹配明确的 database.schema.object 三段名",
            referenceInputs.GetProperty("searchDatabase").GetProperty("description").GetString());
        Assert.Contains(
            "不受 offset/limit 控制",
            referenceInputs.GetProperty("includeJobs").GetProperty("description").GetString());
        Assert.Contains(
            "referencesTruncationReason=max_offset",
            referenceInputs.GetProperty("offset").GetProperty("description").GetString());
        Assert.False(referenceInputs.GetProperty("includeJobs").GetProperty("default").GetBoolean());
        Assert.Equal(20, referenceInputs.GetProperty("limit").GetProperty("default").GetInt32());

        Assert.All(
            tools.Where(tool => tool.Name != "find_object_references"),
            tool => Assert.Contains(
                "database",
                tool.ProtocolTool.InputSchema
                    .GetProperty("required")
                    .EnumerateArray()
                    .Select(item => item.GetString())));

        Assert.All(
            tools.Where(tool => tool.Name != "execute_procedure"),
            tool => Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.All(
            tools.Where(tool => tool.Name != "execute_procedure"),
            tool => Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint));

        var rejectedQuery = await client.CallToolAsync(
            "execute_sql",
            new Dictionary<string, object?>
            {
                ["sql"] = string.Empty,
                ["database"] = "ExampleDatabase",
            },
            cancellationToken: cancellationToken);
        var rejectedQueryContent = Assert.NotNull(rejectedQuery.StructuredContent);
        AssertRequiredPropertiesPresent(
            Assert.NotNull(Assert.Single(tools, tool => tool.Name == "execute_sql").ProtocolTool.OutputSchema),
            rejectedQueryContent);
        Assert.Equal(JsonValueKind.Null, rejectedQueryContent.GetProperty("truncationReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, rejectedQueryContent.GetProperty("guidance").ValueKind);
        Assert.Equal("safety_rejection", rejectedQueryContent.GetProperty("error").GetProperty("category").GetString());
        Assert.True(rejectedQuery.IsError);
        var rejectedQueryText = Assert.IsType<TextContentBlock>(Assert.Single(rejectedQuery.Content)).Text;
        Assert.Contains("查询失败", rejectedQueryText, StringComparison.Ordinal);
        Assert.DoesNotContain("resultSets", rejectedQueryText, StringComparison.Ordinal);

        var rejectedPersistentWrite = await client.CallToolAsync(
            "execute_sql",
            new Dictionary<string, object?>
            {
                ["sql"] = "INSERT INTO dbo.ExampleTable (ExampleColumn) VALUES (1);",
                ["database"] = "ExampleDatabase",
            },
            cancellationToken: cancellationToken);
        var rejectedPersistentWriteContent = Assert.NotNull(rejectedPersistentWrite.StructuredContent);
        Assert.Equal(
            "safety_rejection",
            rejectedPersistentWriteContent.GetProperty("error").GetProperty("category").GetString());
        Assert.Contains(
            "持久化",
            rejectedPersistentWriteContent.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);

        var rejectedInsertExecute = await client.CallToolAsync(
            "execute_sql",
            new Dictionary<string, object?>
            {
                ["sql"] = "CREATE TABLE #result (id int); INSERT INTO #result EXEC dbo.DangerousProcedure;",
                ["database"] = "ExampleDatabase",
            },
            cancellationToken: cancellationToken);
        var rejectedInsertExecuteContent = Assert.NotNull(rejectedInsertExecute.StructuredContent);
        Assert.Equal(
            "safety_rejection",
            rejectedInsertExecuteContent.GetProperty("error").GetProperty("category").GetString());
        Assert.Contains(
            "EXEC/EXECUTE",
            rejectedInsertExecuteContent.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);

        var rejectedNestedDml = await client.CallToolAsync(
            "execute_sql",
            new Dictionary<string, object?>
            {
                ["sql"] = "CREATE TABLE #result (id int); INSERT INTO #result(id) SELECT id FROM (DELETE FROM dbo.PersistentTable OUTPUT DELETED.id) AS deleted_rows;",
                ["database"] = "ExampleDatabase",
            },
            cancellationToken: cancellationToken);
        var rejectedNestedDmlContent = Assert.NotNull(rejectedNestedDml.StructuredContent);
        Assert.Equal(
            "safety_rejection",
            rejectedNestedDmlContent.GetProperty("error").GetProperty("category").GetString());
        Assert.Contains(
            "嵌套",
            rejectedNestedDmlContent.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);

        var rejectedExternalSource = await client.CallToolAsync(
            "execute_sql",
            new Dictionary<string, object?>
            {
                ["sql"] = "SELECT * FROM [LinkedServer].[Database].[dbo].[TableName];",
                ["database"] = "ExampleDatabase",
            },
            cancellationToken: cancellationToken);
        var rejectedExternalSourceContent = Assert.NotNull(rejectedExternalSource.StructuredContent);
        Assert.Equal(
            "safety_rejection",
            rejectedExternalSourceContent.GetProperty("error").GetProperty("category").GetString());
        Assert.Contains(
            "远程",
            rejectedExternalSourceContent.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);

        var rejectedSequence = await client.CallToolAsync(
            "execute_sql",
            new Dictionary<string, object?>
            {
                ["sql"] = "SELECT NEXT VALUE FOR dbo.OrderSequence;",
                ["database"] = "ExampleDatabase",
            },
            cancellationToken: cancellationToken);
        var rejectedSequenceContent = Assert.NotNull(rejectedSequence.StructuredContent);
        Assert.Equal(
            "safety_rejection",
            rejectedSequenceContent.GetProperty("error").GetProperty("category").GetString());
        Assert.Contains(
            "序列",
            rejectedSequenceContent.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);

        var rejectedProcedureDatabase = await client.CallToolAsync(
            "execute_procedure",
            new Dictionary<string, object?>
            {
                ["sql"] = "EXEC OtherDatabase.dbo.ExampleProcedure;",
                ["database"] = "ExampleDatabase",
            },
            cancellationToken: cancellationToken);
        var rejectedProcedureDatabaseContent = Assert.NotNull(rejectedProcedureDatabase.StructuredContent);
        Assert.Equal(
            "safety_rejection",
            rejectedProcedureDatabaseContent.GetProperty("error").GetProperty("category").GetString());
        Assert.Contains(
            "必须与 database 参数一致",
            rejectedProcedureDatabaseContent.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    private static void AssertRequiredPropertiesPresent(JsonElement schema, JsonElement content)
    {
        foreach (var propertyName in schema
                     .GetProperty("required")
                     .EnumerateArray()
                     .Select(item => Assert.IsType<string>(item.GetString())))
        {
            Assert.True(
                content.TryGetProperty(propertyName, out _),
                $"Structured content is missing required property '{propertyName}'.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
