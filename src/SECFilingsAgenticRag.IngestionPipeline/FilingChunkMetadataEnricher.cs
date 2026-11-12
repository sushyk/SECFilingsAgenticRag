using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.IngestionPipeline;

// No built-in processor stamps known metadata: IngestionDocument has no metadata bag, so per-filing values must be attached
// to chunks. It runs last and gives every chunk the same keys with non-null values, because VectorStoreWriter derives the
// table schema from the first chunk's metadata types.
public sealed class FilingChunkMetadataEnricher(IReadOnlyDictionary<string, StagedFiling> filingsByDocumentId) : IngestionChunkProcessor<string>
{
    private const int MaxSectionLength = 200;
    private const string NoSection = "(no section)";

    private readonly Dictionary<string, int> _chunkCounts = [];

    public IReadOnlyDictionary<string, int> ChunkCounts => _chunkCounts;

    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IAsyncEnumerable<IngestionChunk<string>> chunks, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // chunkcount needs the document's total, so the whole document is buffered.
        var source = await chunks.ToListAsync(cancellationToken);
        var section = NoSection;

        for (var index = 0; index < source.Count; index++)
        {
            var chunk = source[index];
            var filing = filingsByDocumentId[chunk.Document.Identifier];
            section = SectionOf(chunk) ?? section;

            var stamped = new IngestionChunk<string>(chunk.Content, chunk.Document, chunk.Context);
            foreach (var (key, value) in chunk.Metadata)
                stamped.Metadata[key] = value;

            var metadata = stamped.Metadata;
            metadata[FilingChunkMetadataKeys.Ticker] = filing.Metadata.Ticker;
            metadata[FilingChunkMetadataKeys.CompanyName] = filing.Metadata.CompanyName;
            metadata[FilingChunkMetadataKeys.Cik] = filing.Metadata.Cik;
            metadata[FilingChunkMetadataKeys.FormType] = filing.Metadata.FormType;
            metadata[FilingChunkMetadataKeys.FiscalYear] = filing.Metadata.FiscalYear;
            metadata[FilingChunkMetadataKeys.FilingDate] = filing.Metadata.FilingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            metadata[FilingChunkMetadataKeys.SourceUrl] = filing.Metadata.SourceUrl;
            metadata[FilingChunkMetadataKeys.Section] = section;
            metadata[FilingChunkMetadataKeys.ChunkIndex] = index;
            metadata[FilingChunkMetadataKeys.ChunkCount] = source.Count;
            metadata[FilingChunkMetadataKeys.IndexFingerprint] = filing.IndexFingerprint;
            metadata.TryAdd(FilingChunkMetadataKeys.ContainsInstructionLikeText, false);
            yield return stamped;
        }

        if (source.Count > 0)
            _chunkCounts[source[0].Document.Identifier] = source.Count;
    }

    // HeaderChunker joins the heading path into Context as markdown ("# PART I ## Item 1A. Risk Factors"); the last heading is the section.
    private static string? SectionOf(IngestionChunk<string> chunk)
    {
        var heading = chunk.Context?.Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return heading?[..Math.Min(heading.Length, MaxSectionLength)];
    }
}
