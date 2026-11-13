using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using SECFilingsAgenticRag.Common.Telemetry;

namespace SECFilingsAgenticRag.Common.AI;

// Custom (no built-in fits): AddChatClient registers one client per container, but the reranker, query rewriter, topic guard and
// eval judge may each run a different model on the same Ollama endpoint with the same logging and telemetry pipeline.
public static class ChatClientFactory
{
    public const string OllamaHttpClientName = "ollama";

    public static IChatClient Create(IServiceProvider services, string model)
    {
        var contextLength = services.GetRequiredService<IOptions<ChatModelOptions>>().Value.ContextLength;
        var telemetry = services.GetRequiredService<IOptions<TelemetryOptions>>().Value;
        return new ChatClientBuilder(new OllamaApiClient(OllamaHttpClient(services), model))
            .ConfigureOptions(options =>
            {
                if (contextLength is not null)
                    options.AddOllamaOption(OllamaOption.NumCtx, contextLength.Value);
            })
            .UseLogging()
            .UseOpenTelemetry(sourceName: TelemetryNames.AI, configure: otel => otel.EnableSensitiveData = telemetry.EnableSensitiveData)
            .Build(services);
    }

    // An unset per-feature model override means the registered AI:Chat client.
    public static IChatClient ForModel(IServiceProvider services, string? model) =>
        string.IsNullOrWhiteSpace(model) ? services.GetRequiredService<IChatClient>() : Create(services, model);

    public static HttpClient OllamaHttpClient(IServiceProvider services) =>
        services.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaHttpClientName);
}
