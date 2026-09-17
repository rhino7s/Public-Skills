using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SqlServerReadonlyMcp.Tests;

public sealed class CapabilityProtocolTests
{
    [Theory(Timeout = 60_000)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true, "")]
    [InlineData(false, false, " \t")]
    public async Task DevelopmentServerRegistersCatalogAndAppliesGateBeforeEveryTool(bool ad, bool restricted, string? modeOverride = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "capability-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = Path.Combine(directory, "test.json");
            // Deliberately unreachable loopback port. Never use the installed MCP or real DB configuration.
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            {
                connection = new { authentication = ad ? "windowsIntegrated" : "sqlPassword", server = "tcp:127.0.0.1,1",
                    username = ad ? "" : "test", password = ad ? "" : "test-only", connectTimeoutSeconds = 1 },
                access = new { mode = modeOverride ?? (restricted ? "catalog" : "development") },
                capabilities = new { listFunction = "TestCatalog.dbo.list_capabilities", checkFunction = "TestCatalog.dbo.list_capabilities_check", pageSize = 100 },
                logging = new { directory = Path.Combine(directory, "logs") },
            }), cancellationToken);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "development-capability-tests", Command = "dotnet",
                Arguments = [typeof(Program).Assembly.Location, "--config", config],
                WorkingDirectory = directory,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            Assert.Equal(restricted ? 4 : 7, tools.Count);
            if (restricted) Assert.DoesNotContain(tools, t => t.Name is "find_object" or "get_object_details" or "find_object_references");
            var procedure = Assert.Single(tools, tool => tool.Name == "execute_procedure");
            Assert.Contains("“能力目录”中已授权", procedure.ProtocolTool.Description);
            Assert.True(procedure.ProtocolTool.Annotations?.DestructiveHint);
            var detailsTool = Assert.Single(tools, tool => tool.Name == "get_capability_details");
            Assert.Equal(["id"], detailsTool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Contains("has_desp=true", client.ServerInstructions);
            Assert.Contains("get_capability_details", client.ServerInstructions);
            var catalog = Assert.Single(tools, tool => tool.Name == "list_capabilities");
            Assert.Equal(["offset"], catalog.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray());
            Assert.DoesNotContain("TestCatalog", client.ServerInstructions);
            Assert.Equal(McpServerInstructions.Build(true, restricted), client.ServerInstructions);
            Assert.Contains("业务操作前读取完整“能力目录”摘要", client.ServerInstructions);
            Assert.DoesNotContain("execute_procedure", client.ServerInstructions);
            var sqlDescription = procedure.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("sql").GetProperty("description").GetString();
            Assert.DoesNotContain("catalog", sqlDescription);
            Assert.DoesNotContain("development", sqlDescription);
            if (restricted) Assert.Contains("database.schema.procedure 三段名", sqlDescription);
            else Assert.Contains("可省略数据库和 schema", sqlDescription);
            Assert.Contains("不得自动重试", procedure.ProtocolTool.Description);
            if (restricted)
            {
                foreach (var tool in tools)
                {
                    // Even deliberately invalid business arguments must never reach the underlying tool.
                    var result = await client.CallToolAsync(tool.Name,
                        new Dictionary<string, object?> { ["sql"] = "", ["database"] = "test", ["offset"] = -1 },
                        cancellationToken: cancellationToken);
                    Assert.True(result.IsError);
                    Assert.DoesNotContain("TestCatalog", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
                    Assert.Equal("access_check_unavailable", result.StructuredContent!.Value.GetProperty("code").GetString());
                    Assert.Contains("当前暂时无法访问，请确认网络连接后再试。", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
                }
                var audits = Directory.GetFiles(Path.Combine(directory,"logs"),"*.log")
                    .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                    .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "tool").ToArray();
                Assert.Equal(tools.Count,audits.Length);
                Assert.All(audits,row =>
                {
                    Assert.Equal(JsonValueKind.Number,row.GetProperty("authorization_ms").ValueKind);
                    Assert.Equal(JsonValueKind.Null,row.GetProperty("execution_ms").ValueKind);
                    Assert.Equal("access_check_unavailable",row.GetProperty("errorCategory").GetString());
                });
            }
            else
            {
                var query = await client.CallToolAsync("execute_sql", new Dictionary<string, object?> { ["sql"] = "", ["database"] = "test" }, cancellationToken: cancellationToken);
                Assert.Equal("safety_rejection", query.StructuredContent!.Value.GetProperty("error").GetProperty("category").GetString());
                foreach (var sql in new[] { "EXEC OtherDb.dbo.p;", "EXEC sys.sp_prepexec NULL, NULL, N'SELECT 1';", "EXEC sp_cursoropen NULL, N'SELECT 1';" })
                {
                    var rejected = await client.CallToolAsync("execute_procedure", new Dictionary<string, object?> { ["sql"] = sql, ["database"] = "test" }, cancellationToken: cancellationToken);
                    Assert.Equal("safety_rejection", rejected.StructuredContent!.Value.GetProperty("error").GetProperty("category").GetString());
                }
                var blocked = await client.CallToolAsync("execute_procedure", new Dictionary<string, object?> { ["sql"] = "EXEC dbo.sp_test;", ["database"] = "test" }, cancellationToken: cancellationToken);
                Assert.Equal("access_check_unavailable", blocked.StructuredContent!.Value.GetProperty("error").GetProperty("category").GetString());
                var details = await client.CallToolAsync("get_capability_details", new Dictionary<string, object?> { ["id"] = 1 }, cancellationToken: cancellationToken);
                Assert.Equal("capabilities_unavailable", details.StructuredContent!.Value.GetProperty("code").GetString());
                var page = await client.CallToolAsync("list_capabilities", new Dictionary<string, object?> { ["offset"] = -1 }, cancellationToken: cancellationToken);
                Assert.Equal("invalid_input", page.StructuredContent!.Value.GetProperty("code").GetString());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
