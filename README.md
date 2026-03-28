# nl-production-reliability-engineering-ai-apis

A local-first C# example showing how to run LLM inference behind production reliability controls.

This project demonstrates a deterministic reliability layer for local Ollama models:

- fixed-window rate limiting
- bounded request queue with backpressure
- worker pool execution
- per-model timeout + retries
- per-model circuit breakers
- deterministic fallback chain across local models
- live metrics with p50/p95 latency, error rate, timeout rate, and fallback rate

## Stack

- .NET 10 console application
- Ollama local models via `OllamaSharp`
- No cloud dependency

## Prerequisites

- .NET 10 SDK or later
- Ollama running locally

Pull at least one chat model (three are configured by default):

```bash
ollama pull llama3.2:3b
ollama pull mistral:7b
ollama pull phi3:mini
```

## Run

```bash
cd 16.nl-production-reliability-engineering-ai-apis/ProductionReliabilityAiApis
dotnet run
```

The app starts the inference gateway, runs a synthetic load test, and prints periodic reliability metrics.

## Configuration

Set values in `appsettings.json` or with environment variables prefixed by `RELIABILITY_`.

Example:

```bash
set RELIABILITY_App__PrimaryModel=llama3.2:3b
set RELIABILITY_App__RateLimitRequestsPerSecond=30
```

Key settings:

- `RateLimitRequestsPerSecond`: ingress throttle
- `QueueCapacity`: max in-flight queued requests before rejection
- `WorkerCount`: parallel workers that process queue items
- `EndToEndTimeoutMs`: full request deadline
- `AttemptTimeoutMs`: timeout for each model call attempt
- `MaxAttemptsPerModel`: retries per model before fallback
- `CircuitBreakerFailureThreshold`: consecutive failures to open breaker
- `CircuitBreakerOpenSeconds`: cooldown before half-open probe

## Interactive mode (optional)

Set `EnableInteractiveMode = true` in `appsettings.json` to enter manual prompt mode after load test.

## Tests

```bash
cd ..\ProductionReliabilityAiApis.Tests
dotnet test
```

Tests validate deterministic behavior for:

- rate limiter windows
- circuit breaker transitions
- latency percentile metrics
