using System.ComponentModel.DataAnnotations;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace SECFilingsAgenticRag.AgentServer.Tools;

public sealed class McpEndpointOptions
{
    public const string SectionName = "Mcp";

    // Under Aspire, service discovery resolves the host "filings-mcp-server" in http://filings-mcp-server/mcp; running AgentServer alone: http://localhost:5190/mcp.
    // Not https+http://: the MCP transport accepts only an http or https scheme.
    [Required]
    public Uri? Endpoint { get; set; }

    // Bounds one tool call: a reranked or multi-query search on CPU makes several model calls.
    [Range(5, 3600)]
    public int TimeoutSeconds { get; set; } = 120;
}

// Custom (no built-in fits): no built-in context provider supplies tools from an MCP server, and McpClient setup is async while
// the AddAIAgent factory is not. Connecting on the first run, not at startup, keeps the agent server starting while the MCP server is down.
public sealed class McpToolsProvider(
    IHttpClientFactory httpClients, IOptions<McpEndpointOptions> options, ILoggerFactory loggerFactory, ILogger<McpToolsProvider> logger)
    : AIContextProvider, IAsyncDisposable
{
    public const string HttpClientName = "mcp";

    // Both when the server can't be reached to list the tools and when a call fails: without it, small models answer from memory.
    public const string Unavailable =
        "Error: the filing search service (MCP server) is unreachable. Tell the user you could not search the SEC filings right now " +
        "and suggest trying again shortly. Do not answer from memory.";

    private readonly SemaphoreSlim connecting = new(1, 1);
    private McpClient? client;
    private volatile IList<McpClientTool>? tools;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        try
        {
            return new AIContext { Tools = [.. await GetToolsAsync(cancellationToken)] };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "MCP server {Endpoint} is unreachable; this run has no tools", options.Value.Endpoint);
            return new AIContext { Instructions = Unavailable };
        }
    }

    // tools/list runs once per process: the stateless server sends no list_changed notifications, so a tool added on the
    // server reaches the agent after an api restart. A failed connect is not cached, so the next run tries again.
    private async Task<IList<McpClientTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (tools is { } listed)
            return listed;
        await connecting.WaitAsync(cancellationToken);
        try
        {
            if (tools is { } raced)
                return raced;
            // The server is stateless: no session can expire and nothing is pushed, so no standalone GET stream is needed.
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = options.Value.Endpoint!,
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false,
            }, httpClients.CreateClient(HttpClientName), loggerFactory);
            var connected = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
            try
            {
                tools = await connected.ListToolsAsync(cancellationToken: cancellationToken);
            }
            catch
            {
                await connected.DisposeAsync();
                throw;
            }
            client = connected;
            return tools;
        }
        finally
        {
            connecting.Release();
        }
    }

    public ValueTask DisposeAsync() => client?.DisposeAsync() ?? ValueTask.CompletedTask;
}
