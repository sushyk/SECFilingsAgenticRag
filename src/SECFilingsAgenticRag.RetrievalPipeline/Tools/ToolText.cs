using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace SECFilingsAgenticRag.RetrievalPipeline.Tools;

// Custom (no built-in fits): untrusted-text fencing shared by standard-rag's sources, the tool excerpts and the MCP resources.
public static partial class ToolText
{
    // Retrieved text is never trusted: chat-template tokens could open a new turn or a reasoning block, angle brackets could
    // close the fence, and long runs of blank lines are a common way to push injected text out of a reviewer's sight.
    public static string Untrusted(string? text, Tokenizer tokenizer, int maxTokens)
    {
        var cleaned = ExcessNewlines().Replace(ControlTokens().Replace(text ?? "", ""), "\n\n\n");
        var cut = tokenizer.GetIndexByTokenCount(cleaned, maxTokens, out _, out _);
        return Escape(cut < cleaned.Length ? cleaned[..cut] + "…" : cleaned);
    }

    public static string Attribute(string? text) => Escape(text).Replace("\"", "&quot;");

    private static string Escape(string? text) => (text ?? "").Replace("<", "&lt;").Replace(">", "&gt;");

    [GeneratedRegex(@"<\|[^|>]{1,40}\|>|</?think>", RegexOptions.IgnoreCase)]
    private static partial Regex ControlTokens();

    [GeneratedRegex(@"\n{4,}")]
    private static partial Regex ExcessNewlines();
}
