using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class QueryConcurrencyGateTests
{
    [Fact]
    public async Task RejectsQueuedOperationAfterWaitLimit()
    {
        using var gate = new QueryConcurrencyGate(1, TimeSpan.FromMilliseconds(25));
        using var firstLease = await gate.EnterAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<QueryQueueTimeoutException>(
            () => gate.EnterAsync(TestContext.Current.CancellationToken));

        Assert.Contains("并发已满", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.WaitMilliseconds > 0);
    }

    [Fact]
    public async Task CancellationTakesPrecedenceOverQueueTimeout()
    {
        using var gate = new QueryConcurrencyGate(1, TimeSpan.FromSeconds(1));
        using var firstLease = await gate.EnterAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.EnterAsync(cancellation.Token));
    }
}
