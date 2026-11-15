using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.McpServer;
using SECFilingsAgenticRag.RetrievalPipeline;
using SECFilingsAgenticRag.RetrievalPipeline.Tools;

// Small models send optional arguments as the string "null" (measured in Phase 5), which fails binding. The SDK's generic
// "An error occurred invoking …" made the model answer from memory; this message makes it retry.
const string ArgumentsError = "Error: the arguments do not match the tool schema. Send numbers as JSON numbers and omit optional " +
                              "parameters you have no value for; never send the string \"null\".";

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("secfilings", configureDataSourceBuilder: dataSource => dataSource.UseVector());
builder.Services.AddSecFilingsAI(builder.Configuration);
builder.Services.AddSecFilingsVectorStore(EmbeddingModelConstants.QueryEmbeddingKey);
builder.Services.AddSecFilingsRetrieval(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<FilingTools>();
builder.Services.AddSingleton<FilingResources>();
builder.Services.AddOptions<McpResourceOptions>().BindConfiguration(McpResourceOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddMcpServer(server => server.ServerInstructions = FilingPrompts.Instructions)
    // Stateless is the 2.4 default; it is set because the design depends on it: no session can expire, so an mcp restart never
    // breaks api's connected client, and there is no long-lived GET that the request timeout would cut.
    .WithHttpTransport(transport => transport.Stateless = true)
    .WithResources<FilingResources>()
    .WithPrompts<FilingPrompts>()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        try
        {
            return await next(context, cancellationToken);
        }
        catch (JsonException)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ArgumentsError }] };
        }
    }));
// WithTools only takes ready-made tools, but FilingTools comes from DI. Wrapping its AIFunctions keeps one definition for the
// in-house agent and every external MCP client: the same names, descriptions, schema, argument validation and cite tags.
builder.Services.AddOptions<McpServerOptions>().Configure<FilingTools>((options, tools) =>
{
    options.ToolCollection ??= [];
    foreach (AIFunction tool in tools.AsTools())
        options.ToolCollection.Add(McpServerTool.Create(tool));
});
// Browser-hosted MCP clients (GitHub Copilot in VS Code for the web, Codespaces) call /mcp cross-origin; none are allowed by default.
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks().AddCheck<OllamaHealthCheck>("ollama", tags: ["ready"]);
builder.Services.AddRequestTimeouts();

var app = builder.Build();

// mcp may start before api, which creates the table too; with it, the catalog is empty instead of failing before the first index.
await app.Services.GetRequiredService<VectorStoreCollection<Guid, FilingChunkRecord>>().EnsureCollectionExistsAsync();

app.UseCors();
app.UseRequestTimeouts();
app.MapDefaultEndpoints();
// The same bound as AgentServer's /api/search: a hybrid search with rerank and multi-query makes several model calls.
app.MapMcp("/mcp").WithRequestTimeout(TimeSpan.FromSeconds(120));
app.Run();
