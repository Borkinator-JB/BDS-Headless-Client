using Microsoft.Extensions.Logging;

namespace Bds.Android;

/// <summary>Logcat plus the last lines in memory, shown in the app.</summary>
public static class AppLog
{
    const int MaxLines = 200;
    static readonly LinkedList<string> Lines = new();
    static readonly Lock Gate = new();

    public static void Add(string line)
    {
        global::Android.Util.Log.Info("BdsHeadless", line);
        lock (Gate)
        {
            Lines.AddLast($"{DateTime.Now:HH:mm:ss} {line}");
            while (Lines.Count > MaxLines) Lines.RemoveFirst();
        }
    }

    public static string Text()
    {
        lock (Gate) return Lines.Count == 0 ? "No log entries." : string.Join('\n', Lines.Reverse());
    }
}

public sealed class AppLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new AppLogger(
        categoryName[(categoryName.LastIndexOf('.') + 1)..],
        categoryName.StartsWith("Microsoft.", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);
    public void Dispose() { }

    sealed class AppLogger(string category, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{logLevel.ToString()[..4]} {category}: {formatter(state, exception)}";
            if (exception is not null) line += $" ({exception.GetType().Name}: {exception.Message})";
            AppLog.Add(line);
        }
    }
}
