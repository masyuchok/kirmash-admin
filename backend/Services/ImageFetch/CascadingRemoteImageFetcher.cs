using Microsoft.Extensions.Options;

namespace backend.Services.ImageFetch;

/// <summary>
/// Tries Direct download first; on 403/429/HTML bot-wall automatically falls back to Relay.
/// Callers see one result; Source indicates which path succeeded.
/// </summary>
public sealed class CascadingRemoteImageFetcher : IRemoteImageFetcher
{
    private readonly DirectRemoteImageFetcher _direct;
    private readonly RelayRemoteImageFetcher _relay;
    private readonly ImageFetchOptions _options;
    private readonly ILogger<CascadingRemoteImageFetcher> _logger;

    public CascadingRemoteImageFetcher(
        DirectRemoteImageFetcher direct,
        RelayRemoteImageFetcher relay,
        IOptions<ImageFetchOptions> options,
        ILogger<CascadingRemoteImageFetcher> logger )
    {
        _direct = direct;
        _relay = relay;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RemoteImageFetchResult> FetchAsync(
        string imageUrl,
        string? pageUrl,
        CancellationToken cancellationToken )
    {
        string? relayHost = TryRelayHost( _options.RelayUrl );
        bool relayConfigured = _relay.IsConfigured;

        RemoteImageFetchResult? direct = null;
        bool shouldFallback = false;

        if (!_options.DisableDirect)
        {
            direct = await _direct.FetchAsync( imageUrl, pageUrl, cancellationToken );
            _logger.LogInformation(
                "image-fetch direct SourceUrl={SourceUrl} Success={Success} Status={Status} ByteLength={ByteLength} MimeType={MimeType} Error={Error}",
                imageUrl,
                direct.Success,
                direct.StatusCode,
                direct.ByteLength,
                direct.MimeType,
                direct.Error );

            if (direct.Success)
            {
                return direct.WithCascadeDiagnostics(
                    directStatus: direct.StatusCode,
                    shouldFallback: false,
                    relayConfigured: relayConfigured,
                    relayAttempted: false,
                    relayUrlHost: relayHost );
            }

            shouldFallback = ShouldFallbackToRelay( direct );
            _logger.LogInformation(
                "image-fetch cascade Direct failed status={Status} ShouldFallbackToRelay={ShouldFallback} RelayConfigured={RelayConfigured} RelayUrlHost={RelayUrlHost}",
                direct.StatusCode,
                shouldFallback,
                relayConfigured,
                relayHost );

            if (!shouldFallback)
            {
                return direct.WithCascadeDiagnostics(
                    directStatus: direct.StatusCode,
                    shouldFallback: false,
                    relayConfigured: relayConfigured,
                    relayAttempted: false,
                    relayUrlHost: relayHost );
            }
        }
        else
        {
            shouldFallback = true;
            _logger.LogInformation(
                "image-fetch direct skipped (DisableDirect=true) SourceUrl={SourceUrl} RelayConfigured={RelayConfigured} RelayUrlHost={RelayUrlHost}",
                imageUrl,
                relayConfigured,
                relayHost );
        }

        if (!relayConfigured)
        {
            _logger.LogWarning(
                "image-fetch relay NOT attempted SourceUrl={SourceUrl} DirectStatus={DirectStatus} ShouldFallbackToRelay=true RelayConfigured=false RelayUrlHost={RelayUrlHost}",
                imageUrl,
                direct?.StatusCode,
                relayHost );

            RemoteImageFetchResult missing = direct ?? RemoteImageFetchResult.Fail(
                RemoteImageFetchSource.Relay,
                "Direct failed and image fetch relay is not configured (IMAGE_FETCH_RELAY_URL / IMAGE_FETCH_RELAY_TOKEN)." );

            // Keep Source=Direct when we never left Direct, but surface cascade diag.
            return missing.WithCascadeDiagnostics(
                directStatus: direct?.StatusCode,
                shouldFallback: true,
                relayConfigured: false,
                relayAttempted: false,
                relayUrlHost: relayHost );
        }

        _logger.LogInformation(
            "image-fetch relay attempt started=true SourceUrl={SourceUrl} RelayUrlHost={RelayUrlHost} DirectStatus={DirectStatus}",
            imageUrl,
            relayHost,
            direct?.StatusCode );

        RemoteImageFetchResult relay = await _relay.FetchAsync( imageUrl, pageUrl, cancellationToken );
        _logger.LogInformation(
            "image-fetch relay SourceUrl={SourceUrl} Success={Success} Status={Status} ByteLength={ByteLength} MimeType={MimeType} Error={Error}",
            imageUrl,
            relay.Success,
            relay.StatusCode,
            relay.ByteLength,
            relay.MimeType,
            relay.Error );

        if (relay.Success)
        {
            return relay.WithCascadeDiagnostics(
                directStatus: direct?.StatusCode,
                shouldFallback: shouldFallback,
                relayConfigured: true,
                relayAttempted: true,
                relayStatus: relay.StatusCode,
                relayError: null,
                relayUrlHost: relayHost );
        }

        string combined =
            $"Direct failed ({direct?.StatusCode?.ToString() ?? "n/a"}: {direct?.Error ?? "skipped"}); "
            + $"Relay failed ({relay.StatusCode?.ToString() ?? "n/a"}: {relay.Error ?? "unknown"}).";

        return RemoteImageFetchResult.Fail(
                RemoteImageFetchSource.Relay,
                combined,
                relay.StatusCode ?? direct?.StatusCode,
                relay.FinalUrl ?? direct?.FinalUrl,
                relay.MimeType ?? direct?.MimeType,
                exceptionType: relay.ExceptionType ?? direct?.ExceptionType )
            .WithCascadeDiagnostics(
                directStatus: direct?.StatusCode,
                shouldFallback: shouldFallback,
                relayConfigured: true,
                relayAttempted: true,
                relayStatus: relay.StatusCode,
                relayError: relay.Error,
                relayUrlHost: relayHost );
    }

    public static bool ShouldFallbackToRelay( RemoteImageFetchResult direct )
    {
        if (direct.Success)
        {
            return false;
        }

        if (direct.StatusCode is 403 or 429)
        {
            return true;
        }

        string err = direct.Error ?? string.Empty;
        if (err.Contains( "text/html", StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        if (err.Contains( "peekLooksHtml=True", StringComparison.OrdinalIgnoreCase )
            || err.Contains( "looks like HTML", StringComparison.OrdinalIgnoreCase )
            || err.Contains( "bot wall", StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        return false;
    }

    private static string? TryRelayHost( string? relayUrl )
    {
        if (string.IsNullOrWhiteSpace( relayUrl ))
        {
            return null;
        }

        if (Uri.TryCreate( relayUrl.Trim(), UriKind.Absolute, out Uri? uri ))
        {
            return uri.Host;
        }

        return "(unparseable)";
    }
}
