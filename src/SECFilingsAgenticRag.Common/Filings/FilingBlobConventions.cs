using System.Globalization;

namespace SECFilingsAgenticRag.Common.Filings;

public static class FilingBlobConventions
{
    public const string Ticker = "ticker", CompanyName = "companyname", Cik = "cik", FormType = "formtype",
        Accession = "accession", FilingDate = "filingdate", ReportDate = "reportdate", FiscalYear = "fiscalyear",
        PrimaryDocument = "primarydocument", SourceUrl = "sourceurl", ContentSha256 = "contentsha256";

    private const string DateFormat = "yyyy-MM-dd";

    public static string BlobName(FilingMetadata metadata, string extension) => $"{metadata.DocumentId}{extension}";

    // Blob metadata values must be ASCII, and company names are not always.
    public static IDictionary<string, string> ToBlobMetadata(FilingMetadata metadata, string contentSha256) =>
        new Dictionary<string, string>
        {
            [Ticker] = metadata.Ticker,
            [CompanyName] = Uri.EscapeDataString(metadata.CompanyName),
            [Cik] = metadata.Cik,
            [FormType] = metadata.FormType,
            [Accession] = metadata.AccessionNumber,
            [FilingDate] = metadata.FilingDate.ToString(DateFormat, CultureInfo.InvariantCulture),
            [ReportDate] = metadata.ReportDate.ToString(DateFormat, CultureInfo.InvariantCulture),
            [FiscalYear] = metadata.FiscalYear.ToString(CultureInfo.InvariantCulture),
            [PrimaryDocument] = metadata.PrimaryDocument,
            [SourceUrl] = metadata.SourceUrl,
            [ContentSha256] = contentSha256,
        };

    public static FilingMetadata FromBlobMetadata(IDictionary<string, string> metadata) =>
        new(
            metadata[Ticker],
            Uri.UnescapeDataString(metadata[CompanyName]),
            metadata[Cik],
            metadata[FormType],
            metadata[Accession],
            DateOnly.ParseExact(metadata[FilingDate], DateFormat, CultureInfo.InvariantCulture),
            DateOnly.ParseExact(metadata[ReportDate], DateFormat, CultureInfo.InvariantCulture),
            int.Parse(metadata[FiscalYear], CultureInfo.InvariantCulture),
            metadata[PrimaryDocument],
            metadata[SourceUrl]);
}
