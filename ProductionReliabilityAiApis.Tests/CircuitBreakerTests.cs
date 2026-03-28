using ProductionReliabilityAiApis.Resilience;

namespace ProductionReliabilityAiApis.Tests;

public sealed class CircuitBreakerTests
{
    [Fact]
    public void CircuitBreaker_OpensAndHalfOpens_AsExpected()
    {
        var breaker = new CircuitBreaker(
            failureThreshold: 2,
            openDuration: TimeSpan.FromSeconds(10));

        var t0 = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);

        Assert.True(breaker.CanExecute(t0, out _));

        breaker.OnFailure(t0.AddMilliseconds(10));
        breaker.OnFailure(t0.AddMilliseconds(20));

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.CanExecute(t0.AddSeconds(5), out _));

        Assert.True(breaker.CanExecute(t0.AddSeconds(11), out var reason));
        Assert.Equal("half-open-probe", reason);

        Assert.False(breaker.CanExecute(t0.AddSeconds(11).AddMilliseconds(1), out _));

        breaker.OnSuccess();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.CanExecute(t0.AddSeconds(12), out _));
    }
}
