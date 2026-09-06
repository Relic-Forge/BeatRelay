using System;
using System.IO;
using System.Text.Json;

namespace BeatRelay.Diagnostics;

public sealed class OverlayLogger : IOverlayLogger
{
    private readonly object syncRoot = new();

    public OverlayLogger(string logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("Log directory is required.", nameof(logDirectory));
        }

        LogDirectory = logDirectory;
        LogPath = Path.Combine(logDirectory, "BeatRelay.jsonl");
    }

    public string LogDirectory { get; }

    public string LogPath { get; }

    public void Info(string eventName, string message) => Write("info", eventName, message);

    public void Warn(string eventName, string message) => Write("warn", eventName, message);

    public void Error(string eventName, string message) => Write("error", eventName, message);

    private void Write(string level, string eventName, string message)
    {
        Directory.CreateDirectory(LogDirectory);

        var record = new LogRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Level = level,
            EventName = eventName,
            Message = message
        };

        var line = JsonSerializer.Serialize(record);
        lock (syncRoot)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }

    private sealed class LogRecord
    {
        public DateTimeOffset TimestampUtc { get; set; }

        public string Level { get; set; } = string.Empty;

        public string EventName { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;
    }
}
