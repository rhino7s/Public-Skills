using Microsoft.Extensions.Logging;

namespace SqlServerReadonlyMcp.Logging;

public sealed class DailyJsonLoggerProvider : ILoggerProvider
{
    private readonly DailyLogWriter _writer;
    private readonly LogLevel _minimumLevel;

    public DailyJsonLoggerProvider(DailyLogWriter writer, LogLevel minimumLevel)
    {
        _writer = writer;
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) =>
        new DailyJsonLogger(_writer, categoryName, _minimumLevel);

    public void Dispose()
    {
    }

    private sealed class DailyJsonLogger : ILogger
    {
        private readonly DailyLogWriter _writer;
        private readonly string _category;
        private readonly LogLevel _minimumLevel;

        public DailyJsonLogger(DailyLogWriter writer, string category, LogLevel minimumLevel)
        {
            _writer = writer;
            _category = category;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                var exceptionText = exception?.ToString();
                _writer.Write(new Dictionary<string, object?>
                {
                    ["level"] = logLevel.ToString(),
                    ["eventType"] = "application",
                    ["category"] = _category,
                    ["eventId"] = eventId.Id,
                    ["message"] = Limit(formatter(state, exception), 2_048),
                    ["exception"] = Limit(exceptionText, 2_048),
                });
            }
            catch
            {
                // Logging must never corrupt stdout or fail an MCP request.
            }
        }

        private static string? Limit(string? value, int maximumLength) =>
            value is null || value.Length <= maximumLength
                ? value
                : value[..maximumLength];
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
