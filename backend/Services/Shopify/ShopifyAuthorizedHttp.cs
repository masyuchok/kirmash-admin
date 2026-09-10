using System.Net;
using System.Text;

namespace backend.Services.Shopify;

/// <summary>
/// Sends Shopify Admin REST/GraphQL requests with a process-wide rate limit
/// (~2 calls/sec for basic API clients) and retries on throttle responses.
/// </summary>
internal static class ShopifyAuthorizedHttp
{
    private static readonly SemaphoreSlim Gate = new( 1, 1 );
    private static DateTime _nextAllowedUtc = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds( 520 );
    private const int MaxAttempts = 6;

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string accessToken,
        HttpMethod method,
        string url,
        HttpContent? content = null )
    {
        byte[]? bodyBytes = null;
        string? mediaType = null;
        if (content is not null)
        {
            bodyBytes = await content.ReadAsByteArrayAsync();
            mediaType = content.Headers.ContentType?.MediaType ?? "application/json";
        }

        HttpResponseMessage? lastResponse = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await WaitForSlotAsync();

            using HttpRequestMessage request = new( method, url );
            request.Headers.Add( "X-Shopify-Access-Token", accessToken );
            if (bodyBytes is not null)
            {
                request.Content = new ByteArrayContent( bodyBytes );
                request.Content.Headers.TryAddWithoutValidation(
                    "Content-Type",
                    mediaType ?? "application/json" );
            }

            lastResponse = await client.SendAsync( request );
            if (lastResponse.IsSuccessStatusCode)
            {
                return lastResponse;
            }

            if (attempt >= MaxAttempts || !IsRateLimited( lastResponse ))
            {
                return lastResponse;
            }

            int delayMs = TryReadRetryAfterMs( lastResponse ) ?? (600 * attempt);
            lastResponse.Dispose();
            lastResponse = null;
            await Task.Delay( delayMs );
        }

        return lastResponse
            ?? throw new InvalidOperationException( "Не ўдалося выканаць запыт да Shopify." );
    }

    private static async Task WaitForSlotAsync()
    {
        await Gate.WaitAsync();
        try
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextAllowedUtc)
            {
                await Task.Delay( _nextAllowedUtc - now );
            }

            _nextAllowedUtc = DateTime.UtcNow + MinInterval;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsRateLimited( HttpResponseMessage response ) =>
        response.StatusCode == (HttpStatusCode)429;

    private static int? TryReadRetryAfterMs( HttpResponseMessage response )
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta
            && delta > TimeSpan.Zero)
        {
            return (int)Math.Clamp( delta.TotalMilliseconds, 250, 10_000 );
        }

        if (response.Headers.TryGetValues( "Retry-After", out IEnumerable<string>? values ))
        {
            string? raw = values.FirstOrDefault();
            if (double.TryParse( raw, out double seconds ) && seconds > 0)
            {
                return (int)Math.Clamp( seconds * 1000, 250, 10_000 );
            }
        }

        return null;
    }
}
