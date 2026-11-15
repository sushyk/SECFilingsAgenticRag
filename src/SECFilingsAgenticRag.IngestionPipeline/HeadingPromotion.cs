using System.Text.RegularExpressions;

namespace SECFilingsAgenticRag.IngestionPipeline;

// 10-K headings are bold text, not <h*> elements, so HeaderChunker would see no structure without promoting them.
internal static partial class HeadingPromotion
{
    private const int MaxLineLength = 140, MaxTitleLength = 120;

    private readonly record struct Heading(string Markdown, bool IsPart, int LastLine);

    [GeneratedRegex(@"^PART\s+[IVX]+\b.{0,80}$", RegexOptions.IgnoreCase)]
    private static partial Regex PartLine();

    [GeneratedRegex(@"^ITEM\s+(\d{1,2}[A-C]?)\s*[.:–—-]\s*(\S.{0,120})$", RegexOptions.IgnoreCase)]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"^ITEM\s+(\d{1,2}[A-C]?)\s*[.:–—-]$", RegexOptions.IgnoreCase)]
    private static partial Regex BareItemLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string Promote(string markdown)
    {
        var lines = markdown.Split('\n');
        var promoted = new List<string>(lines.Length);
        string? part = null, item = null;

        for (var i = 0; i < lines.Length; i++)
        {
            // Some filers repeat "PART I" as a running page header; promoting it again would reset the chunker's heading context on every page.
            if (Match(lines, i) is not { } heading || heading.Markdown.Equals(heading.IsPart ? part : item, StringComparison.OrdinalIgnoreCase))
            {
                promoted.Add(lines[i]);
                continue;
            }

            promoted.Add(heading.Markdown);
            i = heading.LastLine;
            if (heading.IsPart)
                (part, item) = (heading.Markdown, null);
            else
                item = heading.Markdown;
        }

        return string.Join('\n', promoted);
    }

    private static Heading? Match(string[] lines, int index)
    {
        var line = Normalize(lines[index]);
        if (line.StartsWith('|') || line.Length > MaxLineLength)
            return null;

        if (PartLine().IsMatch(line))
            return new($"# {line}", IsPart: true, index);

        if (ItemLine().Match(line) is { Success: true } item)
            return new(ItemHeading(item.Groups[1].Value, item.Groups[2].Value), IsPart: false, index);

        // A bare "ITEM 1A." is followed by its title as a separate paragraph. Without the trailing separator it is a page header.
        if (BareItemLine().Match(line) is { Success: true } bare && NextTitle(lines, index) is var (next, title))
            return new(ItemHeading(bare.Groups[1].Value, title), IsPart: false, next);

        return null;
    }

    private static string ItemHeading(string number, string title) => $"## Item {number.ToUpperInvariant()}. {title}";

    private static (int Index, string Title)? NextTitle(string[] lines, int itemIndex)
    {
        for (var i = itemIndex + 1; i < lines.Length; i++)
        {
            var line = Normalize(lines[i]);
            if (line.Length == 0)
                continue;

            return line.Length <= MaxTitleLength && !line.StartsWith('|') ? (i, line) : null;
        }

        return null;
    }

    private static string Normalize(string line) =>
        Whitespace().Replace(line.Replace("**", "").Replace("__", "").Replace(' ', ' '), " ").Trim();
}
