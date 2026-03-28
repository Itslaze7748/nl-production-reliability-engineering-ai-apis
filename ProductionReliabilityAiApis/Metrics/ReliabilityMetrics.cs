using System.Collections.Concurrent;
using System.Diagnostics;
using ProductionReliabilityAiApis.Domain;

namespace ProductionReliabilityAiApis.Metrics;

public sealed class ReliabilityMetrics
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly int _maxLatencySamples;
    private readonly ConcurrentQueue<double> _latencySamples = new();
    private readonly ConcurrentDictionary<string, ModelCounters> _modelCounters =
        new(StringComparer.OrdinalIgnoreCase);

    private long _latencySampleCount;

    private long _received;
    private long _enqueued;
    private long _completed;
    private long _succeeded;
    private long _failed;
    private long _timedOut;
    private long _canceled;
    private long _rateLimited;
    private long _queueRejected;
    private long _fallbackServed;
    private long _modelAttempts;
    private long _circuitOpenSkips;
    private long _peakQueueDepth;

    public ReliabilityMetrics(int maxLatencySamples)
    {
        if (maxLatencySamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLatencySamples));
        }

        _maxLatencySamples = maxLatencySamples;
    }

    public void RecordReceived() => Interlocked.Increment(ref _received);

    public void RecordEnqueued(int currentQueueDepth)
    {
        Interlocked.Increment(ref _enqueued);
        UpdatePeak(currentQueueDepth);
    }

    public void RecordRateLimited() => Interlocked.Increment(ref _rateLimited);

    public void RecordQueueRejected() => Interlocked.Increment(ref _queueRejected);

    public void RecordModelAttempt(string model)
    {
        Interlocked.Increment(ref _modelAttempts);
        _ = GetOrCreateModelCounters(model).IncrementAttempts();
    }

    public void RecordModelSuccess(string model)
        => _ = GetOrCreateModelCounters(model).IncrementSuccesses();

    public void RecordModelFailure(string model, bool timeout)
    {
        _ = GetOrCreateModelCounters(model).IncrementFailures();
        if (timeout)
        {
            _ = GetOrCreateModelCounters(model).IncrementTimeouts();
        }
    }

    public void RecordCircuitOpenSkip(string model)
    {
        Interlocked.Increment(ref _circuitOpenSkips);
        _ = GetOrCreateModelCounters(model).IncrementCircuitSkips();
    }

    public void RecordCompletion(InferenceResponse response)
    {
        Interlocked.Increment(ref _completed);

        switch (response.Outcome)
        {
            case RequestOutcome.Success:
                Interlocked.Increment(ref _succeeded);
                if (response.FallbackDepth > 0)
                {
                    Interlocked.Increment(ref _fallbackServed);
                }

                AddLatency(response.LatencyMs);
                break;

            case RequestOutcome.Failed:
                Interlocked.Increment(ref _failed);
                AddLatency(response.LatencyMs);
                break;

            case RequestOutcome.TimedOut:
                Interlocked.Increment(ref _timedOut);
                AddLatency(response.LatencyMs);
                break;

            case RequestOutcome.Canceled:
                Interlocked.Increment(ref _canceled);
                break;
        }
    }

    public MetricsSnapshot CreateSnapshot(int currentQueueDepth, int queueCapacity)
    {
        var samples = _latencySamples.ToArray();
        Array.Sort(samples);

        var avg = samples.Length == 0 ? 0 : samples.Average();
        var p50 = Percentile(samples, 0.50);
        var p95 = Percentile(samples, 0.95);

        var completed = Volatile.Read(ref _completed);
        var failed = Volatile.Read(ref _failed);
        var timedOut = Volatile.Read(ref _timedOut);
        var succeeded = Volatile.Read(ref _succeeded);

        var errorRate = completed == 0 ? 0 : (double)(failed + timedOut) / completed;
        var timeoutRate = completed == 0 ? 0 : (double)timedOut / completed;
        var fallbackRate = succeeded == 0 ? 0 : (double)Volatile.Read(ref _fallbackServed) / succeeded;

        var queueSaturation = queueCapacity <= 0
            ? 0
            : Math.Clamp((double)currentQueueDepth / queueCapacity, 0, 1);

        var models = _modelCounters
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => kvp.Value.ToSnapshot(kvp.Key))
            .ToArray();

        return new MetricsSnapshot(
            UptimeSeconds: _uptime.Elapsed.TotalSeconds,
            Received: Volatile.Read(ref _received),
            Enqueued: Volatile.Read(ref _enqueued),
            Completed: completed,
            Succeeded: succeeded,
            Failed: failed,
            TimedOut: timedOut,
            Canceled: Volatile.Read(ref _canceled),
            RateLimited: Volatile.Read(ref _rateLimited),
            QueueRejected: Volatile.Read(ref _queueRejected),
            FallbackServed: Volatile.Read(ref _fallbackServed),
            ModelAttempts: Volatile.Read(ref _modelAttempts),
            CircuitOpenSkips: Volatile.Read(ref _circuitOpenSkips),
            AverageLatencyMs: avg,
            P50LatencyMs: p50,
            P95LatencyMs: p95,
            ErrorRate: errorRate,
            TimeoutRate: timeoutRate,
            FallbackRate: fallbackRate,
            CurrentQueueDepth: currentQueueDepth,
            PeakQueueDepth: (int)Volatile.Read(ref _peakQueueDepth),
            QueueSaturation: queueSaturation,
            Models: models);
    }

    private ModelCounters GetOrCreateModelCounters(string model)
        => _modelCounters.GetOrAdd(model, _ => new ModelCounters());

    private void AddLatency(double latencyMs)
    {
        if (latencyMs < 0)
        {
            return;
        }

        _latencySamples.Enqueue(latencyMs);
        var count = Interlocked.Increment(ref _latencySampleCount);

        while (count > _maxLatencySamples && _latencySamples.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _latencySampleCount);
        }
    }

    private void UpdatePeak(int queueDepth)
    {
        while (true)
        {
            var current = Volatile.Read(ref _peakQueueDepth);
            if (queueDepth <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _peakQueueDepth, queueDepth, current) == current)
            {
                return;
            }
        }
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private sealed class ModelCounters
    {
        private long _attempts;
        private long _successes;
        private long _failures;
        private long _timeouts;
        private long _circuitSkips;

        public long IncrementAttempts() => Interlocked.Increment(ref _attempts);
        public long IncrementSuccesses() => Interlocked.Increment(ref _successes);
        public long IncrementFailures() => Interlocked.Increment(ref _failures);
        public long IncrementTimeouts() => Interlocked.Increment(ref _timeouts);
        public long IncrementCircuitSkips() => Interlocked.Increment(ref _circuitSkips);

        public ModelMetricsSnapshot ToSnapshot(string model)
            => new(
                Model: model,
                Attempts: Volatile.Read(ref _attempts),
                Successes: Volatile.Read(ref _successes),
                Failures: Volatile.Read(ref _failures),
                Timeouts: Volatile.Read(ref _timeouts),
                CircuitSkips: Volatile.Read(ref _circuitSkips));
    }
}
