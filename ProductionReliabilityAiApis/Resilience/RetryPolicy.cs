namespace ProductionReliabilityAiApis.Resilience;

public sealed class RetryPolicy
{
    public RetryPolicy(int maxAttempts, int baseDelayMs)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        if (baseDelayMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelayMs));
        }

        MaxAttempts = maxAttempts;
        BaseDelayMs = baseDelayMs;
    }

    public int MaxAttempts { get; }

    public int BaseDelayMs { get; }

    public TimeSpan GetDelay(int attempt)
    {
        var boundedAttempt = Math.Max(1, attempt);
        return TimeSpan.FromMilliseconds(BaseDelayMs * boundedAttempt);
    }
}
