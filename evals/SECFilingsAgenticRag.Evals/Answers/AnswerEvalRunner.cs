using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using Microsoft.Extensions.Options;
using SECFilingsAgenticRag.AgentServer;
using SECFilingsAgenticRag.AgentServer.Tools;
using SECFilingsAgenticRag.Evals.Dataset;
using SECFilingsAgenticRag.Evals.Reporting;

namespace SECFilingsAgenticRag.Evals.Answers;

public sealed partial class AnswerEvalRunner(
    IServiceProvider services, IConfiguration configuration, RecordingVectorSearch recorder,
    IOptions<EvalOptions> evals, IOptions<JudgeOptions> judgeOptions)
{
    private const string DefaultAgents = $"{AgentNames.StandardRag},{AgentNames.AgenticRag}";
    private const string CitationsCheck = "citations_faithful";

    private sealed record Answer(
        GoldenQuestion Question, AgentResponse Response, double? Relevance, double? Groundedness, double? Completeness,
        int ToolCalls, bool Cited, double Seconds);

    public async Task RunAsync(GoldenDataset dataset, CancellationToken ct)
    {
        var judge = judgeOptions.Value;
        if (judge.Provider == JudgeProvider.Ollama)
            Console.WriteLine("WARNING: Local 4B judges are unreliable; treat these scores as indicative only.");

        var agentNames = (configuration["agent"] ?? DefaultAgents).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var questions = dataset.Questions.Take(configuration.GetValue("limit", int.MaxValue)).ToList();
        var resultsPath = evals.Value.ResultsPath;

        // The built-in disk store feeds `dotnet aieval report`, and its response cache makes re-judging unchanged answers free.
        // Only the judge is cached: the agents' answers are what is under test.
        var reporting = DiskBasedReportingConfiguration.Create(
            storageRootPath: Path.Combine(resultsPath, "store"),
            evaluators: [new RelevanceEvaluator(), new GroundednessEvaluator(), new CompletenessEvaluator()],
            chatConfiguration: new ChatConfiguration(services.GetRequiredKeyedService<IChatClient>(JudgeOptions.ServiceKey)),
            enableResponseCaching: true,
            executionName: $"answers-{DateTime.Now:yyyyMMdd-HHmmss}");

        var report = new MarkdownReport("Answer evaluation")
            .Line($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}")
            .Line($"- Agent chat model: {configuration["AI:Chat:Model"]}; judge: {judge.Provider} {judge.Model}")
            .Line($"- Questions: {questions.Count}")
            .Line();
        List<(string Agent, List<Answer> Answers)> results = [];
        foreach (var name in agentNames)
        {
            var agent = services.GetRequiredKeyedService<AIAgent>(name);
            List<Answer> answers = [];
            foreach (var question in questions)
                answers.Add(await AnswerAsync(name, agent, question, reporting, ct));
            results.Add((name, answers));
        }

        report.Table(["Agent", "Relevance", "Groundedness", "Completeness", "Avg tool calls", "Cited %", "Avg s"],
            results.Select(r => (IReadOnlyList<string>)[
                r.Agent, Score(r.Answers.Average(a => a.Relevance)), Score(r.Answers.Average(a => a.Groundedness)),
                Score(r.Answers.Average(a => a.Completeness)), MarkdownReport.Number(r.Answers.Average(a => a.ToolCalls)),
                $"{r.Answers.Average(a => a.Cited ? 100 : 0):0}", $"{r.Answers.Average(a => a.Seconds):0}"]));
        report.Line("## Per question").Table(["Agent", "Question", "Relevance", "Groundedness", "Completeness", "Tool calls", "Cited", "s"],
            results.SelectMany(r => r.Answers.Select(a => (IReadOnlyList<string>)[
                r.Agent, a.Question.Id, Score(a.Relevance), Score(a.Groundedness), Score(a.Completeness),
                $"{a.ToolCalls}", a.Cited ? "yes" : "no", $"{a.Seconds:0}"])));
        if (results.Find(r => r.Agent == AgentNames.AgenticRag) is { Answers: { } agentic })
            await AppendToolUseAsync(report, services.GetRequiredKeyedService<AIAgent>(AgentNames.AgenticRag), agentic, ct);
        await report.SaveAsync(resultsPath, "answers", ct);
    }

    private async Task<Answer> AnswerAsync(
        string agentName, AIAgent agent, GoldenQuestion question, ReportingConfiguration reporting, CancellationToken ct)
    {
        Console.WriteLine($"[{agentName}] {question.Id}: {question.Question}");
        recorder.TakeRetrieved();
        var started = Stopwatch.GetTimestamp();
        var response = await agent.RunAsync(question.Question, await agent.CreateSessionAsync(ct), cancellationToken: ct);
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var retrieved = recorder.TakeRetrieved();

        await using var run = await reporting.CreateScenarioRunAsync($"{agentName}.{question.Id}", cancellationToken: ct);
        var result = await run.EvaluateAsync(
            [new ChatMessage(ChatRole.User, question.Question)], new ChatResponse(response.Messages),
            additionalContext:
            [
                // Without retrieval there is nothing to be grounded in, which is exactly what the score should show.
                new GroundednessEvaluatorContext(retrieved.Count == 0 ? "(nothing was retrieved)" : string.Join("\n\n", retrieved.Select(r => r.Text))),
                new CompletenessEvaluatorContext(question.ReferenceAnswer),
            ],
            cancellationToken: ct);

        var answer = new Answer(question, response,
            Value(result, RelevanceEvaluator.RelevanceMetricName), Value(result, GroundednessEvaluator.GroundednessMetricName),
            Value(result, CompletenessEvaluator.CompletenessMetricName),
            response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count(),
            Citation().IsMatch(response.Text), seconds);
        Console.WriteLine($"  relevance={Score(answer.Relevance)} groundedness={Score(answer.Groundedness)} " +
                          $"completeness={Score(answer.Completeness)} toolCalls={answer.ToolCalls} {seconds:0} s");
        return answer;
    }

    // MAF's built-in agent evaluation over the answers already produced, so the slow agent runs are not repeated.
    private static async Task AppendToolUseAsync(MarkdownReport report, AIAgent agent, List<Answer> answers, CancellationToken ct)
    {
        var withTools = answers.Where(a => a.Question.ExpectedToolCalls is { Count: > 0 }).ToList();
        if (withTools.Count == 0)
            return;

        var results = await agent.EvaluateAsync(
            withTools.Select(a => a.Response), withTools.Select(a => a.Question.Question),
            new LocalEvaluator(
                EvalChecks.ToolCalledCheck(FilingTools.SearchToolName),
                EvalChecks.ToolCallArgsMatch(),
                FunctionEvaluator.Create(CitationsCheck, CitesOnlyReturnedTags)),
            evalName: "tool-use",
            expectedToolCalls: withTools.Select(a => a.Question.ExpectedToolCalls!.Select(call => call.ToExpected())),
            cancellationToken: ct);

        var checks = results.Items
            .SelectMany((item, i) => item.Metrics.Values.OfType<BooleanMetric>().Select(metric => (metric.Name, metric.Value, withTools[i].Question.Id)))
            .GroupBy(check => check.Name);
        report.Line($"## Tool use ({AgentNames.AgenticRag}): {results.Passed}/{results.Total} answers pass every check").Line()
            .Table(["Check", "Pass rate", "Failing questions"], checks.Select(check => (IReadOnlyList<string>)[
                check.Key, $"{check.Count(c => c.Value == true)}/{check.Count()}",
                string.Join(", ", check.Where(c => c.Value != true).Select(c => c.Id))]));
    }

    // Phase 05 measured small models inventing cite tags; a tag the tools never returned is a fabricated citation.
    private static bool CitesOnlyReturnedTags(EvalItem item)
    {
        var returned = item.Conversation.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .SelectMany(result => ReturnedTag().Matches(result.Result?.ToString() ?? "").Select(match => match.Groups[1].Value))
            .ToHashSet();
        return CitedTag().Matches(item.Response).All(match => returned.Contains(match.Groups[1].Value));
    }

    private static double? Value(EvaluationResult result, string metricName) =>
        result.Metrics.TryGetValue(metricName, out var metric) ? (metric as NumericMetric)?.Value : null;

    private static string Score(double? value) => value is null ? "n/a" : MarkdownReport.Number(value.Value);

    [GeneratedRegex(@"\[(?:\d+|[A-Z]{1,5}-FY\d{4}-\w+)\]")]
    private static partial Regex Citation();

    [GeneratedRegex(@"\[([A-Z]{1,5}-FY\d{4}-\w+)\]")]
    private static partial Regex CitedTag();

    [GeneratedRegex("cite=\"([^\"]+)\"")]
    private static partial Regex ReturnedTag();
}
