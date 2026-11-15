using System.Globalization;
using System.Text.Json;

namespace SECFilingsAgenticRag.IngestionInit.Edgar;

public sealed record CompanyTicker(string Ticker, string Cik10, string Title);

// ReportDate is null when EDGAR leaves it empty; the caller decides the fallback and logs it.
public sealed record RecentFiling(string Form, string AccessionNumber, DateOnly FilingDate, DateOnly? ReportDate, string PrimaryDocument);

// Custom: no SDK for SEC EDGAR exists.
public sealed class EdgarClient(HttpClient http)
{
    public static Uri DocumentUrl(string cik10, string accessionNumber, string primaryDocument) =>
        new($"https://www.sec.gov/Archives/edgar/data/{cik10.TrimStart('0')}/{accessionNumber.Replace("-", "")}/{primaryDocument}");

    public async Task<IReadOnlyDictionary<string, CompanyTicker>> GetCompanyTickersAsync(CancellationToken ct)
    {
        using var json = await GetJsonAsync("https://www.sec.gov/files/company_tickers.json", ct);
        return json.RootElement.EnumerateObject()
            .Select(property => property.Value)
            .Select(company => new CompanyTicker(
                company.GetProperty("ticker").GetString()!.ToUpperInvariant(),
                company.GetProperty("cik_str").GetInt64().ToString("D10"),
                company.GetProperty("title").GetString()!))
            .DistinctBy(company => company.Ticker)
            .ToDictionary(company => company.Ticker);
    }

    // filings.recent is parallel arrays: index i across all of them is one filing.
    public async Task<(string CompanyName, IReadOnlyList<RecentFiling> Filings)> GetSubmissionsAsync(string cik10, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"https://data.sec.gov/submissions/CIK{cik10}.json", ct);
        var recent = json.RootElement.GetProperty("filings").GetProperty("recent");

        string[] Column(string name) => [.. recent.GetProperty(name).EnumerateArray().Select(value => value.GetString() ?? "")];
        var accessions = Column("accessionNumber");
        var filingDates = Column("filingDate");
        var reportDates = Column("reportDate");
        var forms = Column("form");
        var primaryDocuments = Column("primaryDocument");

        var filings = accessions
            .Select((accession, i) => new RecentFiling(
                forms[i],
                accession,
                ParseDate(filingDates[i]),
                reportDates[i] is "" ? null : ParseDate(reportDates[i]),
                primaryDocuments[i]))
            .ToList();
        return (json.RootElement.GetProperty("name").GetString()!, filings);
    }

    // Written to a temp file first so an interrupted download never leaves a truncated filing that looks complete.
    public async Task DownloadToFileAsync(Uri url, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            await using (var source = await http.GetStreamAsync(url, ct))
            await using (var target = File.Create(temp))
            {
                await source.CopyToAsync(target, ct);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(url, ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static DateOnly ParseDate(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);
}
