using System.ComponentModel.DataAnnotations;

namespace SECFilingsAgenticRag.AgentServer;

public enum SessionStoreKind { InMemory, Postgres }

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

    // Caps model round trips per agentic-rag run (Learn: 5-10 is typical); each one costs tens of seconds on CPU.
    [Range(1, 20)]
    public int MaxToolIterations { get; set; } = 6;

    // Enforced on the last user message by the guardrail middleware; the HTTP body limit (Api:MaxRequestBytes) comes first.
    [Range(16, 100_000)]
    public int MaxInputTokens { get; set; } = 1000;

    // Each standard-rag source is cut to this many tokens; chunks are indexed at up to 512, so the default only trims outliers.
    [Range(32, 4096)]
    public int MaxExcerptTokens { get; set; } = 512;

    // InMemory forgets every conversation on restart; the evals use it because they never resume a thread.
    public SessionStoreKind SessionStore { get; set; } = SessionStoreKind.InMemory;

    // Compaction bounds what is sent to the model, never what is stored. Sized for AI:Chat:ContextLength 8192, leaving
    // about 2k tokens for the answer (and for a reasoning model's thinking, which Ollama counts against the window).
    [Range(256, 1_000_000)]
    public int CompactAboveTokens { get; set; } = 4000;

    [Range(1, 1000)]
    public int MaxHistoryTurns { get; set; } = 10;

    [Range(256, 1_000_000)]
    public int HardTokenBudget { get; set; } = 6000;
}
