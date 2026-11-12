using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;
using SECFilingsAgenticRag.Common.Ingestion;

namespace SECFilingsAgenticRag.IngestionPipeline.Functions;

public sealed class IndexFunction(IIndexingService indexing, IHostApplicationLifetime lifetime)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Anonymous is fine for the local demo; production would use Function keys or Entra authentication.
    [Function("Index")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "index")] HttpRequest request, CancellationToken ct)
    {
        IndexingRequest indexingRequest;
        try
        {
            indexingRequest = request.ContentLength > 0
                ? await JsonSerializer.DeserializeAsync<IndexingRequest>(request.Body, IndexingJson.Options, ct) ?? new()
                : new();
        }
        catch (JsonException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }

        if (!await Gate.WaitAsync(0, ct))
            return new ConflictObjectResult(new { error = "Indexing already running" });

        try
        {
            // A caller that gives up (the dashboard command times out after ~100 s) must not cancel a run that takes minutes,
            // so only host shutdown cancels it.
            var report = await indexing.IndexAsync(indexingRequest, lifetime.ApplicationStopping);
            return new JsonResult(report, IndexingJson.Options);
        }
        finally
        {
            Gate.Release();
        }
    }
}
