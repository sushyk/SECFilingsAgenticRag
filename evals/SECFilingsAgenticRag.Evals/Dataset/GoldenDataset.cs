using System.Text.Json;
using Microsoft.Agents.AI;
using SECFilingsAgenticRag.Common.VectorStore;

namespace SECFilingsAgenticRag.Evals.Dataset;

public sealed record GoldenDataset(int Version, IReadOnlyList<GoldenQuestion> Questions)
{
    public static async Task<GoldenDataset> LoadAsync(string path, Corpus corpus, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var dataset = await JsonSerializer.DeserializeAsync<GoldenDataset>(file, JsonSerializerOptions.Web, ct)
            ?? throw new InvalidDataException($"{path} is empty.");

        // Chunk keys change on every re-index, so the dataset names documents; a document that is not indexed would turn
        // every question about it into a silent miss.
        var indexed = await corpus.DocumentIdsAsync(ct);
        var unknown = (from q in dataset.Questions from e in q.Expected where !indexed.Contains(e.DocumentId)
                       select $"{q.Id}: {e.DocumentId}").ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"{path} references documents that are not indexed:\n  {string.Join("\n  ", unknown)}");
        if (dataset.Questions.GroupBy(q => q.Id).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new InvalidDataException($"{path} has duplicate question id '{duplicate.Key}'.");
        return dataset;
    }
}

public sealed record GoldenQuestion(
    string Id, string Question, GoldenFilter? Filter, IReadOnlyList<Expectation> Expected, string ReferenceAnswer,
    IReadOnlyList<GoldenToolCall>? ExpectedToolCalls)
{
    public bool IsRelevant(FilingChunkRecord chunk) => Expected.Any(e => e.IsSatisfiedBy(chunk));
}

public sealed record GoldenFilter(string[]? Tickers, string[]? FormTypes, int[]? FiscalYears);

// The relevance rule is deterministic, so retrieval metrics need no judge model.
public sealed record Expectation(string DocumentId, string? SectionPrefix, string[]? MustContainAny)
{
    public bool IsSatisfiedBy(FilingChunkRecord chunk) =>
        chunk.DocumentId == DocumentId
        && (SectionPrefix is null || chunk.Section.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase))
        && (MustContainAny is not { Length: > 0 } || MustContainAny.Any(phrase => chunk.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase)));
}

// String values only: MAF's ToolCallArgsMatch compares expected values with the parsed call arguments, and a JsonElement
// never equals them (measured).
public sealed record GoldenToolCall(string Name, Dictionary<string, string>? Arguments)
{
    public ExpectedToolCall ToExpected() =>
        new(Name, Arguments?.ToDictionary(argument => argument.Key, argument => (object)argument.Value));
}
