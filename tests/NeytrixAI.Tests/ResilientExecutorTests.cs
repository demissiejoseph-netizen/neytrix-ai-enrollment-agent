using Microsoft.Extensions.Logging.Abstractions;
using NeytrixAI.Infrastructure.Resilience;
using Xunit;

namespace NeytrixAI.Tests;

// Unit tests for the shared resilience wrapper that fronts every outbound
// third-party call. They pin the fail-closed contract: retries are bounded, a
// timeout or exhausted retry surfaces as ExternalServiceUnavailableException, the
// circuit breaker fails fast once tripped, and caller cancellation is never
// swallowed (it propagates rather than being retried).
public sealed class ResilientExecutorTests
{
    private static ResilientExecutor Build(ResilienceOptions options) =>
        new(options, NullLogger<ResilientExecutor>.Instance);

    private static ResilienceOptions FastOptions(
        int maxRetries = 2, int threshold = 10, int timeoutMs = 200) => new()
    {
        Timeout = TimeSpan.FromMilliseconds(timeoutMs),
        MaxRetries = maxRetries,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        CircuitBreakThreshold = threshold,
        CircuitResetAfter = TimeSpan.FromSeconds(30)
    };

    [Fact]
    public async Task Success_ReturnsValue_WithoutRetrying()
    {
        var executor = Build(FastOptions());
        var calls = 0;

        var result = await executor.ExecuteAsync<string>("op.success",
            _ => { calls++; return Task.FromResult("ok"); }, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retry_ThenSuccess_ReturnsValue()
    {
        var executor = Build(FastOptions(maxRetries: 3));
        var calls = 0;

        var result = await executor.ExecuteAsync<string>("op.retry",
            _ =>
            {
                calls++;
                if (calls < 3) throw new InvalidOperationException("transient");
                return Task.FromResult("recovered");
            }, CancellationToken.None);

        Assert.Equal("recovered", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExhaustedRetries_ThrowsExternalServiceUnavailable()
    {
        var executor = Build(FastOptions(maxRetries: 2));
        var calls = 0;

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            executor.ExecuteAsync<string>("op.exhaust",
                _ => { calls++; throw new InvalidOperationException("down"); }, CancellationToken.None));

        Assert.Equal("op.exhaust", ex.Operation);
        Assert.Equal(3, calls); // 1 initial + 2 retries
    }

    [Fact]
    public async Task Timeout_ThrowsExternalServiceUnavailable()
    {
        var executor = Build(FastOptions(maxRetries: 0, timeoutMs: 50));

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            executor.ExecuteAsync<string>("op.timeout",
                async token => { await Task.Delay(5000, token); return "never"; }, CancellationToken.None));
    }

    [Fact]
    public async Task OpenCircuit_FailsFast()
    {
        var executor = Build(FastOptions(maxRetries: 0, threshold: 1));

        // First failure trips the circuit (threshold = 1).
        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            executor.ExecuteAsync<string>("op.circuit",
                _ => throw new InvalidOperationException("down"), CancellationToken.None));

        // Next call must fail fast without invoking the action.
        var invoked = false;
        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            executor.ExecuteAsync<string>("op.circuit",
                _ => { invoked = true; return Task.FromResult("x"); }, CancellationToken.None));

        Assert.False(invoked);
        Assert.Contains("circuit open", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_NotWrapped()
    {
        var executor = Build(FastOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync<string>("op.cancel",
                _ => Task.FromResult("x"), cts.Token));
    }
}
