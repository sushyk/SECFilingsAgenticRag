using System.ComponentModel.DataAnnotations;

namespace SECFilingsAgenticRag.AgentServer;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    // A whole streamed run, tool rounds included; a CPU agentic-rag answer measured up to 13 minutes.
    [Range(30, 7200)]
    public int ChatTimeoutSeconds { get; set; } = 900;

    // Rejected with 413 before the AG-UI stream starts, so no status code has to be invented mid-stream.
    [Range(1024, 10_000_000)]
    public int MaxRequestBytes { get; set; } = 64 * 1024;
}

public sealed class ChatRateLimitOptions
{
    public const string SectionName = "RateLimiting:Chat";

    [Range(1, 10_000)]
    public int PermitLimit { get; set; } = 30;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

public sealed class GuardrailsOptions
{
    public const string SectionName = "Guardrails";

    public TopicGuardOptions TopicGuard { get; set; } = new();

    // standard-rag and agentic-rag only: chat-only has nothing to cite.
    public bool RequireCitations { get; set; } = true;
}

public sealed class TopicGuardOptions
{
    // One extra model call per message (2-10 s on CPU); switch it off for demos.
    public bool Enabled { get; set; } = true;

    // Null means the AI:Chat model; a small non-reasoning model classifies faster.
    public string? Model { get; set; }
}
