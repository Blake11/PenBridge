using Microsoft.Extensions.Logging;

namespace PenBridge.SimClient;

/// <summary>A minimal stderr logger (the SimClient takes no logging-provider dependency).</summary>
internal sealed class ConsoleLoggerFactory(LogLevel minimum) : ILoggerFactory
{
    private static readonly Lock WriteLock = new();

    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, minimum);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class ConsoleLogger(string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string line = $"{DateTime.Now:HH:mm:ss.fff} {Short(logLevel)} {category}: {formatter(state, exception)}";
            lock (WriteLock)
            {
                Console.Error.WriteLine(line);
                if (exception is not null)
                {
                    Console.Error.WriteLine(exception.Message);
                }
            }
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            _ => "crit",
        };
    }
}
