using System.Diagnostics;
using System.Threading.Channels;
using ProductionReliabilityAiApis.App;
using ProductionReliabilityAiApis.Domain;
using ProductionReliabilityAiApis.Llm;
using ProductionReliabilityAiApis.Metrics;
using ProductionReliabilityAiApis.Resilience;

namespace ProductionReliabilityAiApis.Gateway;

public sealed class InferenceGateway : IAsyncDisposable
{
    private const string TimeoutMessage = "I could not complete this request within the latency budget. Please retry.";
    private const string FailureMessage = "I cannot provide a reliable answer right now. Please retry shortly.";

    private readonly AppConfig _config;
    private readonly ReliabilityMetrics _metrics;
    private readonly RetryPolicy _retryPolicy;
    private readonly TimeSpan _attemptTimeout;

    private readonly IReadOnlyList<string> _modelChain;
    private readonly Dictionary<string, IChatModelClient> _modelClients;
    private readonly Dictionary<string, CircuitBreaker> _circuitBreakers;

    private readonly FixedWindowRateLimiter _rateLimiter;
    private readonly Channel<QueuedRequest> _channel;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task[] _workers;

    private int _queueDepth;

    public InferenceGateway(AppConfig config, ReliabilityMetrics metrics)
    {
        _config = config;
        _metrics = metrics;

        _retryPolicy = new RetryPolicy(config.MaxAttemptsPerModel, config.RetryBaseDelayMs);
        _attemptTimeout = TimeSpan.FromMilliseconds(config.AttemptTimeoutMs);

        _modelChain = config.ModelChain;

        var baseUri = new Uri(config.OllamaBaseUrl);
        _modelClients = new Dictionary<string, IChatModelClient>(StringComparer.OrdinalIgnoreCase);
        _circuitBreakers = new Dictionary<string, CircuitBreaker>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in _modelChain)
        {
            _modelClients[model] = new OllamaChatModelClient(baseUri, model);
            _circuitBreakers[model] = new CircuitBreaker(
                config.CircuitBreakerFailureThreshold,
                TimeSpan.FromSeconds(config.CircuitBreakerOpenSeconds));
        }

        _rateLimiter = new FixedWindowRateLimiter(
            config.RateLimitRequestsPerSecond,
            TimeSpan.FromSeconds(1));

        _channel = Channel.CreateBounded<QueuedRequest>(new BoundedChannelOptions(config.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false
        });

        _workers = Enumerable.Range(1, config.WorkerCount)
            .Select(workerId => Task.Run(() => WorkerLoopAsync(workerId, _shutdownCts.Token)))
            .ToArray();
    }

    public int CurrentQueueDepth => Volatile.Read(ref _queueDepth);

    public MetricsSnapshot GetMetricsSnapshot()
        => _metrics.CreateSnapshot(CurrentQueueDepth, _config.QueueCapacity);

    public async Task<InferenceResponse> SubmitAsync(InferenceRequest request, CancellationToken cancellationToken = default)
    {
        _metrics.RecordReceived();

        if (cancellationToken.IsCancellationRequested)
        {
            return InferenceResponse.FailureResponse(
                requestId: request.RequestId,
                outcome: RequestOutcome.Canceled,
                responseText: "Request was canceled before enqueue.",
                latencyMs: 0,
                modelAttempts: 0,
                detail: "client-canceled");
        }

        if (!_rateLimiter.TryAcquire(DateTimeOffset.UtcNow))
        {
            _metrics.RecordRateLimited();
            return InferenceResponse.FailureResponse(
                requestId: request.RequestId,
                outcome: RequestOutcome.RateLimited,
                responseText: "Rate limit exceeded. Retry shortly.",
                latencyMs: 0,
                modelAttempts: 0,
                detail: "rate-limited");
        }

        var completion = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new QueuedRequest(request, completion);

        var depth = Interlocked.Increment(ref _queueDepth);
        if (!_channel.Writer.TryWrite(queued))
        {
            Interlocked.Decrement(ref _queueDepth);
            _metrics.RecordQueueRejected();

            return InferenceResponse.FailureResponse(
                requestId: request.RequestId,
                outcome: RequestOutcome.QueueRejected,
                responseText: "Queue is saturated. Retry shortly.",
                latencyMs: 0,
                modelAttempts: 0,
                detail: "queue-rejected");
        }

        _metrics.RecordEnqueued(depth);

        return await completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _shutdownCts.Cancel();

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown path
        }
        finally
        {
            _shutdownCts.Dispose();
        }
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken shutdownToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(shutdownToken).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var queued))
                {
                    Interlocked.Decrement(ref _queueDepth);

                    InferenceResponse response;
                    try
                    {
                        response = await ExecuteWithReliabilityAsync(queued.Request, shutdownToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                    {
                        response = InferenceResponse.FailureResponse(
                            requestId: queued.Request.RequestId,
                            outcome: RequestOutcome.Canceled,
                            responseText: "Worker canceled during shutdown.",
                            latencyMs: 0,
                            modelAttempts: 0,
                            detail: $"worker-{workerId}-shutdown");
                    }
                    catch (Exception ex)
                    {
                        response = InferenceResponse.FailureResponse(
                            requestId: queued.Request.RequestId,
                            outcome: RequestOutcome.Failed,
                            responseText: FailureMessage,
                            latencyMs: 0,
                            modelAttempts: 0,
                            detail: $"worker-{workerId}-unexpected:{ex.GetType().Name}");
                    }

                    _metrics.RecordCompletion(response);
                    queued.Completion.TrySetResult(response);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown path
        }
    }

    private async Task<InferenceResponse> ExecuteWithReliabilityAsync(InferenceRequest request, CancellationToken shutdownToken)
    {
        var sw = Stopwatch.StartNew();
        var attemptsMade = 0;
        var attemptTrace = new List<string>(capacity: _modelChain.Count * _retryPolicy.MaxAttempts);

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        var deadline = request.Deadline <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(_config.EndToEndTimeoutMs)
            : request.Deadline;
        deadlineCts.CancelAfter(deadline);
        var requestToken = deadlineCts.Token;

        var systemPrompt = BuildSystemPrompt();

        for (var modelIndex = 0; modelIndex < _modelChain.Count; modelIndex++)
        {
            var model = _modelChain[modelIndex];
            var breaker = _circuitBreakers[model];
            var modelClient = _modelClients[model];

            if (!breaker.CanExecute(DateTimeOffset.UtcNow, out var breakerReason))
            {
                attemptTrace.Add($"{model}:skip({breakerReason})");
                _metrics.RecordCircuitOpenSkip(model);
                continue;
            }

            for (var attempt = 1; attempt <= _retryPolicy.MaxAttempts; attempt++)
            {
                if (requestToken.IsCancellationRequested)
                {
                    return InferenceResponse.FailureResponse(
                        requestId: request.RequestId,
                        outcome: RequestOutcome.TimedOut,
                        responseText: TimeoutMessage,
                        latencyMs: sw.Elapsed.TotalMilliseconds,
                        modelAttempts: attemptsMade,
                        detail: string.Join(" | ", attemptTrace));
                }

                var remainingBudget = deadline - sw.Elapsed;
                if (remainingBudget <= TimeSpan.Zero)
                {
                    return InferenceResponse.FailureResponse(
                        requestId: request.RequestId,
                        outcome: RequestOutcome.TimedOut,
                        responseText: TimeoutMessage,
                        latencyMs: sw.Elapsed.TotalMilliseconds,
                        modelAttempts: attemptsMade,
                        detail: string.Join(" | ", attemptTrace));
                }

                var attemptTimeout = remainingBudget < _attemptTimeout ? remainingBudget : _attemptTimeout;
                if (attemptTimeout <= TimeSpan.Zero)
                {
                    attemptTimeout = TimeSpan.FromMilliseconds(1);
                }

                attemptsMade++;
                _metrics.RecordModelAttempt(model);
                attemptTrace.Add($"{model}:try{attempt}");

                try
                {
                    var text = await modelClient.CompleteAsync(
                        systemPrompt,
                        request.Prompt,
                        attemptTimeout,
                        requestToken).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        throw new InvalidOperationException("empty-response");
                    }

                    breaker.OnSuccess();
                    _metrics.RecordModelSuccess(model);

                    return InferenceResponse.SuccessResponse(
                        requestId: request.RequestId,
                        responseText: text,
                        modelUsed: model,
                        modelAttempts: attemptsMade,
                        fallbackDepth: modelIndex,
                        latencyMs: sw.Elapsed.TotalMilliseconds,
                        detail: string.Join(" | ", attemptTrace));
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    breaker.OnFailure(DateTimeOffset.UtcNow);
                    _metrics.RecordModelFailure(model, timeout: true);
                    attemptTrace.Add($"{model}:timeout");

                    if (requestToken.IsCancellationRequested)
                    {
                        return InferenceResponse.FailureResponse(
                            requestId: request.RequestId,
                            outcome: RequestOutcome.TimedOut,
                            responseText: TimeoutMessage,
                            latencyMs: sw.Elapsed.TotalMilliseconds,
                            modelAttempts: attemptsMade,
                            detail: string.Join(" | ", attemptTrace));
                    }
                }
                catch (Exception ex)
                {
                    breaker.OnFailure(DateTimeOffset.UtcNow);
                    _metrics.RecordModelFailure(model, timeout: false);
                    attemptTrace.Add($"{model}:error({ex.GetType().Name})");
                }

                if (attempt < _retryPolicy.MaxAttempts)
                {
                    var delay = _retryPolicy.GetDelay(attempt);
                    var remainingAfterAttempt = deadline - sw.Elapsed;
                    if (remainingAfterAttempt <= TimeSpan.Zero)
                    {
                        return InferenceResponse.FailureResponse(
                            requestId: request.RequestId,
                            outcome: RequestOutcome.TimedOut,
                            responseText: TimeoutMessage,
                            latencyMs: sw.Elapsed.TotalMilliseconds,
                            modelAttempts: attemptsMade,
                            detail: string.Join(" | ", attemptTrace));
                    }

                    var boundedDelay = delay < remainingAfterAttempt ? delay : remainingAfterAttempt;
                    try
                    {
                        await Task.Delay(boundedDelay, requestToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return InferenceResponse.FailureResponse(
                            requestId: request.RequestId,
                            outcome: RequestOutcome.TimedOut,
                            responseText: TimeoutMessage,
                            latencyMs: sw.Elapsed.TotalMilliseconds,
                            modelAttempts: attemptsMade,
                            detail: string.Join(" | ", attemptTrace));
                    }
                }
            }
        }

        return InferenceResponse.FailureResponse(
            requestId: request.RequestId,
            outcome: RequestOutcome.Failed,
            responseText: FailureMessage,
            latencyMs: sw.Elapsed.TotalMilliseconds,
            modelAttempts: attemptsMade,
            detail: string.Join(" | ", attemptTrace));
    }

    private static string BuildSystemPrompt()
        =>
            "You are a local production inference endpoint. " +
            "Respond with concise, factual, operational guidance. " +
            "If data is missing, state assumptions explicitly.";

    private sealed record QueuedRequest(
        InferenceRequest Request,
        TaskCompletionSource<InferenceResponse> Completion);
}
