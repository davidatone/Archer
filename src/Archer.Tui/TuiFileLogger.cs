using Microsoft.Extensions.Logging;

namespace Archer.Tui;

/// <summary>
/// Trivial file logger so the TUI never writes diagnostic output to stdout/stderr — those
/// streams are owned by Terminal.Gui and any stray console write corrupts the rendered UI.
/// </summary>
internal sealed class TuiFileLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _writer;
    private readonly object _lock = new();

    public TuiFileLoggerProvider(TextWriter writer) => _writer = writer;

    public ILogger CreateLogger(string categoryName) => new TuiFileLogger(this, categoryName);

    public void Dispose() => (_writer as IDisposable)?.Dispose();

    private sealed class TuiFileLogger : ILogger
    {
        private readonly TuiFileLoggerProvider _provider;
        private readonly string _category;

        public TuiFileLogger(TuiFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {Letter(logLevel)} {_category}: {formatter(state, exception)}";
            lock (_provider._lock)
            {
                _provider._writer.WriteLine(line);
                if (exception is not null) _provider._writer.WriteLine(exception);
            }
        }

        private static char Letter(LogLevel l) => l switch
        {
            LogLevel.Trace => 'T',
            LogLevel.Debug => 'D',
            LogLevel.Information => 'I',
            LogLevel.Warning => 'W',
            LogLevel.Error => 'E',
            LogLevel.Critical => 'C',
            _ => '?',
        };
    }
}
