using System.Diagnostics;
using SqlServerReadonlyMcp.Configuration;

namespace SqlServerReadonlyMcp.Sql;

public sealed class QueryConcurrencyGate : IDisposable
{
    internal const int MaximumQueueWaitSeconds = 30;

    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _maximumQueueWait;

    public QueryConcurrencyGate(McpSettings settings)
        : this(
            settings.Query.MaxConcurrentQueries,
            TimeSpan.FromSeconds(MaximumQueueWaitSeconds))
    {
    }

    internal QueryConcurrencyGate(int maximumConcurrency, TimeSpan maximumQueueWait)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrency);
        if (maximumQueueWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumQueueWait),
                "排队等待上限必须大于零。");
        }

        _semaphore = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        _maximumQueueWait = maximumQueueWait;
    }

    public async Task<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var entered = await _semaphore
            .WaitAsync(_maximumQueueWait, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();
        if (!entered)
        {
            var waitSeconds = Math.Max(1, (int)Math.Ceiling(_maximumQueueWait.TotalSeconds));
            throw new QueryQueueTimeoutException(
                $"数据库操作并发已满，等待 {waitSeconds} 秒后仍无可用执行槽；请稍后重试。",
                stopwatch.ElapsedMilliseconds);
        }

        return new Lease(_semaphore, stopwatch.ElapsedMilliseconds);
    }

    public void Dispose() => _semaphore.Dispose();

    public sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        internal Lease(SemaphoreSlim semaphore, long waitMilliseconds)
        {
            _semaphore = semaphore;
            WaitMilliseconds = waitMilliseconds;
        }

        public long WaitMilliseconds { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _semaphore.Release();
        }
    }
}

internal sealed class QueryQueueTimeoutException(string message, long waitMilliseconds)
    : TimeoutException(message)
{
    public long WaitMilliseconds { get; } = waitMilliseconds;
}
