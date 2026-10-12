using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Satora.Tests;

// What a worker logged and how loudly.
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    // The exception logged with each entry, in the order of Entries.
    public List<Exception?> Errors { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
        Errors.Add(exception);
    }
}
