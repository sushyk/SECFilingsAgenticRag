using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.RetrievalPipeline;

// A citation title is domain wording; MEVD records carry no display name.
public static class FilingCitation
{
    public static string Title(FilingChunkRecord record)
    {
        var filing = $"{record.CompanyName} {record.FormType} FY{record.FiscalYear}";
        return string.IsNullOrWhiteSpace(record.Section) ? filing : $"{filing} — {record.Section}";
    }
}
