namespace ProductionReliabilityAiApis.Resilience;

public sealed class FixedWindowRateLimiter
{
    private readonly object _gate = new();
    private readonly int _maxRequestsPerWindow;
    private readonly TimeSpan _window;

    private DateTimeOffset _windowStartUtc = DateTimeOffset.MinValue;
    private int _requestCount;

    public FixedWindowRateLimiter(int maxRequestsPerWindow, TimeSpan window)
    {
        if (maxRequestsPerWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRequestsPerWindow));
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        _maxRequestsPerWindow = maxRequestsPerWindow;
        _window = window;
    }

    public bool TryAcquire(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_windowStartUtc == DateTimeOffset.MinValue)
            {
                _windowStartUtc = nowUtc;
            }

            if ((nowUtc - _windowStartUtc) >= _window)
            {
                _windowStartUtc = nowUtc;
                _requestCount = 0;
            }

            if (_requestCount >= _maxRequestsPerWindow)
            {
                return false;
            }

            _requestCount++;
            return true;
        }
    }
}
