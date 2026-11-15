using System.Threading.RateLimiting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Npgsql;
using SECFilingsAgenticRag.AgentServer;
using SECFilingsAgenticRag.AgentServer.Endpoints;
using SECFilingsAgenticRag.Common.AI;
using SECFilingsAgenticRag.Common.VectorStore;
using SECFilingsAgenticRag.RetrievalPipeline;
using SECFilingsAgenticRag.RetrievalPipeline.Tools;

const string ChatPolicy = "chat";
const string SearchPolicy = "search";

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("secfilings", configureDataSourceBuilder: dataSource => dataSource.UseVector());
builder.Services.AddSecFilingsAI(builder.Configuration);
builder.Services.AddSecFilingsVectorStore(EmbeddingModelConstants.QueryEmbeddingKey);
builder.Services.AddSecFilingsRetrieval(builder.Configuration);
builder.AddSecFilingsAgents();
builder.Services.AddAGUIServer();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<FilingTools>();
// A browser-hosted UI (Phase 12) calls /agents/* and /api/* cross-origin; none are allowed by default.
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks().AddCheck<OllamaHealthCheck>("ollama", tags: ["ready"]);

builder.Services.AddOptions<ApiOptions>().BindConfiguration(ApiOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ChatRateLimitOptions>().BindConfiguration(ChatRateLimitOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
// Every chat run costs minutes of CPU, so a single client must not be able to queue more than the window allows.
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<ChatRateLimitOptions>>((limiter, chat) =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(ChatPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = chat.Value.PermitLimit,
            Window = TimeSpan.FromSeconds(chat.Value.WindowSeconds),
            QueueLimit = 0,
        }));
});
builder.Services.AddRequestTimeouts();
builder.Services.AddOptions<RequestTimeoutOptions>().Configure<IOptions<ApiOptions>>((timeouts, api) =>
{
    timeouts.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(30) };
    timeouts.AddPolicy(ChatPolicy, TimeSpan.FromSeconds(api.Value.ChatTimeoutSeconds));
    // A hybrid search with rerank and multi-query makes several model calls.
    timeouts.AddPolicy(SearchPolicy, TimeSpan.FromSeconds(120));
});
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
builder.Services.AddOpenApi();

var app = builder.Build();

// The API does not wait for indexing: with the table created here, search answers with no hits until the first run.
await app.Services.GetRequiredService<VectorStoreCollection<Guid, FilingChunkRecord>>().EnsureCollectionExistsAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.UseRequestTimeouts();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.MapDefaultEndpoints();
app.MapSearchEndpoints().WithRequestTimeout(SearchPolicy);

var requestSizeLimit = new RequestSizeLimitAttribute(app.Services.GetRequiredService<IOptions<ApiOptions>>().Value.MaxRequestBytes);
foreach (var agent in new[] { AgentNames.ChatOnly, AgentNames.StandardRag, AgentNames.AgenticRag })
    app.MapAGUIServer(agent, $"/agents/{agent}")
        .RequireRateLimiting(ChatPolicy)
        .WithRequestTimeout(ChatPolicy)
        .WithMetadata(requestSizeLimit);

app.MapGet("/api/filings", async (FilingTools tools, CancellationToken ct) => TypedResults.Ok(await tools.ListFilingsAsync(ct)));
app.Run();
