using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NeytrixAI.Tests.Fakes;

/// <summary>
/// A minimal <see cref="ILogger{T}"/> that captures formatted log entries so tests
/// can assert on structured audit output (state transitions, escalation records).
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public readonly ConcurrentQueue<(LogLevel Level, string Message)> Entries = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
