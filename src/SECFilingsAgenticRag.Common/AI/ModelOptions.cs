using System.ComponentModel.DataAnnotations;

namespace SECFilingsAgenticRag.Common.AI;

// No code defaults for model names: each host's appsettings.json holds them and AI__* environment variables override them,
// so changing a model after deployment is configuration only.
public sealed class ChatModelOptions
{
    public const string SectionName = "AI:Chat";

    [Required]
    public string? Model { get; set; }

    // Ollama silently drops the start of a prompt longer than its default window (measured: a 4,132-token rerank prompt cut to
    // 2,050), which removes the instructions and the question first. Retrieved sources need a larger window.
    [Range(512, 1_048_576)]
    public int? ContextLength { get; set; }

    // Per Ollama HTTP attempt. A CPU completion takes 20-300 s; for a streamed one this bounds only the time to the headers.
    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 600;
}

public sealed class EmbeddingModelOptions
{
    public const string SectionName = "AI:Embedding";

    // Must produce EmbeddingModelConstants.Dimensions and understand its task prefixes; changing it means re-indexing.
    [Required]
    public string? Model { get; set; }
}
