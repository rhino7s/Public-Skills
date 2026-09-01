using System.Text.Json;
using SqlServerReadonlyMcp.Logging;

namespace SqlServerReadonlyMcp.Tests;

public sealed class DailyLogWriterTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "sqlserver-readonly-mcp-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void WritesOneJsonLineToDateNamedFile()
    {
        using var writer = new DailyLogWriter(_temporaryDirectory, 20);
        writer.Initialize();

        writer.Write(new Dictionary<string, object?>
        {
            ["eventType"] = "query",
            ["requestId"] = "request-1",
        });

        var expectedPath = Path.Combine(
            _temporaryDirectory,
            $"sqlserver-mcp-{DateTime.Now:yyyy-MM-dd}.log");
        Assert.True(File.Exists(expectedPath));
        var lines = File.ReadAllLines(expectedPath);
        var line = Assert.Single(lines);
        using var document = JsonDocument.Parse(line);
        Assert.Equal("query", document.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("request-1", document.RootElement.GetProperty("requestId").GetString());
        Assert.True(document.RootElement.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public void DeletesOnlyExpiredFilesMatchingExactLogName()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var expiredDate = DateTime.Today.AddDays(-20);
        var expired = Path.Combine(_temporaryDirectory, $"sqlserver-mcp-{expiredDate:yyyy-MM-dd}.log");
        var retainedDate = DateTime.Today.AddDays(-19);
        var retained = Path.Combine(_temporaryDirectory, $"sqlserver-mcp-{retainedDate:yyyy-MM-dd}.log");
        var unrelated = Path.Combine(_temporaryDirectory, $"prefix-sqlserver-mcp-{expiredDate:yyyy-MM-dd}.log");
        File.WriteAllText(expired, "old");
        File.WriteAllText(retained, "keep");
        File.WriteAllText(unrelated, "unrelated");

        using var writer = new DailyLogWriter(_temporaryDirectory, 20);
        writer.Initialize();

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(retained));
        Assert.True(File.Exists(unrelated));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
