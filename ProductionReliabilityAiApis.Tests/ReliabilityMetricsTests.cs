using ProductionReliabilityAiApis.Domain;
using ProductionReliabilityAiApis.Metrics;

namespace ProductionReliabilityAiApis.Tests;

public sealed class ReliabilityMetricsTests
{
    [Fact]
    public void Snapshot_ComputesPercentiles_AndRates()
    {
        var metrics = new ReliabilityMetrics(maxLatencySamples: 100);

        metrics.RecordReceived();
        metrics.RecordReceived();
        metrics.RecordReceived();
        metrics.RecordEnqueued(currentQueueDepth: 1);
        metrics.RecordEnqueued(currentQueueDepth: 2);

        metrics.RecordCompletion(InferenceResponse.SuccessResponse(
            requestId: "r1",
            responseText: "ok",
            modelUsed: "m1",
            modelAttempts: 1,
            fallbackDepth: 0,
            latencyMs: 10,
            detail: ""));

        metrics.RecordCompletion(InferenceResponse.SuccessResponse(
            requestId: "r2",
            responseText: "ok",
            modelUsed: "m1",
            modelAttempts: 1,
            fallbackDepth: 1,
            latencyMs: 30,
            detail: ""));

        metrics.RecordCompletion(InferenceResponse.FailureResponse(
            requestId: "r3",
            outcome: RequestOutcome.TimedOut,
            responseText: "timeout",
            latencyMs: 50,
            modelAttempts: 2,
            detail: ""));

        var snapshot = metrics.CreateSnapshot(currentQueueDepth: 0, queueCapacity: 10);

        Assert.Equal(3, snapshot.Received);
        Assert.Equal(3, snapshot.Completed);
        Assert.Equal(2, snapshot.Succeeded);
        Assert.Equal(1, snapshot.TimedOut);

        Assert.InRange(snapshot.P50LatencyMs, 29.9, 30.1);
        Assert.InRange(snapshot.P95LatencyMs, 47.9, 50.0);
        Assert.InRange(snapshot.TimeoutRate, 0.33, 0.34);
        Assert.InRange(snapshot.FallbackRate, 0.49, 0.51);
    }
}
