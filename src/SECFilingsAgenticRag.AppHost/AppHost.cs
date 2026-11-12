using System.Net.Http.Json;

var builder = DistributedApplication.CreateBuilder(args);

// Persistent so the container survives AppHost restarts. CREATE EXTENSION runs from AddSecFilingsVectorStore
// because Aspire's database creation script runs at server level.
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg17")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var db = postgres.AddDatabase("secfilings");

// Fixed blob port so CLI checks can use the well-known devstoreaccount1 connection string.
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithDataVolume().WithBlobPort(10000));
var filings = storage.AddBlobContainer("filings");

// Ollama runs natively, not as a container; the endpoint comes from ConnectionStrings:ollama.
var ollama = builder.AddConnectionString("ollama");

// The embedding model is fixed in code (EmbeddingModelConstants); only the chat model is configurable.
var chatModel = builder.AddParameter("chat-model");

// The SEC requires "<Name> <email>" as User-Agent; it is personal data, so it lives in AppHost user-secrets.
// A required parameter would keep ingestion-init from starting until someone fills it in; empty lets the job log the fix instead.
var secUserAgent = builder.AddParameter("sec-user-agent", () => builder.Configuration["Parameters:sec-user-agent"] ?? "", secret: true);
var dataPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "data", "filings"));

// A run-to-completion job: nothing waits on it, indexing is requested by the user.
builder.AddProject<Projects.SECFilingsAgenticRag_IngestionInit>("ingestion-init")
    .WithReference(filings)
    .WaitFor(filings)
    .WithEnvironment("IngestionInit__DataPath", dataPath)
    .WithEnvironment("IngestionInit__Edgar__UserAgent", secUserAgent);

// Nothing indexes automatically: the user requests it with scripts/Invoke-Indexing.ps1 or these commands. HttpClientName is left
// unset, so the command's HttpClient has an infinite timeout and a long forced run still returns its report.
builder.AddAzureFunctionsProject<Projects.SECFilingsAgenticRag_IngestionPipeline_Functions>("indexing")
    .WithHostStorage(storage)
    .WithReference(db).WaitFor(db)
    .WithReference(filings).WaitFor(filings)
    .WithReference(ollama)
    .WithEnvironment("AI__Chat__Model", chatModel)
    .WithHttpCommand("/api/index", "Re-index filings", commandName: "reindex",
        commandOptions: ReindexOptions(force: false, "Index new or changed filings; unchanged ones are skipped"))
    .WithHttpCommand("/api/index", "Force re-index all filings", commandName: "reindex-force",
        commandOptions: ReindexOptions(force: true, "Re-embed every filing", "Re-embed every filing? This takes several minutes on CPU."));

// Waits only for the database: search returns no hits until indexing has run. Host port 5180 comes from launchSettings.json,
// so clients can rely on it.
builder.AddProject<Projects.SECFilingsAgenticRag_AgentServer>("api")
    .WithReference(db).WaitFor(db)
    .WithReference(ollama)
    .WithEnvironment("AI__Chat__Model", chatModel)
    .WithExternalHttpEndpoints();

// Explicit start: an eval run takes minutes to hours on CPU, so it runs only when asked (aspire resource evals-retrieval start).
foreach (var (name, command) in new[] { ("evals-retrieval", "retrieval"), ("evals-answers", "answers") })
    builder.AddProject<Projects.SECFilingsAgenticRag_Evals>(name)
        .WithArgs(command)
        .WithReference(db).WaitFor(db)
        .WithReference(ollama)
        .WithEnvironment("AI__Chat__Model", chatModel)
        .WithExplicitStart();

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
