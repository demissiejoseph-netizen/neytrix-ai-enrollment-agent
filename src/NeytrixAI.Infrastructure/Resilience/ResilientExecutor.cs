using System.Collections.Concurrent;

namespace NeytrixAI.Infrastructure.Resilience;

/// <summary>
/// Thrown when an external dependency (Stripe, Google Calendar, …) cannot be
/// reached after retries, or while its circuit breaker is open. Callers should
/// treat this as a signal to degrade gracefully and escalate to a human rather
/// than crash or hang.
/// </summary>
public sealed class ExternalServiceUnavailableException : Exception
{
    public string Operation { get; }

    public ExternalServiceUnavailableException(string operation, string message, Exception? inner = null)
        : base(message, inner)
        => Operation = operation;
}

public sealed class ResilienceOptions
{
    /// <summary>Per-attempt timeout. A single external call may not exceed this.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Number of retries after the first attempt (so total attempts = MaxRetries + 1).</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Base delay for exponential backoff between retries.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Consecutive failures before the circuit opens for an operation.</summary>
    public int CircuitBreakThreshold { get; init; } = 5;

    /// <summary>How long the circuit stays open before allowing a trial call.</summary>
    public TimeSpan CircuitResetAfter { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Small, dependency-free resilience wrapper for outbound calls to third parties.
/// Provides a per-attempt timeout, bounded exponential-backoff retries, and a
/// per-operation circuit breaker so a struggling downstream fails fast instead
/// of tying up threads. Fail-closed by design: any exhausted/So open-circuit call
/// surfaces as <see cref="ExternalServiceUnavailableException"/> for the caller
/// to escalate. Registered as a singleton so circuit state is shared across requests.
/// </summary>
public sealed class ResilientExecutor
{
    private readonly ResilienceOptions _options;
    private readonly ILogger<ResilientExecutor> _logger;
    private readonly ConcurrentDictionary<string, CircuitState> _circuits = new();

    public ResilientExecutor(ResilienceOptions options, ILogger<ResilientExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        var circuit = _circuits.GetOrAdd(operation, _ => new CircuitState());

        if (circuit.IsOpen(_options.CircuitResetAfter, out var retryAfter))
        {
            _logger.LogWarning(
                "Circuit for {Operation} is open; failing fast (retry after {RetryAfter}s).",
                operation, retryAfter.TotalSeconds);
            throw new ExternalServiceUnavailableException(operation,
                $"{operation} is temporarily unavailable (circuit open).");
        }

        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.Timeout);

            try
            {
                var result = await action(timeoutCts.Token).ConfigureAwait(false);
                circuit.RecordSuccess();
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller (not our timeout) cancelled: propagate, do not retry.
                throw;
            }
            catch (Exception ex)
            {
                var timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
                circuit.RecordFailure();

                var openedNow = circuit.FailureCount >= _options.CircuitBreakThreshold;
                if (openedNow) circuit.Open();

                var isLastAttempt = attempt > _options.MaxRetries || openedNow;
                _logger.Log(isLastAttempt ? LogLevel.Error : LogLevel.Warning, ex,
                    "{Operation} attempt {Attempt} failed (timedOut={TimedOut}).",
                    operation, attempt, timedOut);

                if (isLastAttempt)
                {
                    throw new ExternalServiceUnavailableException(operation,
                        $"{operation} failed after {attempt} attempt(s).", ex);
                }

                var delay = TimeSpan.FromMilliseconds(
                    _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class CircuitState
    {
        private readonly object _gate = new();
        private int _failureCount;
        private DateTimeOffset? _openedAt;

        public int FailureCount { get { lock (_gate) return _failureCount; } }

        public bool IsOpen(TimeSpan resetAfter, out TimeSpan retryAfter)
        {
            lock (_gate)
            {
                retryAfter = TimeSpan.Zero;
                if (_openedAt is null) return false;

                var elapsed = DateTimeOffset.UtcNow - _openedAt.Value;
                if (elapsed >= resetAfter)
                {
                    // Half-open: allow a single trial call through.
                    _openedAt = null;
                    _failureCount = 0;
                    return false;
                }

                retryAfter = resetAfter - elapsed;
                return true;
            }
        }

        public void RecordSuccess()
        {
            lock (_gate)
            {
                _failureCount = 0;
                _openedAt = null;
            }
        }

        public void RecordFailure()
        {
            lock (_gate) _failureCount++;
        }

        public void Open()
        {
            lock (_gate) _openedAt = DateTimeOffset.UtcNow;
        }
    }
}
