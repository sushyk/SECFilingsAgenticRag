using System.Text;

namespace SECFilingsAgenticRag.Evals.Reporting;

public sealed class MarkdownReport(string title)
{
    private readonly StringBuilder markdown = new($"# {title}\n\n");

    public MarkdownReport Line(string text = "")
    {
        markdown.AppendLine(text);
        return this;
    }

    public MarkdownReport Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        markdown.AppendLine(Row(headers)).AppendLine(Row([.. headers.Select(_ => "---")]));
        foreach (var row in rows)
            markdown.AppendLine(Row(row));
        markdown.AppendLine();
        return this;
    }

    // The same text goes to the console, so a run under Aspire shows its results in `aspire logs`.
    public async Task SaveAsync(string resultsPath, string kind, CancellationToken ct)
    {
        Directory.CreateDirectory(resultsPath);
        var path = Path.Combine(resultsPath, $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.md");
        await File.WriteAllTextAsync(path, markdown.ToString(), ct);
        Console.WriteLine($"\n{markdown}\nReport written to {path}");
    }

    public static string Number(double value) => value.ToString("0.00");

    private static string Row(IEnumerable<string> cells) => $"| {string.Join(" | ", cells.Select(c => c.Replace("|", "\\|")))} |";
}
