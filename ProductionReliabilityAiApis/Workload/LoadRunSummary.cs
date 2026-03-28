using ProductionReliabilityAiApis.Domain;

namespace ProductionReliabilityAiApis.Workload;

public sealed record LoadRunSummary(
    int Total,
    int Successes,
    int Failed,
    int TimedOut,
    int RateLimited,
    int QueueRejected,
    int Canceled,
    double AverageLatencyMs,
    double P95LatencyMs)
{
    public static LoadRunSummary FromResponses(IReadOnlyCollection<InferenceResponse> responses)
    {
        var total = responses.Count;
        var successes = responses.Count(r => r.Outcome == RequestOutcome.Success);
        var failed = responses.Count(r => r.Outcome == RequestOutcome.Failed);
        var timedOut = responses.Count(r => r.Outcome == RequestOutcome.TimedOut);
        var rateLimited = responses.Count(r => r.Outcome == RequestOutcome.RateLimited);
        var queueRejected = responses.Count(r => r.Outcome == RequestOutcome.QueueRejected);
        var canceled = responses.Count(r => r.Outcome == RequestOutcome.Canceled);

        var latencySamples = responses
            .Where(r => r.Outcome is RequestOutcome.Success or RequestOutcome.Failed or RequestOutcome.TimedOut)
            .Select(r => r.LatencyMs)
            .OrderBy(v => v)
            .ToArray();

        var averageLatency = latencySamples.Length == 0 ? 0 : latencySamples.Average();
        var p95Latency = Percentile(latencySamples, 0.95);

        return new LoadRunSummary(
            Total: total,
            Successes: successes,
            Failed: failed,
            TimedOut: timedOut,
            RateLimited: rateLimited,
            QueueRejected: queueRejected,
            Canceled: canceled,
            AverageLatencyMs: averageLatency,
            P95LatencyMs: p95Latency);
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
}
