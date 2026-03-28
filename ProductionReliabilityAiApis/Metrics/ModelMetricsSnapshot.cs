namespace ProductionReliabilityAiApis.Metrics;

public sealed record ModelMetricsSnapshot(
    string Model,
    long Attempts,
    long Successes,
    long Failures,
    long Timeouts,
    long CircuitSkips);
