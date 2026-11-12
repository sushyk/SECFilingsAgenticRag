using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OllamaSharp.Models;

namespace SECFilingsAgenticRag.Common.AI;

// Custom (no built-in fits): AddChatClient registers one client per container, but the reranker, query rewriter and eval judge
// may each run a different model on the same Ollama endpoint with the same logging and telemetry pipeline.
public static class ChatClientFactory
{
    // Local CPU inference is slow, and ServiceDefaults attach a 10 s / 30 s resilience handler to IHttpClientFactory
    // clients, which would kill these calls. That is why Ollama gets a dedicated HttpClient.
    private static readonly TimeSpan OllamaTimeout = TimeSpan.FromMinutes(10);

    public static IChatClient Create(IServiceProvider services, IConfiguration configuration, string model)
    {
        // Ollama silently drops the start of a prompt longer than its default window (measured: a 4,132-token rerank prompt
        // cut to 2,050), which removes the instructions and the question first. Retrieved sources need a larger window.
        var contextLength = configuration["AI:Chat:ContextLength"];
        return new ChatClientBuilder(new OllamaApiClient(NewOllamaHttpClient(configuration), model))
            .ConfigureOptions(options =>
            {
                if (contextLength is not null)
                    options.AddOllamaOption(OllamaOption.NumCtx, int.Parse(contextLength));
            })
            .UseLogging()
            .UseOpenTelemetry()
            .Build(services);
    }

    internal static HttpClient NewOllamaHttpClient(IConfiguration configuration) => new()
    {
        BaseAddress = new Uri(configuration.GetConnectionString("ollama")
            ?? throw new InvalidOperationException("ConnectionStrings:ollama is not configured, e.g. \"http://localhost:11434\".")),
        Timeout = OllamaTimeout,
    };
}
