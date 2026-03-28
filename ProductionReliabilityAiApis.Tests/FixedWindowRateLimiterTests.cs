using ProductionReliabilityAiApis.Resilience;

namespace ProductionReliabilityAiApis.Tests;

public sealed class FixedWindowRateLimiterTests
{
    [Fact]
    public void TryAcquire_RespectsWindowBudget_AndResetsOnNewWindow()
    {
        var limiter = new FixedWindowRateLimiter(
            maxRequestsPerWindow: 2,
            window: TimeSpan.FromSeconds(1));

        var t0 = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);

        Assert.True(limiter.TryAcquire(t0));
        Assert.True(limiter.TryAcquire(t0.AddMilliseconds(100)));
        Assert.False(limiter.TryAcquire(t0.AddMilliseconds(200)));

        Assert.True(limiter.TryAcquire(t0.AddSeconds(1).AddMilliseconds(1)));
    }
}
