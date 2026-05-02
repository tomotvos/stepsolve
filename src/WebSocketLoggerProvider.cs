using Microsoft.Extensions.Logging;

namespace StepSolve;

/// <summary>
/// Logger provider that forwards log entries to the WebSocket broadcaster.
/// Bridges the .NET logging pipeline to real-time dashboard log streaming.
/// </summary>
public sealed class WebSocketLoggerProvider : ILoggerProvider
{
    private readonly WebSocketBroadcaster _broadcaster;

    public WebSocketLoggerProvider(WebSocketBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    public ILogger CreateLogger(string categoryName) => new WebSocketLogger(_broadcaster, categoryName);
    public void Dispose() { }

    private sealed class WebSocketLogger : ILogger
    {
        private readonly WebSocketBroadcaster _broadcaster;
        private readonly string _category;      // short name for log messages
        private readonly string _fullCategory;  // original name for level filtering

        public WebSocketLogger(WebSocketBroadcaster broadcaster, string category)
        {
            _broadcaster = broadcaster;
            _fullCategory = category;
            // Use short category name (last segment) for readability in the log pane
            var dot = category.LastIndexOf('.');
            _category = dot >= 0 ? category[(dot + 1)..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            // Pass Debug+ for StepSolve categories so the live log pane shows
            // application activity. Use Warning+ for framework categories to
            // avoid flooding with ASP.NET / Kestrel internals.
            if (_fullCategory.StartsWith("Microsoft") || _fullCategory.StartsWith("System"))
                return logLevel >= LogLevel.Warning;
            return logLevel >= LogLevel.Debug;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var level = logLevel switch
            {
                LogLevel.Warning => "WARNING",
                LogLevel.Error or LogLevel.Critical => "ERROR",
                LogLevel.Debug or LogLevel.Trace => "DEBUG",
                _ => "INFO",
            };

            var message = $"[{_category}] {formatter(state, exception)}";
            if (exception != null)
                message += $" | {exception.GetType().Name}: {exception.Message}";

            // Fire-and-forget — don't block the logging call
            _ = _broadcaster.BroadcastLog(level, message);
        }
    }
}
