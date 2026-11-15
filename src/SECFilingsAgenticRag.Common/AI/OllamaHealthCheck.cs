using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SECFilingsAgenticRag.Common.AI;

// Custom (no built-in fits): Aspire has no client integration for a natively run Ollama, and reachability alone is not enough:
// a misconfigured model name only fails at the first chat or embedding call.
public sealed class OllamaHealthCheck(
    IHttpClientFactory httpClientFactory, IOptions<ChatModelOptions> chat, IOptions<EmbeddingModelOptions> embedding) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed record Tags(Model[] Models);
    private sealed record Model(string Name);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        Tags? tags;
        try
        {
            tags = await httpClientFactory.CreateClient(ChatClientFactory.OllamaHttpClientName).GetFromJsonAsync<Tags>("api/tags", timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Ollama is unreachable.", ex);
        }

        // Ollama lists untagged pulls as "<name>:latest".
        var installed = (tags?.Models ?? []).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new[] { chat.Value.Model!, embedding.Value.Model! }
            .Where(model => !installed.Contains(model) && !installed.Contains($"{model}:latest")).ToList();
        return missing.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"Ollama has not pulled: {string.Join(", ", missing)}.");
    }
}
