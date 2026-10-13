using System.Collections.Concurrent;
using CoreFoundation;
using NativeLog = CoreFoundation.OSLog;
using Microsoft.Extensions.Logging;

namespace PenBridge.Mac.Services;

/// <summary>
/// <see cref="ILogger"/> → unified logging (<c>os_log</c>): subsystem <see cref="Subsystem"/>, one os_log category per
/// logger category (type). Read with <c>log stream --predicate 'subsystem == "com.blake11.penbridge"'</c> or Console.app.
/// </summary>
/// <remarks>
/// Uses the <see cref="NativeLog"/> binding (a direct native call, NativeAOT-safe). Messages are logged as public
/// strings, so callers must never pass secrets (PINs, tokens, keys); peer text goes through
/// <see cref="PenBridge.Core.Net.PeerText.Sanitize"/>. Sample-path logging is forbidden: nothing on the per-event input
/// path may log.
/// </remarks>
public sealed class OsLogLoggerProvider : ILoggerProvider
{
    /// <summary>The os_log subsystem (the bundle id).</summary>
    public const string Subsystem = "com.blake11.penbridge";

    private readonly ConcurrentDictionary<string, OsLogLogger> _loggers = new(StringComparer.Ordinal);
    private readonly LogLevel _minimumLevel;

    /// <param name="minimumLevel">Messages below this level are skipped before formatting.</param>
    public OsLogLoggerProvider(LogLevel minimumLevel = LogLevel.Information)
    {
        _minimumLevel = minimumLevel;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName ?? string.Empty, static (name, level) => new OsLogLogger(new NativeLog(Subsystem, name), level), _minimumLevel);

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (OsLogLogger logger in _loggers.Values)
        {
            logger.Dispose();
        }

        _loggers.Clear();
    }

    private sealed class OsLogLogger(NativeLog log, LogLevel minimumLevel) : ILogger, IDisposable
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            if (exception is not null)
            {
                // Type and message only: stack traces are noise in Console and exception messages carry no secrets.
                message = message + " [" + exception.GetType().Name + ": " + exception.Message + "]";
            }

            log.Log(Map(logLevel), message);
        }

        public void Dispose() => log.Dispose();

        private static OSLogLevel Map(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => OSLogLevel.Debug,
            // Info-level entries are kept only in memory unless configured; Default is persisted, so Information survives for "Open logs".
            LogLevel.Information or LogLevel.Warning => OSLogLevel.Default,
            LogLevel.Error => OSLogLevel.Error,
            _ => OSLogLevel.Fault,
        };
    }
}

/// <summary>A minimal <see cref="ILoggerFactory"/> over one <see cref="OsLogLoggerProvider"/> (no DI container needed).</summary>
public sealed class OsLogLoggerFactory : ILoggerFactory
{
    private readonly OsLogLoggerProvider _provider;

    public OsLogLoggerFactory(LogLevel minimumLevel = LogLevel.Information)
    {
        _provider = new OsLogLoggerProvider(minimumLevel);
    }

    /// <summary>Not supported: every logger goes to os_log.</summary>
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("OsLogLoggerFactory logs to os_log only.");

    public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);

    public void Dispose() => _provider.Dispose();
}
