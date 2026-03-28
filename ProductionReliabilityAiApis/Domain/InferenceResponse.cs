namespace ProductionReliabilityAiApis.Domain;

public sealed record InferenceResponse(
    string RequestId,
    RequestOutcome Outcome,
    string ResponseText,
    string? ModelUsed,
    int ModelAttempts,
    int FallbackDepth,
    double LatencyMs,
    string Detail)
{
    public bool Success => Outcome == RequestOutcome.Success;

    public static InferenceResponse SuccessResponse(
        string requestId,
        string responseText,
        string modelUsed,
        int modelAttempts,
        int fallbackDepth,
        double latencyMs,
        string detail)
        => new(
            RequestId: requestId,
            Outcome: RequestOutcome.Success,
            ResponseText: responseText,
            ModelUsed: modelUsed,
            ModelAttempts: modelAttempts,
            FallbackDepth: fallbackDepth,
            LatencyMs: latencyMs,
            Detail: detail);

    public static InferenceResponse FailureResponse(
        string requestId,
        RequestOutcome outcome,
        string responseText,
        double latencyMs,
        int modelAttempts,
        string detail)
        => new(
            RequestId: requestId,
            Outcome: outcome,
            ResponseText: responseText,
            ModelUsed: null,
            ModelAttempts: modelAttempts,
            FallbackDepth: -1,
            LatencyMs: latencyMs,
            Detail: detail);
}
