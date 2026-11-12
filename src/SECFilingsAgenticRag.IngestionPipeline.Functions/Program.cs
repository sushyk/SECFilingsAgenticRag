using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Pgvector.Npgsql;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.IngestionPipeline;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("secfilings", configureDataSourceBuilder: dataSource => dataSource.UseVector());
builder.AddAzureBlobContainerClient("filings");
builder.Services.AddSecFilingsAI(builder.Configuration);
builder.Services.AddSecFilingsVectorStore(EmbeddingModelConstants.DocumentEmbeddingKey);
builder.Services.AddSecFilingsIndexing(builder.Configuration);
builder.Build().Run();
