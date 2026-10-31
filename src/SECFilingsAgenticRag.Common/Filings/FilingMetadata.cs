using System.Text.Json.Serialization;

namespace SECFilingsAgenticRag.Common.Filings;

public sealed record FilingMetadata(
    string Ticker,
    string CompanyName,
    string Cik,
    string FormType,
    string AccessionNumber,
    DateOnly FilingDate,
    DateOnly ReportDate,
    int FiscalYear,
    string PrimaryDocument,
    string SourceUrl)
{
    [JsonIgnore]
    public string DocumentId => $"{Ticker}/{FormType}/{AccessionNumber}";
}
