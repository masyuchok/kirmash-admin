namespace backend.Services.ImageFetch;

public sealed class ImageFetchOptions
{
    public const string SectionName = "ImageFetch";

    /// <summary>Base URL of the image-fetch relay, e.g. https://relay.example.com (POST {base}/fetch).</summary>
    public string? RelayUrl { get; set; }

    /// <summary>Bearer token for the relay. Never log this value.</summary>
    public string? RelayToken { get; set; }

    /// <summary>
    /// Optional extra hosts (e.g. shared CDNs) beyond Supplier.Website and the import page host.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    public int MaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Development/proof only: skip Direct and go straight to Relay.
    /// Production should leave this false.
    /// </summary>
    public bool DisableDirect { get; set; }
}
