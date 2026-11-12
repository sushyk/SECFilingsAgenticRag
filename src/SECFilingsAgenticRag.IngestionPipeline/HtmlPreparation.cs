using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Text.RegularExpressions;

namespace SECFilingsAgenticRag.IngestionPipeline;

// Document preparation before chunking: removes inline-XBRL machinery and flattens layout tables, which ReverseMarkdown
// would otherwise turn into giant, mostly empty pipe rows.
internal static partial class HtmlPreparation
{
    private const int MaxCellsPerRow = 12;

    [GeneratedRegex(@"display\s*:\s*none", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenStyle();

    [GeneratedRegex(@"^[\s$%()–—-]*$")]
    private static partial Regex SymbolsOnly();

    [GeneratedRegex(@"[\s ]+")]
    private static partial Regex Whitespace();

    public static string Prepare(string html)
    {
        var document = new HtmlParser().ParseDocument(html);

        foreach (var hidden in document.All.Where(IsNoise).ToList())
            hidden.Remove();

        foreach (var inline in document.All.Where(e => e.LocalName.StartsWith("ix:", StringComparison.Ordinal)).ToList())
            inline.Replace(inline.ChildNodes.ToArray());

        foreach (var text in document.Descendants<IText>())
            text.Data = Whitespace().Replace(text.Data, " ");

        foreach (var table in document.QuerySelectorAll("table").ToList())
            Flatten(table);

        return document.Body?.InnerHtml ?? "";
    }

    private static bool IsNoise(IElement element) =>
        element.LocalName is "script" or "style" or "head" or "ix:header" || HiddenStyle().IsMatch(element.GetAttribute("style") ?? "");

    private static void Flatten(IElement table)
    {
        var document = table.Owner!;
        var rows = table.QuerySelectorAll("tr")
            .Select(row => row.Children
                .Where(cell => cell.LocalName is "td" or "th")
                .Select(cell => Whitespace().Replace(cell.TextContent, " ").Trim())
                .Where(text => !SymbolsOnly().IsMatch(text))
                .ToList())
            .Where(cells => cells.Count > 0)
            .ToList();

        var isLayout = rows.Count <= 1 || rows.All(cells => cells.Count == 1);
        table.Replace(isLayout
            ? rows.Select(cells => NewElement(document, "p", string.Join(' ', cells))).ToArray<INode>()
            : [DataTable(document, rows)]);
    }

    private static IElement DataTable(IDocument document, List<List<string>> rows)
    {
        var table = document.CreateElement("table");
        foreach (var cells in rows)
        {
            var row = document.CreateElement("tr");
            var capped = cells.Count > MaxCellsPerRow
                ? cells.Take(MaxCellsPerRow - 1).Append(string.Join(' ', cells.Skip(MaxCellsPerRow - 1)))
                : cells;
            foreach (var cell in capped)
                row.AppendChild(NewElement(document, "td", cell));
            table.AppendChild(row);
        }

        return table;
    }

    private static IElement NewElement(IDocument document, string name, string text)
    {
        var element = document.CreateElement(name);
        element.TextContent = text;
        return element;
    }
}
