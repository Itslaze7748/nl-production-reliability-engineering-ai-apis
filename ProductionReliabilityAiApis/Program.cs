using Microsoft.Extensions.Configuration;
using ProductionReliabilityAiApis.App;
using ProductionReliabilityAiApis.Domain;
using ProductionReliabilityAiApis.Gateway;
using ProductionReliabilityAiApis.Metrics;
using ProductionReliabilityAiApis.Workload;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "RELIABILITY_")
    .Build();

var config = AppConfig.Load(configuration);
config.Validate();

var metrics = new ReliabilityMetrics(config.MaxLatencySamples);
await using var gateway = new InferenceGateway(config, metrics);

using var reporterCts = new CancellationTokenSource();
var reporterTask = PrintMetricsLoopAsync(gateway, config, reporterCts.Token);

Console.WriteLine("Production Reliability Engineering for AI APIs (local-first)");
Console.WriteLine($"- Ollama URL: {config.OllamaBaseUrl}");
Console.WriteLine($"- Model chain: {string.Join(" -> ", config.ModelChain)}");
Console.WriteLine($"- Rate limit: {config.RateLimitRequestsPerSecond}/sec");
Console.WriteLine($"- Queue: capacity={config.QueueCapacity}, workers={config.WorkerCount}");
Console.WriteLine($"- Timeout budget: end-to-end={config.EndToEndTimeoutMs}ms, per-attempt={config.AttemptTimeoutMs}ms");
Console.WriteLine($"- Retry: {config.MaxAttemptsPerModel} attempts/model, base backoff={config.RetryBaseDelayMs}ms");
Console.WriteLine($"- Circuit breaker: threshold={config.CircuitBreakerFailureThreshold}, open={config.CircuitBreakerOpenSeconds}s");
Console.WriteLine();

var harness = new LoadHarness();
var loadSummary = await harness.RunAsync(gateway, config, CancellationToken.None);

Console.WriteLine();
Console.WriteLine("Load summary:");
Console.WriteLine($"- Total: {loadSummary.Total}");
Console.WriteLine($"- Success: {loadSummary.Successes}");
Console.WriteLine($"- Failed: {loadSummary.Failed}");
Console.WriteLine($"- Timed out: {loadSummary.TimedOut}");
Console.WriteLine($"- Rate limited: {loadSummary.RateLimited}");
Console.WriteLine($"- Queue rejected: {loadSummary.QueueRejected}");
Console.WriteLine($"- Canceled: {loadSummary.Canceled}");
Console.WriteLine($"- Avg latency (served): {loadSummary.AverageLatencyMs:F1} ms");
Console.WriteLine($"- P95 latency (served): {loadSummary.P95LatencyMs:F1} ms");

if (config.EnableInteractiveMode)
{
    Console.WriteLine();
    Console.WriteLine("Interactive mode enabled. Type /exit to quit.");

    while (true)
    {
        Console.Write("\nPrompt> ");
        var prompt = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(prompt) ||
            prompt.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        var request = new InferenceRequest(
            RequestId: $"interactive-{Guid.NewGuid():N}",
            TenantId: "interactive",
            Prompt: prompt,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Deadline: TimeSpan.FromMilliseconds(config.EndToEndTimeoutMs));

        var response = await gateway.SubmitAsync(request);

        Console.WriteLine($"Outcome: {response.Outcome}");
        Console.WriteLine($"Model: {response.ModelUsed ?? "-"}");
        Console.WriteLine($"Attempts: {response.ModelAttempts}");
        Console.WriteLine($"Latency: {response.LatencyMs:F1} ms");
        Console.WriteLine($"Response:\n{response.ResponseText}");
    }
}

reporterCts.Cancel();
try
{
    await reporterTask;
}
catch (OperationCanceledException)
{
    // expected on shutdown
}

Console.WriteLine();
Console.WriteLine("Final metrics snapshot:");
PrintSnapshot(gateway.GetMetricsSnapshot());

static async Task PrintMetricsLoopAsync(
    InferenceGateway gateway,
    AppConfig config,
    CancellationToken cancellationToken)
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(config.MetricsPrintIntervalSeconds), cancellationToken);
        Console.WriteLine();
        Console.WriteLine("[Live Metrics]");
        PrintSnapshot(gateway.GetMetricsSnapshot());
    }
}

static void PrintSnapshot(MetricsSnapshot snapshot)
{
    Console.WriteLine($"Uptime(s): {snapshot.UptimeSeconds:F1}");
    Console.WriteLine($"Requests: recv={snapshot.Received} enq={snapshot.Enqueued} done={snapshot.Completed} ok={snapshot.Succeeded} fail={snapshot.Failed} timeout={snapshot.TimedOut}");
    Console.WriteLine($"Rejections: rate={snapshot.RateLimited} queue={snapshot.QueueRejected} canceled={snapshot.Canceled}");
    Console.WriteLine($"Latency(ms): avg={snapshot.AverageLatencyMs:F1} p50={snapshot.P50LatencyMs:F1} p95={snapshot.P95LatencyMs:F1}");
    Console.WriteLine($"Rates: error={snapshot.ErrorRate:P1} timeout={snapshot.TimeoutRate:P1} fallback={snapshot.FallbackRate:P1}");
    Console.WriteLine($"Queue: current={snapshot.CurrentQueueDepth} peak={snapshot.PeakQueueDepth} saturation={snapshot.QueueSaturation:P1}");
    Console.WriteLine($"Model attempts={snapshot.ModelAttempts} | circuit-open-skips={snapshot.CircuitOpenSkips}");

    foreach (var model in snapshot.Models)
    {
        Console.WriteLine($"  {model.Model,-14} attempts={model.Attempts,-5} ok={model.Successes,-5} fail={model.Failures,-5} timeout={model.Timeouts,-5} skip={model.CircuitSkips,-5}");
    }
}
