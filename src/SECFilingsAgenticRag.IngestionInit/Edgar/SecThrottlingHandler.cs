using System.Net;

namespace SECFilingsAgenticRag.IngestionInit.Edgar;

// The SEC blocks clients above 10 requests/s, so the standard resilience handler cannot own retries here:
// this handler spaces every attempt, including retries.
public sealed class SecThrottlingHandler(TimeSpan minInterval, TimeProvider? timeProvider = null) : DelegatingHandler
{
    private const int MaxAttempts = 3;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await WaitForSlotAsync(ct);
            var response = await base.SendAsync(request, ct);
            if (attempt == MaxAttempts || response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            {
                return response;
            }

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            response.Dispose();
            await Task.Delay(delay, _time, ct);
        }
    }

    private Task WaitForSlotAsync(CancellationToken ct)
    {
        DateTimeOffset now, start;
        lock (_gate)
        {
            now = _time.GetUtcNow();
            start = now > _nextSlot ? now : _nextSlot;
            _nextSlot = start + minInterval;
        }

        return start > now ? Task.Delay(start - now, _time, ct) : Task.CompletedTask;
    }
}
