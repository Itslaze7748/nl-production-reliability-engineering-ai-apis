using System.Collections.Concurrent;
using ProductionReliabilityAiApis.Domain;

namespace ProductionReliabilityAiApis.Workload;

public sealed class LoadHarness
{
    private static readonly string[] PromptTemplates =
    [
        "How do I reduce p95 latency in an AI API while preserving answer quality?",
        "What deterministic fallback policy should we use when the primary model fails?",
        "Design retry and timeout settings for a local LLM gateway handling incident questions.",
        "How should we instrument queue saturation and circuit breaker events in production?",
        "Give a concise runbook for AI API reliability during traffic spikes."
    ];

    public async Task<LoadRunSummary> RunAsync(
        Gateway.InferenceGateway gateway,
        App.AppConfig config,
        CancellationToken cancellationToken = default)
    {
        var responses = new ConcurrentBag<InferenceResponse>();

        using var concurrencyGate = new SemaphoreSlim(config.LoadConcurrentClients);
        var tasks = new List<Task>(capacity: config.LoadTotalRequests);

        for (var i = 1; i <= config.LoadTotalRequests; i++)
        {
            var requestNumber = i;
            tasks.Add(Task.Run(async () =>
            {
                await concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (config.LoadBurstMode && (requestNumber % 25 == 0))
                    {
                        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                    }

                    var prompt = BuildPrompt(requestNumber);
                    var request = new InferenceRequest(
                        RequestId: $"load-{requestNumber:0000}",
                        TenantId: "newsletter-load",
                        Prompt: prompt,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        Deadline: TimeSpan.FromMilliseconds(config.EndToEndTimeoutMs));

                    var response = await gateway.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
                    responses.Add(response);
                }
                finally
                {
                    concurrencyGate.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return LoadRunSummary.FromResponses(responses.ToArray());
    }

    private static string BuildPrompt(int requestNumber)
    {
        var template = PromptTemplates[(requestNumber - 1) % PromptTemplates.Length];
        return $"{template}\nRequestId={requestNumber}";
    }
}
