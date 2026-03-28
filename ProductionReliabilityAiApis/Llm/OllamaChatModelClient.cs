using OllamaSharp;
using OllamaSharp.Models.Chat;
using System.Text;

namespace ProductionReliabilityAiApis.Llm;

public interface IChatModelClient
{
    string ModelName { get; }

    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class OllamaChatModelClient : IChatModelClient
{
    private readonly OllamaApiClient _client;

    public OllamaChatModelClient(Uri baseUri, string modelName)
    {
        ModelName = modelName;
        _client = new OllamaApiClient(baseUri)
        {
            SelectedModel = modelName
        };
    }

    public string ModelName { get; }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var request = new ChatRequest
        {
            Model = ModelName,
            Messages =
            [
                new Message(ChatRole.System, systemPrompt),
                new Message(ChatRole.User, userPrompt)
            ]
        };

        var builder = new StringBuilder(capacity: 512);
        await foreach (var chunk in _client.ChatAsync(request, timeoutCts.Token))
        {
            if (chunk?.Message?.Content is { Length: > 0 } text)
            {
                builder.Append(text);
            }
        }

        return builder.ToString().Trim();
    }
}
