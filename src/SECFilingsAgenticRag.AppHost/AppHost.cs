using System.Net.Http.Json;
using Aspire.Hosting.Dotnet;

var builder = DistributedApplication.CreateBuilder(args);

// Persistent so the container survives AppHost restarts. CREATE EXTENSION runs from AddSecFilingsVectorStore
// because Aspire's database creation script runs at server level.
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg17")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var db = postgres.AddDatabase("secfilings-vector-db", databaseName: "secfilings");

// Fixed blob port so CLI checks can use the well-known devstoreaccount1 connection string.
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithDataVolume().WithBlobPort(10000));
var filings = storage.AddBlobContainer("filings-blob-container", blobContainerName: "filings");

// Ollama runs natively, not as a container; the endpoint comes from ConnectionStrings:ollama.
var ollama = builder.AddConnectionString("ollama");

// The single source of model names under Aspire. Indexing and query must embed with the same model, so both get one parameter.
var chatModel = builder.AddParameter("chat-model");
var embeddingModel = builder.AddParameter("embedding-model");

// The SEC requires "<Name> <email>" as User-Agent. It is not a secret, but it is personal, so it lives in AppHost user-secrets
// rather than appsettings.json; until it is set, the dashboard prompts for it.
var secUserAgent = builder.AddParameter("sec-user-agent");
var dataPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "data", "filings"));

// A run-to-completion job: nothing waits on it, indexing is requested by the user.
builder.AddDotnetProject("ingestion-init-job", "../SECFilingsAgenticRag.IngestionInit/SECFilingsAgenticRag.IngestionInit.csproj")
    .WithReference(filings)
    .WaitFor(filings)
    .WithEnvironment("IngestionInit__DataPath", dataPath)
    .WithEnvironment("IngestionInit__Edgar__UserAgent", secUserAgent);

// Nothing indexes automatically: the user requests it with scripts/Invoke-Indexing.ps1 or these commands. HttpClientName is left
// unset, so the command's HttpClient has an infinite timeout and a long forced run still returns its report.
builder.AddAzureFunctionsProject<Projects.SECFilingsAgenticRag_IngestionPipeline_Functions>("ingestion-pipeline")
    .WithHostStorage(storage)
    .WithReference(db).WaitFor(db)
    .WithReference(filings).WaitFor(filings)
    .WithReference(ollama)
    .WithAIModels(chatModel, embeddingModel)
    .WithHttpCommand("/api/index", "Re-index filings", commandName: "reindex",
        commandOptions: ReindexOptions(force: false, "Index new or changed filings; unchanged ones are skipped"))
    .WithHttpCommand("/api/index", "Force re-index all filings", commandName: "reindex-force",
        commandOptions: ReindexOptions(force: true, "Re-embed every filing", "Re-embed every filing? This takes several minutes on CPU."));

// agentic-rag's tools, and the MCP endpoint for external hosts (VS Code, Claude Desktop, Inspector) on the fixed port 5190 from
// launchSettings.json. It gets the chat model too: multi-query and rerank, when configured, call it from inside a search.
var mcp = builder.AddDotnetProject("filings-mcp-server", "../SECFilingsAgenticRag.McpServer/SECFilingsAgenticRag.McpServer.csproj")
    .WithReference(db).WaitFor(db)
    .WithReference(ollama)
    .WithAIModels(chatModel, embeddingModel)
    .WithExternalHttpEndpoints();

// Waits only for the database: search returns no hits until indexing has run. Host port 5180 comes from launchSettings.json,
// so clients can rely on it. No WaitFor(mcp): agentic-rag connects on its first run, and a missing MCP server only costs it its tools.
builder.AddDotnetProject("agent-server-web-api", "../SECFilingsAgenticRag.AgentServer/SECFilingsAgenticRag.AgentServer.csproj")
    .WithReference(db).WaitFor(db)
    .WithReference(ollama)
    .WithReference(mcp)
    .WithAIModels(chatModel, embeddingModel)
    .WithExternalHttpEndpoints();

// Explicit start: an eval run takes minutes to hours on CPU, so it runs only when asked (aspire resource evals-retrieval start).
IResourceBuilder<DotnetProjectResource> Evals(string name, string command) => builder.AddDotnetProject(name, "../../evals/SECFilingsAgenticRag.Evals/SECFilingsAgenticRag.Evals.csproj")
    .WithArgs(command)
    .WithReference(db).WaitFor(db)
    .WithReference(ollama)
    .WithAIModels(chatModel, embeddingModel)
    .WithExplicitStart();
Evals("evals-retrieval", "retrieval");
// It runs agentic-rag in-process, whose tool calls go to mcp at the fixed port (Mcp:Endpoint in the evals' appsettings.json;
// the evals host has no service discovery).
Evals("evals-answers", "answers").WaitFor(mcp);

builder.Build().Run();

static HttpCommandOptions ReindexOptions(bool force, string description, string? confirmationMessage = null) => new()
{
    Method = HttpMethod.Post,
    Description = description,
    ConfirmationMessage = confirmationMessage,
    ResultMode = HttpCommandResultMode.Json,
    PrepareRequest = context =>
    {
        context.Request.Content = JsonContent.Create(new { force });
        return Task.CompletedTask;
    },
};

static class AIModelParameters
{
    public static IResourceBuilder<T> WithAIModels<T>(this IResourceBuilder<T> resource,
        IResourceBuilder<ParameterResource> chatModel, IResourceBuilder<ParameterResource> embeddingModel) where T : IResourceWithEnvironment =>
        resource.WithEnvironment("AI__Chat__Model", chatModel).WithEnvironment("AI__Embedding__Model", embeddingModel);
}
