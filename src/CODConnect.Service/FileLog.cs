using Microsoft.Extensions.Logging;

namespace CODConnect.Service;

public static class FileLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CODCONNECT", "service.log");

    public static void Write(string category, string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    File.Copy(Path, Path + ".1", overwrite: true);
                    File.WriteAllText(Path, string.Empty);
                }

                File.AppendAllText(Path, line);
            }
            catch
            {
            }
        }
    }

    public static void HookProcessFailures()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("process", "FATAL", $"unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("process", "WARN", $"unobserved task exception: {e.Exception}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("process", "INFO", "process exit");
    }
}

public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            FileLog.Write(category, logLevel.ToString().ToUpperInvariant(), message);
        }
    }
}
