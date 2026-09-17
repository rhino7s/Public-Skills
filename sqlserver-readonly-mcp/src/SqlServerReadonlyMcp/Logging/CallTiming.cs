using System.Diagnostics;

namespace SqlServerReadonlyMcp.Logging;

/// <summary>每次调用独立；仅输出计时，不记录授权集合或说明正文。</summary>
public sealed class CallTiming : IDisposable
{
    public const int PreflightSeconds = 15;
    private static readonly AsyncLocal<CallTiming?> Ambient = new();
    private readonly CallTiming? _previous;
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, double> _elapsed = [];
    private string? _active;
    private long _phaseStart;
    public static CallTiming? Current => Ambient.Value;
    public CancellationTokenSource Preflight { get; }
    public CancellationToken CallerToken { get; }
    public bool Audited { get; set; }
    public string RequestId { get; } = Guid.NewGuid().ToString("N");

    public CallTiming(CancellationToken token)
    {
        CallerToken = token;
        _previous = Ambient.Value;
        Ambient.Value = this;
        Preflight = CancellationTokenSource.CreateLinkedTokenSource(token);
        Preflight.CancelAfter(TimeSpan.FromSeconds(PreflightSeconds));
    }

    public IDisposable Measure(string phase)
    {
        if (_active is not null) throw new InvalidOperationException("Timing phases must not overlap.");
        _active = phase;
        _phaseStart = Stopwatch.GetTimestamp();
        return new Phase(this);
    }

    public Dictionary<string, object?> Snapshot()
    {
        var result = new Dictionary<string, object?>();
        foreach (var phase in new[] { "parse", "authorization", "metadata", "execution" })
        {
            var exists = _elapsed.TryGetValue(phase, out var value);
            if (_active == phase) { value += Stopwatch.GetElapsedTime(_phaseStart).TotalMilliseconds; exists = true; }
            result[phase + "_ms"] = exists ? Math.Round(value, 3) : null;
        }
        result["total_ms"] = Math.Round(Stopwatch.GetElapsedTime(_start).TotalMilliseconds, 3);
        return result;
    }

    public void Dispose() { Preflight.Dispose(); Ambient.Value = _previous; }
    private sealed class Phase(CallTiming owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var phase = owner._active!;
            owner._elapsed[phase] = owner._elapsed.GetValueOrDefault(phase) + Stopwatch.GetElapsedTime(owner._phaseStart).TotalMilliseconds;
            owner._active = null;
        }
    }
}
