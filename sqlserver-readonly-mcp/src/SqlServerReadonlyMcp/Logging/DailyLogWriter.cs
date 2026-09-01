using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlServerReadonlyMcp.Logging;

public sealed partial class DailyLogWriter : IDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly Mutex _mutex;
    private DateOnly _lastCleanupDate;
    private bool _disposed;

    public DailyLogWriter(string directory, int retentionDays)
    {
        _directory = Path.GetFullPath(
            Path.IsPathRooted(directory)
                ? directory
                : Path.Combine(AppContext.BaseDirectory, directory));
        _retentionDays = retentionDays;
        _mutex = new Mutex(false, CreateMutexName(_directory));
    }

    public string DirectoryPath => _directory;

    public void Initialize()
    {
        Directory.CreateDirectory(_directory);
        ExecuteLocked(() => CleanupExpiredFiles(DateOnly.FromDateTime(DateTime.Now)));
    }

    public void Write(IReadOnlyDictionary<string, object?> entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = DateTimeOffset.Now;
        var payload = new Dictionary<string, object?>(entry, StringComparer.Ordinal)
        {
            ["timestamp"] = now.ToString("O", CultureInfo.InvariantCulture),
        };
        var line = JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine;

        ExecuteLocked(() =>
        {
            var today = DateOnly.FromDateTime(now.LocalDateTime);
            if (_lastCleanupDate != today)
            {
                CleanupExpiredFiles(today);
            }

            var path = Path.Combine(_directory, $"sqlserver-mcp-{today:yyyy-MM-dd}.log");
            File.AppendAllText(path, line, Utf8WithoutBom);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutex.Dispose();
    }

    private void CleanupExpiredFiles(DateOnly today)
    {
        var firstDateToKeep = today.AddDays(-(_retentionDays - 1));

        foreach (var path in Directory.EnumerateFiles(_directory, "sqlserver-mcp-????-??-??.log"))
        {
            var fileName = Path.GetFileName(path);
            var match = DailyLogFileRegex().Match(fileName);
            if (!match.Success ||
                !DateOnly.TryParseExact(
                    match.Groups[1].Value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var fileDate) ||
                fileDate >= firstDateToKeep)
            {
                continue;
            }

            File.Delete(path);
        }

        _lastCleanupDate = today;
    }

    private void ExecuteLocked(Action action)
    {
        var lockTaken = false;
        try
        {
            lockTaken = _mutex.WaitOne(TimeSpan.FromSeconds(10));
            if (!lockTaken)
            {
                throw new IOException("等待日志文件锁超时。");
            }

            action();
        }
        catch (AbandonedMutexException)
        {
            lockTaken = true;
            action();
        }
        finally
        {
            if (lockTaken)
            {
                _mutex.ReleaseMutex();
            }
        }
    }

    private static string CreateMutexName(string directory)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()));
        return $"Local\\SqlServerReadonlyMcpLog_{Convert.ToHexString(bytes.AsSpan(0, 12))}";
    }

    [GeneratedRegex(@"^sqlserver-mcp-(\d{4}-\d{2}-\d{2})\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex DailyLogFileRegex();
}
