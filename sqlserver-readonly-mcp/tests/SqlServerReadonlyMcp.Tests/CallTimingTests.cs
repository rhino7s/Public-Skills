using System.Text.Json;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Logging;

namespace SqlServerReadonlyMcp.Tests;

public sealed class CallTimingTests
{
    [Fact]
    public async Task PhasesAccumulateWithoutOverlapAndRestoreAmbientScope()
    {
        Assert.Null(CallTiming.Current);
        using (var timing = new CallTiming(TestContext.Current.CancellationToken))
        {
            using (timing.Measure("authorization"))
            {
                Assert.Throws<InvalidOperationException>(() => timing.Measure("parse"));
                await Task.Yield();
                Assert.Same(timing, CallTiming.Current);
            }
            using (timing.Measure("authorization")) { }
            using (timing.Measure("parse")) { }
            var values = timing.Snapshot();
            Assert.IsType<double>(values["authorization_ms"]);
            Assert.IsType<double>(values["parse_ms"]);
            Assert.Null(values["metadata_ms"]);
            Assert.Null(values["execution_ms"]);
            Assert.True((double)values["total_ms"]! + .01 >= (double)values["authorization_ms"]! + (double)values["parse_ms"]!);
        }
        Assert.Null(CallTiming.Current);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"Logging\":{}}", false)]
    [InlineData("{\"Logging\":{\"IncludeSqlText\":false}}", false)]
    [InlineData("{\"Logging\":{\"IncludeSqlText\":true}}", true)]
    public void OrdinaryAuditContainsTimingAndNoSqlUnlessEnabled(string configuration, bool includeSql)
    {
        var path = Path.Combine(Path.GetTempPath(), "mcp-timing-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new DailyLogWriter(path, 1))
            using (var timing = new CallTiming(TestContext.Current.CancellationToken))
            {
                writer.Initialize();
                var audit = new AuditLogger(writer, JsonSerializer.Deserialize<McpSettings>(configuration)!);
                using var phase = timing.Measure("parse");
                audit.WriteQuery(new(timing.RequestId,"execute_sql","D","SELECT 'private'",0,1,0,0,0,false,null,"error",ErrorCategory:"safety_rejection"));
                Assert.True(timing.Audited);
            }
            var line = Assert.Single(Directory.GetFiles(path).SelectMany(File.ReadAllLines));
            using var json = JsonDocument.Parse(line);
            var row = json.RootElement;
            Assert.Equal(JsonValueKind.Number,row.GetProperty("parse_ms").ValueKind);
            Assert.Equal(JsonValueKind.Null,row.GetProperty("execution_ms").ValueKind);
            if (includeSql)
                Assert.Equal("SELECT 'private'", row.GetProperty("sql").GetString());
            else
            {
                Assert.Equal(JsonValueKind.Null,row.GetProperty("sql").ValueKind);
                Assert.DoesNotContain("private",line);
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path,true); }
    }
}
