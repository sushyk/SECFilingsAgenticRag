using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.RetrievalPipeline;

namespace SECFilingsAgenticRag.AgentServer.Endpoints;

// Wire DTOs, because the HTTP contract must not expose the record's vector and bookkeeping columns.
public sealed record SearchRequest(
    [Required, StringLength(FilingSearch.MaxQueryLength)] string Query,
    [Range(1, RetrievalOptions.TopLimit)] int? Top,
    string[]? Tickers,
    string[]? FormTypes,
    int[]? FiscalYears);

public sealed record SearchHit(
    Guid Key, string DocumentId, string Ticker, string CompanyName, string FormType, int FiscalYear,
    string Section, int ChunkIndex, string Text, double? Score, string CitationTitle, string SourceUrl);

public sealed record SearchResponse(string Query, string Strategy, IReadOnlyList<SearchHit> Hits, TimeSpan Elapsed);

public static class SearchEndpoints
{
    private const int LoggedQueryLength = 80;

    public static RouteHandlerBuilder MapSearchEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/search", SearchAsync).ProducesValidationProblem();

    private static async Task<Ok<SearchResponse>> SearchAsync(
        SearchRequest request, IVectorSearchable<FilingChunkRecord> search, IOptions<RetrievalOptions> retrievalOptions,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var options = retrievalOptions.Value;
        var started = Stopwatch.GetTimestamp();
        var hits = await search.SearchFilingsAsync(
            options, request.Query, request.Tickers, request.FormTypes, request.FiscalYears, request.Top, ct);
        var elapsed = Stopwatch.GetElapsedTime(started);

        loggerFactory.CreateLogger(typeof(SearchEndpoints)).LogInformation(
            "Search {Query} tickers={Tickers} formTypes={FormTypes} fiscalYears={FiscalYears} strategy={Strategy} hits={Hits} elapsedMs={ElapsedMs}",
            request.Query[..Math.Min(LoggedQueryLength, request.Query.Length)], request.Tickers, request.FormTypes, request.FiscalYears,
            options.Describe(), hits.Count, elapsed.TotalMilliseconds);

        return TypedResults.Ok(new SearchResponse(request.Query, options.Describe(), [.. hits.Select(ToHit)], elapsed));
    }

    private static SearchHit ToHit(VectorSearchResult<FilingChunkRecord> hit)
    {
        var r = hit.Record;
        return new(r.Key, r.DocumentId, r.Ticker, r.CompanyName, r.FormType, r.FiscalYear, r.Section, r.ChunkIndex,
            r.Text, hit.Score, FilingCitation.Title(r), r.SourceUrl);
    }
}
