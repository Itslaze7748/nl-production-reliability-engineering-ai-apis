namespace ProductionReliabilityAiApis.Resilience;

public enum CircuitState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2
}

public sealed class CircuitBreaker
{
    private readonly object _gate = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAtUtc = DateTimeOffset.MinValue;
    private bool _halfOpenProbeReserved;

    public CircuitBreaker(int failureThreshold, TimeSpan openDuration)
    {
        if (failureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }

        if (openDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(openDuration));
        }

        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
    }

    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool CanExecute(DateTimeOffset nowUtc, out string reason)
    {
        lock (_gate)
        {
            if (_state == CircuitState.Closed)
            {
                reason = "closed";
                return true;
            }

            if (_state == CircuitState.Open)
            {
                var elapsed = nowUtc - _openedAtUtc;
                if (elapsed < _openDuration)
                {
                    reason = "circuit-open";
                    return false;
                }

                _state = CircuitState.HalfOpen;
                _halfOpenProbeReserved = false;
            }

            if (_state == CircuitState.HalfOpen)
            {
                if (_halfOpenProbeReserved)
                {
                    reason = "half-open-probe-in-flight";
                    return false;
                }

                _halfOpenProbeReserved = true;
                reason = "half-open-probe";
                return true;
            }

            reason = "closed";
            return true;
        }
    }

    public void OnSuccess()
    {
        lock (_gate)
        {
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
            _halfOpenProbeReserved = false;
        }
    }

    public void OnFailure(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_state == CircuitState.HalfOpen)
            {
                Open(nowUtc);
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                Open(nowUtc);
            }
        }
    }

    private void Open(DateTimeOffset nowUtc)
    {
        _state = CircuitState.Open;
        _openedAtUtc = nowUtc;
        _consecutiveFailures = 0;
        _halfOpenProbeReserved = false;
    }
}
