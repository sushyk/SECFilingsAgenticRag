using System.ComponentModel.DataAnnotations;

namespace SECFilingsAgenticRag.McpServer;

public sealed class McpResourceOptions
{
    public const string SectionName = "Mcp";

    // A 10-K Item 1A runs to 10-20k tokens and MCP hosts attach a resource whole, so the cap protects the client's context window.
    [Range(500, 100_000)]
    public int MaxResourceTokens { get; set; } = 6000;
}
