using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Trainer.TestSupport;

/// <summary>One scope active when an entry was logged: its formatted text and, when it is structured, its values.</summary>
public sealed record CapturedLogScope(string Text, IReadOnlyDictionary<string, object?> Values);

/// <summary>One captured log call.</summary>
/// <param name="State">The structured state values (message template parameters, including <c>{OriginalFormat}</c>).</param>
/// <param name="Scopes">Active scopes, outermost first.</param>
/// <param name="Exception">The full <c>Exception.ToString()</c>, or null.</param>
public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> State,
    IReadOnlyList<CapturedLogScope> Scopes,
    string? Exception);

/// <summary>
/// Records everything logged through it, with structured values and scopes, so tests can assert on what the app
/// logged (and, for secrets, on what it did not).
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>A snapshot of the entries captured so far, in order.</summary>
    public IReadOnlyList<CapturedLogEntry> Entries => [.. _entries];

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private void Record<TState>(
        string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        List<CapturedLogScope> scopes = [];
        _scopes.ForEachScope(
            static (scope, list) => list.Add(new CapturedLogScope(scope?.ToString() ?? string.Empty, ToValues(scope))),
            scopes);
        _entries.Enqueue(new CapturedLogEntry(
            category, level, eventId, formatter(state, exception), ToValues(state), scopes, exception?.ToString()));
    }

    private static Dictionary<string, object?> ToValues(object? state)
    {
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (KeyValuePair<string, object?> pair in pairs)
            {
                values[pair.Key] = pair.Value;
            }
        }

        return values;
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Record(category, logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
