using SECFilingsAgenticRag.Common.Filings;

namespace SECFilingsAgenticRag.IngestionPipeline;

public sealed record StagedFiling(FilingMetadata Metadata, string IndexFingerprint, FileInfo File, string MediaType)
{
    // MEDI hands readers the staged file's full path as identifier, but documentid must stay stable across runs.
    // Staging uses the blob layout <TICKER>/<FORM>/<accession>.<ext> under a per-run root, so the last three segments carry it.
    public static string DocumentIdOf(string stagedPath)
    {
        var segments = stagedPath.Split('/', '\\');
        return string.Join('/', segments[^3], segments[^2], Path.GetFileNameWithoutExtension(segments[^1]));
    }
}
