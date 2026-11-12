using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Npgsql;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.Evals.Dataset;
using SECFilingsAgenticRag.Evals.Hosting;
using SECFilingsAgenticRag.Evals.Reporting;
using SECFilingsAgenticRag.RetrievalPipeline;

namespace SECFilingsAgenticRag.Evals.Retrieval;

public sealed class RetrievalMatrixRunner(
    IConfiguration configuration, NpgsqlDataSource dataSource, ILoggerFactory loggerFactory, Corpus corpus, IOptions<EvalOptions> evals)
{
    private const string DefaultConfigs = "Vector,Hybrid,Vector+Rerank,Hybrid+Rerank";

    private sealed record Run(GoldenQuestion Question, IReadOnlyList<FilingChunkRecord> Hits, double ElapsedMs);

    public async Task RunAsync(GoldenDataset dataset, CancellationToken ct)
    {
        int[] ks = [.. (configuration["k"] ?? "5,10").Split(',').Select(int.Parse).Order()];
        var names = (configuration["configs"] ?? DefaultConfigs).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        List<(string Name, List<Run> Runs)> results = [];
        foreach (var name in names)
            results.Add((name, await RunConfigurationAsync(name, dataset, Math.Max(ks[^1], RetrievalMetrics.MrrDepth), ct)));

        var (documents, chunks) = await corpus.SizeAsync(ct);
        var retrieval = configuration.GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>() ?? new();
        var chatModel = configuration["AI:Chat:Model"];
        var report = new MarkdownReport("Retrieval evaluation")
            .Line($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}")
            .Line($"- Embedding model: {EmbeddingModelConstants.Name}; rerank model: {retrieval.Rerank.Model ?? chatModel}; " +
                  $"multi-query model: {retrieval.MultiQuery.Model ?? chatModel}")
            .Line($"- Corpus: {documents} documents, {chunks} chunks; {dataset.Questions.Count} questions")
            .Line()
            .Table(
                ["Config", .. ks.Select(k => $"Hit@{k}"), $"MRR@{RetrievalMetrics.MrrDepth}", .. ks.Select(k => $"Recall@{k}"), "p50 ms", "p95 ms"],
                results.Select(r => (IReadOnlyList<string>)[
                    r.Name,
                    .. ks.Select(k => MarkdownReport.Number(r.Runs.Average(run => RetrievalMetrics.HitAt(run.Question, run.Hits, k)))),
                    MarkdownReport.Number(r.Runs.Average(run => RetrievalMetrics.ReciprocalRank(run.Question, run.Hits))),
                    .. ks.Select(k => MarkdownReport.Number(r.Runs.Average(run => RetrievalMetrics.RecallAt(run.Question, run.Hits, k)))),
                    $"{RetrievalMetrics.Percentile(r.Runs.Select(run => run.ElapsedMs), 0.5):0}",
                    $"{RetrievalMetrics.Percentile(r.Runs.Select(run => run.ElapsedMs), 0.95):0}"]))
            .Line($"## Misses (Hit@{ks[^1]} = 0)");
        foreach (var (name, runs) in results)
        {
            var misses = runs.Where(run => RetrievalMetrics.HitAt(run.Question, run.Hits, ks[^1]) == 0).Select(run => run.Question.Id).ToList();
            report.Line($"- {name}: {(misses.Count == 0 ? "none" : string.Join(", ", misses))}");
        }
        await report.SaveAsync(evals.Value.ResultsPath, "retrieval", ct);
    }

    private async Task<List<Run>> RunConfigurationAsync(string name, GoldenDataset dataset, int top, CancellationToken ct)
    {
        var parts = name.Split('+');
        await using var services = EvalServiceFactory.Create(configuration, new Dictionary<string, string?>
        {
            [$"{RetrievalOptions.SectionName}:Strategy"] = parts[0],
            [$"{RetrievalOptions.SectionName}:MultiQuery:Enabled"] = parts.Contains("MultiQuery").ToString(),
            [$"{RetrievalOptions.SectionName}:Rerank:Enabled"] = parts.Contains("Rerank").ToString(),
        }, dataSource, loggerFactory);

        var options = services.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        if (options.Describe() != name)
            throw new InvalidOperationException($"Configuration '{name}' composed '{options.Describe()}'. Use e.g. {DefaultConfigs}.");
        var search = services.GetRequiredService<IVectorSearchable<FilingChunkRecord>>();

        List<Run> runs = [];
        foreach (var question in dataset.Questions)
        {
            var started = Stopwatch.GetTimestamp();
            var hits = await search.SearchFilingsAsync(options, question.Question,
                question.Filter?.Tickers, question.Filter?.FormTypes, question.Filter?.FiscalYears, top, ct);
            runs.Add(new(question, [.. hits.Select(hit => hit.Record)], Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            Console.WriteLine($"[{name}] {runs.Count}/{dataset.Questions.Count} {question.Id} " +
                              $"hit@{top}={RetrievalMetrics.HitAt(question, runs[^1].Hits, top)} {runs[^1].ElapsedMs:0} ms");
        }
        return runs;
    }
}
