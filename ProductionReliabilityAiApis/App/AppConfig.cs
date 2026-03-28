using Microsoft.Extensions.Configuration;

namespace ProductionReliabilityAiApis.App;

public sealed class AppConfig
{
    public string OllamaBaseUrl { get; init; } = "http://localhost:11434";
    public string PrimaryModel { get; init; } = "llama3.2:3b";
    public string SecondaryModel { get; init; } = "mistral:7b";
    public string TertiaryModel { get; init; } = "phi3:mini";

    public int RateLimitRequestsPerSecond { get; init; } = 25;
    public int QueueCapacity { get; init; } = 64;
    public int WorkerCount { get; init; } = 4;

    public int EndToEndTimeoutMs { get; init; } = 7000;
    public int AttemptTimeoutMs { get; init; } = 2200;
    public int MaxAttemptsPerModel { get; init; } = 2;
    public int RetryBaseDelayMs { get; init; } = 120;

    public int CircuitBreakerFailureThreshold { get; init; } = 3;
    public int CircuitBreakerOpenSeconds { get; init; } = 20;

    public int MetricsPrintIntervalSeconds { get; init; } = 2;
    public int MaxLatencySamples { get; init; } = 5000;

    public int LoadTotalRequests { get; init; } = 140;
    public int LoadConcurrentClients { get; init; } = 35;
    public bool LoadBurstMode { get; init; } = true;

    public bool EnableInteractiveMode { get; init; } = false;

    public IReadOnlyList<string> ModelChain => BuildModelChain();

    public static AppConfig Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("App");

        return new AppConfig
        {
            OllamaBaseUrl = section["OllamaBaseUrl"] ?? "http://localhost:11434",
            PrimaryModel = section["PrimaryModel"] ?? "llama3.2:3b",
            SecondaryModel = section["SecondaryModel"] ?? "mistral:7b",
            TertiaryModel = section["TertiaryModel"] ?? "phi3:mini",
            RateLimitRequestsPerSecond = ParseInt(section["RateLimitRequestsPerSecond"], 25),
            QueueCapacity = ParseInt(section["QueueCapacity"], 64),
            WorkerCount = ParseInt(section["WorkerCount"], 4),
            EndToEndTimeoutMs = ParseInt(section["EndToEndTimeoutMs"], 7000),
            AttemptTimeoutMs = ParseInt(section["AttemptTimeoutMs"], 2200),
            MaxAttemptsPerModel = ParseInt(section["MaxAttemptsPerModel"], 2),
            RetryBaseDelayMs = ParseInt(section["RetryBaseDelayMs"], 120),
            CircuitBreakerFailureThreshold = ParseInt(section["CircuitBreakerFailureThreshold"], 3),
            CircuitBreakerOpenSeconds = ParseInt(section["CircuitBreakerOpenSeconds"], 20),
            MetricsPrintIntervalSeconds = ParseInt(section["MetricsPrintIntervalSeconds"], 2),
            MaxLatencySamples = ParseInt(section["MaxLatencySamples"], 5000),
            LoadTotalRequests = ParseInt(section["LoadTotalRequests"], 140),
            LoadConcurrentClients = ParseInt(section["LoadConcurrentClients"], 35),
            LoadBurstMode = ParseBool(section["LoadBurstMode"], true),
            EnableInteractiveMode = ParseBool(section["EnableInteractiveMode"], false)
        };
    }

    public void Validate()
    {
        if (!Uri.TryCreate(OllamaBaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("App:OllamaBaseUrl must be a valid absolute URI.");
        }

        if (ModelChain.Count == 0)
        {
            throw new InvalidOperationException("Configure at least one model in App:PrimaryModel/SecondaryModel/TertiaryModel.");
        }

        if (AttemptTimeoutMs > EndToEndTimeoutMs)
        {
            throw new InvalidOperationException("App:AttemptTimeoutMs must be less than or equal to App:EndToEndTimeoutMs.");
        }
    }

    private IReadOnlyList<string> BuildModelChain()
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>(capacity: 3);

        AddIfValid(unique, ordered, PrimaryModel);
        AddIfValid(unique, ordered, SecondaryModel);
        AddIfValid(unique, ordered, TertiaryModel);

        return ordered;
    }

    private static void AddIfValid(HashSet<string> unique, List<string> ordered, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var trimmed = model.Trim();
        if (unique.Add(trimmed))
        {
            ordered.Add(trimmed);
        }
    }

    private static int ParseInt(string? raw, int fallback)
        => int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;

    private static bool ParseBool(string? raw, bool fallback)
        => bool.TryParse(raw, out var parsed) ? parsed : fallback;
}
