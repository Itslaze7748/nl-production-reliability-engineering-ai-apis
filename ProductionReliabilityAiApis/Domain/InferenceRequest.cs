namespace ProductionReliabilityAiApis.Domain;

public sealed record InferenceRequest(
    string RequestId,
    string TenantId,
    string Prompt,
    DateTimeOffset CreatedAtUtc,
    TimeSpan Deadline);
