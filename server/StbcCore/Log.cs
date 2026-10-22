using System;

namespace StbcServer;

public enum LogLevel { Debug, Info, Warn, Error }

public static class Log
{
    public static LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>Optional sink for tests; defaults to stdout.</summary>
    public static Action<string>? Sink { get; set; }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        if (level < Level) return;
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {level.ToString().ToUpperInvariant(),-5} {message}";
        if (Sink != null) Sink(line);
        else Console.WriteLine(line);
    }
}
