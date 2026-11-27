using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SECFilingsAgenticRag.IngestionInit;
using SECFilingsAgenticRag.IngestionInit.Edgar;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddAzureBlobContainerClient("filings-blob-container");
builder.Services.AddOptions<IngestionInitOptions>()
    .BindConfiguration(IngestionInitOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental, but the only way to drop the ServiceDefaults handler for this one client.
builder.Services.AddHttpClient<EdgarClient>("edgar", (sp, client) =>
    {
        client.Timeout = TimeSpan.FromMinutes(2);

        // "Name email@x" never passes ProductInfoHeaderValue validation, so the header is added unvalidated.
        var userAgent = sp.GetRequiredService<IOptions<IngestionInitOptions>>().Value.Edgar.UserAgent;
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    .AddHttpMessageHandler(sp => new SecThrottlingHandler(
        TimeSpan.FromSeconds(1.0 / sp.GetRequiredService<IOptions<IngestionInitOptions>>().Value.Edgar.MaxRequestsPerSecond)))
    .RemoveAllResilienceHandlers()
    // Handler rotation would recreate SecThrottlingHandler and reset its request spacing.
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
#pragma warning restore EXTEXP0001

builder.Services.AddSingleton<DownloadFilingsStep>();
builder.Services.AddSingleton<UploadFilingsStep>();

// StartAsync/StopAsync instead of RunAsync: the job must exit so `aspire wait ingestion-init --status down` returns.
using var host = builder.Build();
await host.StartAsync();

var exitCode = 0;
try
{
    await host.Services.GetRequiredService<DownloadFilingsStep>().RunAsync(CancellationToken.None);
    await host.Services.GetRequiredService<UploadFilingsStep>().RunAsync(CancellationToken.None);
}
catch (Exception ex)
{
    host.Services.GetRequiredService<ILogger<Program>>().LogError(ex, "IngestionInit failed");
    exitCode = 1;
}

await host.StopAsync();
return exitCode;
