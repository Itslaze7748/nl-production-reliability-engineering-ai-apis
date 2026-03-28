namespace ProductionReliabilityAiApis.Domain;

public enum RequestOutcome
{
    Success = 0,
    RateLimited = 1,
    QueueRejected = 2,
    TimedOut = 3,
    Failed = 4,
    Canceled = 5
}
