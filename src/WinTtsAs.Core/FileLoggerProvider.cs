using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel is >= LogLevel.Information and not LogLevel.None;

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

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_category}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _provider.Write(line);
        }
    }
}
