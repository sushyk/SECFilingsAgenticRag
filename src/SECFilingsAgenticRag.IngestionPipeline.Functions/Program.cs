using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Pgvector.Npgsql;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.IngestionPipeline;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("secfilings-vector-db", configureDataSourceBuilder: dataSource => dataSource.UseVector());
builder.AddAzureBlobContainerClient("filings-blob-container");
builder.Services.AddSecFilingsAI(builder.Configuration);
builder.Services.AddSecFilingsVectorStore(EmbeddingModelConstants.DocumentEmbeddingKey);
builder.Services.AddSecFilingsIngestion(builder.Configuration);
builder.Services.AddHealthChecks().AddCheck<OllamaHealthCheck>("ollama", tags: ["ready"]);
builder.Build().Run();
