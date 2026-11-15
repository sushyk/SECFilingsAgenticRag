using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.IngestionPipeline;

// MEDI's built-in chunk processors are all LLM enrichers (summary, keywords, classification, sentiment); none of them cleans text.
// Only the cleaned chunk is stored, so every step is conservative: it never drops words, numbers or figures.
public sealed partial class ChunkCleaningProcessor(IOptions<ChunkCleaningOptions> options, ILogger<ChunkCleaningProcessor> logger)
    : IngestionChunkProcessor<string>
{
    private const int MinChunkLength = 20;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex[] InstructionPatterns =
    [
        .. new[]
        {
            @"ignore (all )?(previous|prior|above) instructions",
            @"disregard .{0,40}instructions",
            @"^\s*you are now (an?|the) ",
            @"system prompt",
            @"<\|im_start\|>",
            @"### ?(system|instruction)",
            @"do not tell the user",
        }.Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, MatchTimeout)),
    ];

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparatorRow();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\*(\S(?:[^*]*\S)?)\*")]
    private static partial Regex Italic();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"([.\-_=*~])\1{3,}")]
    private static partial Regex PunctuationRun();

    [GeneratedRegex(@"#{1,6}\s+")]
    private static partial Regex HeadingMarker();

    [GeneratedRegex(@"^\s*(page\s+)?\d{1,3}\s*$|^\s*F-\d{1,3}\s*$|^\s*\[?table of contents\]?.*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageFurniture();

    [GeneratedRegex(@"\bItem \d", RegexOptions.IgnoreCase)]
    private static partial Regex ItemReference();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IAsyncEnumerable<IngestionChunk<string>> chunks, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        // Running headers and footers can only be told apart from content by counting lines across the whole document.
        var source = await chunks.ToListAsync(cancellationToken);
        var bodies = source
            .Select(chunk => chunk.Content.ReplaceLineEndings("\n"))
            .Select(content => (settings.MarkdownArtifacts ? StripMarkdown(content) : content).Split('\n').ToList())
            .ToList();

        var boilerplateLines = settings.Boilerplate ? RemoveBoilerplate(source, bodies, settings) : 0;
        int flagged = 0, excluded = 0;

        for (var i = 0; i < source.Count; i++)
        {
            var content = BlankLines().Replace(string.Join('\n', bodies[i]), "\n\n").Trim();
            if (content.Length < MinChunkLength)
                continue;

            var isInstructionLike = settings.PromptInjection && InstructionPatterns.Any(pattern => pattern.IsMatch(content));
            if (isInstructionLike)
            {
                flagged++;
                if (settings.PromptInjectionMode == InstructionLikeTextMode.Exclude)
                {
                    excluded++;
                    continue;
                }
            }

            var cleaned = new IngestionChunk<string>(content, source[i].Document, source[i].Context);
            foreach (var (key, value) in source[i].Metadata)
                cleaned.Metadata[key] = value;
            cleaned.Metadata[FilingChunkMetadataKeys.ContainsInstructionLikeText] = isInstructionLike;
            yield return cleaned;
        }

        if (source.Count > 0)
            logger.LogInformation("Cleaned {DocumentId} boilerplateLines={BoilerplateLines} flagged={Flagged} excluded={Excluded}",
                source[0].Document.Identifier, boilerplateLines, flagged, excluded);
    }

    private static string StripMarkdown(string content)
    {
        var lines = content.Split('\n')
            .Where(line => !TableSeparatorRow().IsMatch(line))
            .Select(line => line.Contains('|') ? string.Join("; ", line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : line)
            .Select(line => line.StartsWith('#') ? HeadingMarker().Replace(line, "") : line);

        var text = string.Join('\n', lines);
        text = Bold().Replace(text, "$2");
        text = Italic().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        return PunctuationRun().Replace(text, "$1");
    }

    private static int RemoveBoilerplate(List<IngestionChunk<string>> source, List<List<string>> bodies, ChunkCleaningOptions settings)
    {
        // The chunker puts the heading context on the first line of every chunk; that line is structure, not a running header.
        int FirstBodyLine(int chunk) => string.IsNullOrEmpty(source[chunk].Context) ? 0 : 1;
        static bool IsProtected(string line) => line.StartsWith('#') || ItemReference().IsMatch(line);

        var repeated = bodies
            .SelectMany((lines, chunk) => lines.Skip(FirstBodyLine(chunk)).Select(Normalize).Distinct())
            .Where(line => line.Length > 0 && line.Length <= settings.BoilerplateMaxLineLength)
            .CountBy(line => line)
            .Where(count => count.Value >= settings.BoilerplateMinRepeatCount)
            .Select(count => count.Key)
            .ToHashSet();

        var removed = 0;
        for (var chunk = 0; chunk < bodies.Count; chunk++)
        {
            var lines = bodies[chunk];
            for (var line = lines.Count - 1; line >= FirstBodyLine(chunk); line--)
            {
                if (IsProtected(lines[line]) || !(PageFurniture().IsMatch(lines[line]) || repeated.Contains(Normalize(lines[line]))))
                    continue;

                lines.RemoveAt(line);
                removed++;
            }
        }

        return removed;
    }

    // Digits are folded because running footers differ only by page number ("Apple Inc. | 2025 Form 10-K | 7").
    private static string Normalize(string line) => Digits().Replace(Whitespace().Replace(line.Trim(), " ").ToLowerInvariant(), "#");
}
