namespace backend.Services.ImageFetch;

public sealed class RemoteImageFetchResult
{
    public bool Success { get; init; }
    public byte[]? Bytes { get; init; }
    public string? MimeType { get; init; }
    public string? FileName { get; init; }
    public int? StatusCode { get; init; }
    public RemoteImageFetchSource Source { get; init; }
    public string? Error { get; init; }
    public string? FinalUrl { get; init; }
    public string? ReasonPhrase { get; init; }
    public long? ContentLengthHeader { get; init; }
    public string? ExceptionType { get; init; }

    /// <summary>Cascade diagnostics (filled by CascadingRemoteImageFetcher).</summary>
    public int? DirectStatus { get; init; }
    public bool? ShouldFallbackToRelay { get; init; }
    public bool? RelayConfigured { get; init; }
    public bool? RelayAttempted { get; init; }
    public int? RelayStatus { get; init; }
    public string? RelayError { get; init; }
    /// <summary>Hostname only — never the full URL with credentials.</summary>
    public string? RelayUrlHost { get; init; }

    public int ByteLength => Bytes?.Length ?? 0;

    public static RemoteImageFetchResult Fail(
        RemoteImageFetchSource source,
        string error,
        int? statusCode = null,
        string? finalUrl = null,
        string? mimeType = null,
        string? reasonPhrase = null,
        long? contentLengthHeader = null,
        string? exceptionType = null ) =>
        new()
        {
            Success = false,
            Source = source,
            Error = error,
            StatusCode = statusCode,
            FinalUrl = finalUrl,
            MimeType = mimeType,
            ReasonPhrase = reasonPhrase,
            ContentLengthHeader = contentLengthHeader,
            ExceptionType = exceptionType,
        };

    public static RemoteImageFetchResult Ok(
        RemoteImageFetchSource source,
        byte[] bytes,
        string mimeType,
        string? fileName,
        int? statusCode,
        string? finalUrl = null,
        long? contentLengthHeader = null ) =>
        new()
        {
            Success = true,
            Source = source,
            Bytes = bytes,
            MimeType = mimeType,
            FileName = fileName,
            StatusCode = statusCode,
            FinalUrl = finalUrl,
            ContentLengthHeader = contentLengthHeader,
        };

    public RemoteImageFetchResult WithCascadeDiagnostics(
        int? directStatus,
        bool shouldFallback,
        bool relayConfigured,
        bool relayAttempted,
        int? relayStatus = null,
        string? relayError = null,
        string? relayUrlHost = null ) =>
        new()
        {
            Success = Success,
            Bytes = Bytes,
            MimeType = MimeType,
            FileName = FileName,
            StatusCode = StatusCode,
            Source = Source,
            Error = Error,
            FinalUrl = FinalUrl,
            ReasonPhrase = ReasonPhrase,
            ContentLengthHeader = ContentLengthHeader,
            ExceptionType = ExceptionType,
            DirectStatus = directStatus,
            ShouldFallbackToRelay = shouldFallback,
            RelayConfigured = relayConfigured,
            RelayAttempted = relayAttempted,
            RelayStatus = relayStatus,
            RelayError = relayError,
            RelayUrlHost = relayUrlHost,
        };
}
