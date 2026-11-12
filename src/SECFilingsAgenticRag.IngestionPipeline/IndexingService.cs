using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.Filings;
using SECFilingsAgenticRag.Common.Ingestion;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.IngestionPipeline;

public interface IIndexingService
{
    Task<IndexingReport> IndexAsync(IndexingRequest request, CancellationToken ct = default);
}

internal sealed class IndexingService(
    BlobContainerClient filings,
    VectorStore vectorStore,
    VectorStoreCollection<Guid, FilingChunkRecord> collection,
    IngestionChunker<string> chunker,
    ChunkCleaningProcessor cleaning,
    IEnumerable<IngestionChunkProcessor<string>> extraProcessors,
    IServiceProvider services,
    IConfiguration configuration,
    IOptions<IndexingOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<IndexingService> logger) : IIndexingService
{
    private const string DefaultMediaType = "text/html";
    private const int MaxChunksPerDocument = 100_000;

    public async Task<IndexingReport> IndexAsync(IndexingRequest request, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var results = new List<DocumentIndexingResult>();
        var stagingRoot = Path.Combine(
            options.Value.StagingPath ?? Path.Combine(Path.GetTempPath(), "secfilings-staging"), Guid.NewGuid().ToString("N"));

        try
        {
            var staged = await StageChangedFilingsAsync(request, stagingRoot, results, ct);
            foreach (var group in staged.Values.GroupBy(filing => filing.MediaType))
                await IndexGroupAsync(group.Key, group.ToList(), staged, results, ct);
        }
        finally
        {
            DeleteStagingRoot(stagingRoot);
        }

        return new IndexingReport(startedAt, clock.Elapsed, results);
    }

    private async Task<Dictionary<string, StagedFiling>> StageChangedFilingsAsync(
        IndexingRequest request, string stagingRoot, List<DocumentIndexingResult> results, CancellationToken ct)
    {
        var staged = new Dictionary<string, StagedFiling>();
        foreach (var blob in await ListBlobsAsync(request.Tickers, ct))
        {
            if (!blob.Metadata.TryGetValue(FilingBlobConventions.ContentSha256, out var sourceSha256))
            {
                logger.LogWarning("Skipping blob {Blob}: it has no {Metadata} metadata", blob.Name, FilingBlobConventions.ContentSha256);
                continue;
            }

            try
            {
                var metadata = FilingBlobConventions.FromBlobMetadata(blob.Metadata);
                var fingerprint = IndexingFingerprint.Compute(sourceSha256, configuration);
                if (!request.Force && await IsUpToDateAsync(metadata.DocumentId, fingerprint, ct))
                {
                    results.Add(new(metadata.DocumentId, DocumentIndexingStatus.Skipped, 0, TimeSpan.Zero, null));
                    continue;
                }

                var file = new FileInfo(Path.Combine(stagingRoot, FilingBlobConventions.BlobName(metadata, Path.GetExtension(blob.Name))));
                file.Directory!.Create();
                await filings.GetBlobClient(blob.Name).DownloadToAsync(file.FullName, ct);
                staged[metadata.DocumentId] = new StagedFiling(metadata, fingerprint, file, blob.Properties.ContentType ?? DefaultMediaType);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Staging {Blob} failed", blob.Name);
                results.Add(Failed(Path.ChangeExtension(blob.Name, null), ex.Message));
            }
        }

        return staged;
    }

    private async Task IndexGroupAsync(
        string mediaType, List<StagedFiling> group, Dictionary<string, StagedFiling> staged,
        List<DocumentIndexingResult> results, CancellationToken ct)
    {
        var reader = services.GetKeyedService<IngestionDocumentReader>(mediaType);
        if (reader is null)
        {
            results.AddRange(group.Select(filing => Failed(filing.Metadata.DocumentId, $"No reader is registered for media type '{mediaType}'")));
            return;
        }

        try
        {
            var metadataProcessor = new FilingMetadataProcessor(staged);
            var writer = new VectorStoreWriter<string>(vectorStore, EmbeddingModelConstants.Dimensions, new VectorStoreWriterOptions
            {
                CollectionName = FilingChunkRecord.CollectionName,
                DistanceFunction = DistanceFunction.CosineSimilarity,
                IndexKind = IndexKind.Hnsw,
                IncrementalIngestion = true,
            });

            // The pipeline disposes its writer, so each group gets its own.
            using var pipeline = new IngestionPipeline<string>(reader, chunker, writer, loggerFactory: loggerFactory);
            foreach (var processor in extraProcessors.Append(cleaning).Append(metadataProcessor))
                pipeline.ChunkProcessors.Add(processor);

            var clock = Stopwatch.StartNew();
            await foreach (var result in pipeline.ProcessAsync(group.Select(filing => filing.File), ct))
            {
                var documentId = StagedFiling.DocumentIdOf(result.DocumentId);
                var chunkCount = metadataProcessor.ChunkCounts.GetValueOrDefault(documentId);
                results.Add(result.Exception is not null ? Failed(documentId, result.Exception.Message)
                    : chunkCount == 0 ? Failed(documentId, "no chunks produced")
                    : new(documentId, DocumentIndexingStatus.Indexed, chunkCount, clock.Elapsed, null));
                logger.LogInformation("{Status} {DocumentId} chunks={ChunkCount} in {Duration}",
                    results[^1].Status, documentId, chunkCount, clock.Elapsed);
                clock.Restart();
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Indexing {MediaType} filings failed", mediaType);
            results.AddRange(group
                .Where(filing => results.All(r => r.DocumentId != filing.Metadata.DocumentId))
                .Select(filing => Failed(filing.Metadata.DocumentId, ex.Message)));
        }
    }

    private async Task<List<BlobItem>> ListBlobsAsync(IReadOnlyList<string>? tickers, CancellationToken ct)
    {
        var prefixes = tickers is { Count: > 0 } ? tickers.Select(ticker => $"{ticker.ToUpperInvariant()}/") : [""];
        var blobs = new List<BlobItem>();
        try
        {
            foreach (var prefix in prefixes)
                await foreach (var blob in filings.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix, ct))
                    blobs.Add(blob);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            logger.LogWarning("The '{Container}' container does not exist yet; has ingestion-init run?", filings.Name);
        }

        return blobs;
    }

    // A crash mid-upsert leaves old chunks plus some new ones; the count check reports that as not up to date, and the
    // next run's incremental ingestion deletes the old and partial chunks after writing the full new set.
    private async Task<bool> IsUpToDateAsync(string documentId, string fingerprint, CancellationToken ct)
    {
        if (!await collection.CollectionExistsAsync(ct))
            return false;

        var chunks = await collection.GetAsync(chunk => chunk.DocumentId == documentId, MaxChunksPerDocument, cancellationToken: ct).ToListAsync(ct);
        return chunks.Count > 0 && chunks.All(chunk => chunk.IndexFingerprint == fingerprint && chunk.ChunkCount == chunks.Count);
    }

    private void DeleteStagingRoot(string stagingRoot)
    {
        if (!Directory.Exists(stagingRoot))
            return;

        try
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the staging folder {Path}", stagingRoot);
        }
    }

    private static DocumentIndexingResult Failed(string documentId, string error) =>
        new(documentId, DocumentIndexingStatus.Failed, 0, TimeSpan.Zero, error);
}
