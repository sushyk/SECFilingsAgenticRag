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

_ = (db, ollama, chatModel);

builder.Build().Run();
