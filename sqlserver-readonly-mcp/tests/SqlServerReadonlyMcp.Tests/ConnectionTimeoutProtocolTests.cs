using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SqlServerReadonlyMcp.Tests;

public sealed class ConnectionTimeoutProtocolTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SilentPreloginPeerReturnsConnectionFailureWithinBudget(bool catalog)
    {
        var token = TestContext.Current.CancellationToken;
        var directory=Path.Combine(Path.GetTempPath(),"mcp-silent-peer-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer=HoldConnectionAsync();
        try
        {
            var config=Path.Combine(directory,"config.json");
            await File.WriteAllTextAsync(config,JsonSerializer.Serialize(new
            {
                connection=new { authentication="sqlPassword",server=$"tcp:127.0.0.1,{((IPEndPoint)listener.LocalEndpoint).Port}",username="test",password="test-only",connectTimeoutSeconds=5 },
                access=new { mode=catalog ? "catalog" : "development" },
                capabilities=new { listFunction=catalog ? "D.dbo.list" : "",checkFunction=catalog ? "D.dbo.check" : "" },
                query=new { timeoutSeconds=60 },
                logging=new { directory=Path.Combine(directory,"logs") }
            }),token);
            var transport=new StdioClientTransport(new StdioClientTransportOptions
            {
                Name="project-silent-peer-test", Command="dotnet",
                Arguments=[typeof(Program).Assembly.Location,"--config",config],WorkingDirectory=directory
            });
            await using var client=await McpClient.CreateAsync(transport,cancellationToken:token);
            var watch=Stopwatch.StartNew();
            var result=await client.CallToolAsync("execute_sql",new Dictionary<string,object?> { ["database"]="D",["sql"]="SELECT 1" },cancellationToken:token);
            Assert.True(result.IsError);
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(1),token);
            Assert.Contains("当前暂时无法访问，请确认网络连接后再试。",Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12),$"Connection failure took {watch.Elapsed.TotalSeconds:F2}s");
            var body=Assert.NotNull(result.StructuredContent);
            if(catalog) Assert.Equal("access_check_unavailable",body.GetProperty("code").GetString());
            else Assert.Equal(0,body.GetProperty("returnedRows").GetInt32());
            TestContext.Current.TestOutputHelper!.WriteLine($"catalog={catalog}, elapsed_ms={watch.ElapsedMilliseconds}");
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            await peer;
            Directory.Delete(directory,true);
        }
        async Task HoldConnectionAsync()
        {
            try
            {
                using var socket=await listener.AcceptTcpClientAsync(stop.Token);
                accepted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan,stop.Token);
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
            catch(SocketException) when(stop.IsCancellationRequested) { }
        }
    }
}
