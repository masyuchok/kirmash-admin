namespace backend.Services.ImageFetch;

public interface IRemoteImageFetcher
{
    Task<RemoteImageFetchResult> FetchAsync(
        string imageUrl,
        string? pageUrl,
        CancellationToken cancellationToken );
}
