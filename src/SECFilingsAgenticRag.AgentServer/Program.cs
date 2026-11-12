using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.VectorData;
using Npgsql;
using SECFilingsAgenticRag.AgentServer;
using SECFilingsAgenticRag.AgentServer.Endpoints;
using SECFilingsAgenticRag.AgentServer.Tools;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.RetrievalPipeline;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("secfilings", configureDataSourceBuilder: dataSource => dataSource.UseVector());
builder.Services.AddSecFilingsAI(builder.Configuration);
builder.Services.AddSecFilingsVectorStore(EmbeddingModelConstants.QueryEmbeddingKey);
builder.Services.AddSecFilingsRetrieval(builder.Configuration);
builder.AddSecFilingsAgents();
builder.Services.AddAGUIServer();
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(FilingSearch.ActivitySourceName));
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
builder.Services.AddOpenApi();

var app = builder.Build();

// The API does not wait for indexing: with the table created here, search answers with no hits until the first run.
await app.Services.GetRequiredService<VectorStoreCollection<Guid, FilingChunkRecord>>().EnsureCollectionExistsAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.MapDefaultEndpoints();
app.MapSearchEndpoints();
app.MapAGUIServer(AgentNames.ChatOnly, $"/agents/{AgentNames.ChatOnly}");
app.MapAGUIServer(AgentNames.StandardRag, $"/agents/{AgentNames.StandardRag}");
app.MapAGUIServer(AgentNames.AgenticRag, $"/agents/{AgentNames.AgenticRag}");
app.MapGet("/api/filings", async (FilingTools tools, CancellationToken ct) => TypedResults.Ok(await tools.ListFilingsAsync(ct)));
app.Run();
