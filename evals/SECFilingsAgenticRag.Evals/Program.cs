using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Npgsql;
using SECFilingsAgenticRag.AgentServer;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.Evals;
using SECFilingsAgenticRag.Evals.Answers;
using SECFilingsAgenticRag.Evals.Dataset;
using SECFilingsAgenticRag.Evals.Retrieval;
using SECFilingsAgenticRag.RetrievalPipeline;

string[] commands = ["dataset-helper", "retrieval", "answers"];
var command = args.FirstOrDefault();
if (!commands.Contains(command))
{
    Console.Error.WriteLine("""
        Usage: dotnet run --project evals/SECFilingsAgenticRag.Evals -- <command> [options]
          dataset-helper [--ticker AAPL]                        sections and sample chunks, to author golden.json
          retrieval [--k 5,10] [--configs Vector,Hybrid,...]    Hit@k / MRR / Recall@k per retrieval configuration
          answers [--agent standard-rag,agentic-rag] [--limit N] answer quality (judge) and agentic-rag tool use
        """);
    return 2;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = typeof(EvalOptions).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ProjectDirectory").Value,
});
builder.Configuration.AddUserSecrets(typeof(EvalOptions).Assembly);

builder.Services.AddOptions<EvalOptions>().BindConfiguration(EvalOptions.SectionName)
    .PostConfigure(options =>
    {
        options.DatasetPath = Path.GetFullPath(options.DatasetPath, builder.Environment.ContentRootPath);
        options.ResultsPath = Path.GetFullPath(options.ResultsPath, builder.Environment.ContentRootPath);
    })
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<JudgeOptions>().BindConfiguration(JudgeOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddKeyedChatClient(JudgeOptions.ServiceKey,
    sp => sp.GetRequiredService<IOptions<JudgeOptions>>().Value.CreateClient(sp, builder.Configuration));

// The evals app is a host, so it owns the connection (libraries never open one).
builder.Services.AddSingleton(_ =>
{
    var dataSource = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("secfilings")
        ?? throw new InvalidOperationException("ConnectionStrings:secfilings is not configured (user-secrets, or run under Aspire)."));
    dataSource.UseVector();
    return dataSource.Build();
});
builder.Services.AddSecFilingsAI(builder.Configuration);
builder.Services.AddSecFilingsVectorStore(EmbeddingModelConstants.QueryEmbeddingKey);
builder.Services.AddSecFilingsRetrieval(builder.Configuration);

// Wraps the composed search after AddSecFilingsRetrieval; the last registration wins, so the agents' context provider and
// tools search through the recorder.
var composed = builder.Services.Last(d => d.ServiceType == typeof(IVectorSearchable<FilingChunkRecord>) && !d.IsKeyedService);
builder.Services.AddSingleton(sp => new RecordingVectorSearch((IVectorSearchable<FilingChunkRecord>)composed.ImplementationFactory!(sp)));
builder.Services.AddSingleton<IVectorSearchable<FilingChunkRecord>>(sp => sp.GetRequiredService<RecordingVectorSearch>());
builder.AddSecFilingsAgents();

builder.Services.AddSingleton<Corpus>().AddSingleton<RetrievalMatrixRunner>().AddSingleton<AnswerEvalRunner>();

using var host = builder.Build();
await host.StartAsync();
var ct = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
var corpus = host.Services.GetRequiredService<Corpus>();

if (command == "dataset-helper")
{
    await corpus.PrintSectionsAsync(builder.Configuration["ticker"], ct);
    return 0;
}

var dataset = await GoldenDataset.LoadAsync(host.Services.GetRequiredService<IOptions<EvalOptions>>().Value.DatasetPath, corpus, ct);
if (command == "retrieval")
    await host.Services.GetRequiredService<RetrievalMatrixRunner>().RunAsync(dataset, ct);
else
    await host.Services.GetRequiredService<AnswerEvalRunner>().RunAsync(dataset, ct);
return 0;
