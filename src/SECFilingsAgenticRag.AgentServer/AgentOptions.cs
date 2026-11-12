using System.ComponentModel.DataAnnotations;

namespace SECFilingsAgenticRag.AgentServer;

// Tuning only: the agent itself is chosen per request by URL, never by configuration.
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    // Every retrieved chunk is prompt tokens the CPU must process before the first answer token.
    [Range(1, 20)]
    public int RetrievalTop { get; set; } = 5;

    // Follow-ups like "which of those…" only retrieve well when earlier user turns are part of the search input.
    [Range(0, 20)]
    public int RecentMessageMemoryLimit { get; set; } = 4;
}
