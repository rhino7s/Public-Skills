using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SqlServerReadonlyMcp.Tests;

public sealed class CapabilityProtocolTests
{
    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DevelopmentServerRegistersCatalogAndAppliesGateBeforeEveryTool(bool ad)
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
            Assert.Equal(6, tools.Count);
            var catalog = Assert.Single(tools, tool => tool.Name == "list_capabilities");
            Assert.Equal(["offset"], catalog.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray());
            Assert.DoesNotContain("TestCatalog", client.ServerInstructions);
            if (ad)
            {
                foreach (var tool in tools)
                {
                    // Even deliberately invalid business arguments must never reach the underlying tool.
                    var result = await client.CallToolAsync(tool.Name,
                        new Dictionary<string, object?> { ["sql"] = "", ["database"] = "test", ["offset"] = -1 },
                        cancellationToken: cancellationToken);
                    Assert.True(result.IsError);
                    Assert.Equal("用户没有访问权限", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
                    Assert.Equal("access_denied", result.StructuredContent!.Value.GetProperty("code").GetString());
                }
            }
            else
            {
                var query = await client.CallToolAsync("execute_sql", new Dictionary<string, object?> { ["sql"] = "", ["database"] = "test" }, cancellationToken: cancellationToken);
                Assert.Equal("safety_rejection", query.StructuredContent!.Value.GetProperty("error").GetProperty("category").GetString());
                var page = await client.CallToolAsync("list_capabilities", new Dictionary<string, object?> { ["offset"] = -1 }, cancellationToken: cancellationToken);
                Assert.Equal("invalid_input", page.StructuredContent!.Value.GetProperty("code").GetString());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
