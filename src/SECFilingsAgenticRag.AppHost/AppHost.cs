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

_ = (db, filings, ollama, chatModel);

builder.Build().Run();
