using System.Net;
using System.Net.Sockets;
using backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace backend.Services.ImageFetch;

/// <summary>
/// SSRF guard: allowlisted hosts from config + supplier Website fields;
/// optionally the product-page host for the current fetch; block loopback / private / link-local.
/// </summary>
public sealed class SupplierImageHostAllowlist
{
    private static readonly TimeSpan SupplierHostsCacheTtl = TimeSpan.FromMinutes( 5 );
    private const string SupplierHostsCacheKey = "image-fetch:supplier-hosts";

    private readonly HashSet<string> _configHosts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SupplierImageHostAllowlist> _logger;

    public SupplierImageHostAllowlist(
        IOptions<ImageFetchOptions> options,
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<SupplierImageHostAllowlist> logger )
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
        IEnumerable<string> configured = options.Value.AllowedHosts ?? [];
        _configHosts = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        foreach (string host in configured)
        {
            AddHostVariants( _configHosts, host );
        }
    }

    public bool IsHostAllowed( string? host, string? pageUrl = null )
    {
        if (string.IsNullOrWhiteSpace( host ))
        {
            return false;
        }

        string normalized = NormalizeHost( host );
        if (normalized.Length == 0)
        {
            return false;
        }

        if (IsSameSiteAsPage( normalized, pageUrl ))
        {
            return true;
        }

        if (_configHosts.Contains( normalized ))
        {
            return true;
        }

        HashSet<string> supplierHosts = GetSupplierHostsCached();
        return supplierHosts.Contains( normalized );
    }

    public bool TryValidateAbsoluteHttpUrl(
        string? url,
        out Uri uri,
        out string error,
        string? pageUrl = null )
    {
        uri = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace( url )
            || !Uri.TryCreate( url.Trim(), UriKind.Absolute, out Uri? parsed )
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "URL is not absolute http(s).";
            return false;
        }

        if (parsed.IsLoopback
            || string.Equals( parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase ))
        {
            error = "Host is loopback/localhost.";
            return false;
        }

        if (!IsHostAllowed( parsed.Host, pageUrl ))
        {
            error = $"Host '{parsed.Host}' is not in the supplier image allowlist.";
            return false;
        }

        if (IPAddress.TryParse( parsed.Host, out IPAddress? literalIp )
            && IsBlockedIp( literalIp ))
        {
            error = "Host resolves to a blocked IP literal.";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// DNS-resolve host and reject private / link-local / loopback answers (SSRF).
    /// </summary>
    public async Task<(bool Ok, string? Error)> ValidateResolvedAddressesAsync(
        Uri uri,
        CancellationToken cancellationToken )
    {
        if (IPAddress.TryParse( uri.Host, out IPAddress? literal ))
        {
            return IsBlockedIp( literal )
                ? (false, "Host is a blocked IP literal.")
                : (true, null);
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync( uri.Host, cancellationToken );
            if (addresses.Length == 0)
            {
                return (false, "Host did not resolve to any address.");
            }

            foreach (IPAddress address in addresses)
            {
                if (IsBlockedIp( address ))
                {
                    _logger.LogWarning(
                        "Blocked image fetch host {Host} resolved to private/link-local {Address}",
                        uri.Host,
                        address );
                    return (false, $"Host resolves to blocked address {address}.");
                }
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return (false, $"DNS resolution failed: {ex.Message}");
        }
    }

    public static bool IsBlockedIp( IPAddress address )
    {
        if (IPAddress.IsLoopback( address ))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 127) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 0) return true;
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
            {
                return true;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                return IsBlockedIp( address.MapToIPv4() );
            }
        }

        return false;
    }

    private HashSet<string> GetSupplierHostsCached()
    {
        if (_cache.TryGetValue( SupplierHostsCacheKey, out HashSet<string>? cached )
            && cached is not null)
        {
            return cached;
        }

        HashSet<string> hosts = LoadSupplierHosts();
        _cache.Set( SupplierHostsCacheKey, hosts, SupplierHostsCacheTtl );
        return hosts;
    }

    private HashSet<string> LoadSupplierHosts()
    {
        HashSet<string> hosts = new( StringComparer.OrdinalIgnoreCase );
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            List<string?> websites = db.Suppliers
                .AsNoTracking()
                .Select( s => s.Website )
                .Where( w => w != null && w != "" )
                .ToList();

            foreach (string? raw in websites)
            {
                AddHostVariants( hosts, ExtractHost( raw ) );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning( ex, "Failed to load supplier Website hosts for image allowlist" );
        }

        return hosts;
    }

    private static bool IsSameSiteAsPage( string imageHost, string? pageUrl )
    {
        string? pageHost = ExtractHost( pageUrl );
        if (string.IsNullOrWhiteSpace( pageHost ))
        {
            return false;
        }

        string imageApex = StripWww( imageHost );
        string pageApex = StripWww( pageHost );
        if (string.Equals( imageApex, pageApex, StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        // Allow CDN-style subdomains of the product page apex (cdn.example.com for example.com).
        return imageHost.EndsWith( "." + pageApex, StringComparison.OrdinalIgnoreCase );
    }

    private static string? ExtractHost( string? raw )
    {
        if (string.IsNullOrWhiteSpace( raw ))
        {
            return null;
        }

        string trimmed = raw.Trim();
        if (Uri.TryCreate( trimmed, UriKind.Absolute, out Uri? absolute )
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return NormalizeHost( absolute.Host );
        }

        if (Uri.TryCreate( "https://" + trimmed.TrimStart( '/' ), UriKind.Absolute, out Uri? coerced )
            && !string.IsNullOrWhiteSpace( coerced.Host )
            && coerced.Host.Contains( '.', StringComparison.Ordinal ))
        {
            return NormalizeHost( coerced.Host );
        }

        return NormalizeHost( trimmed );
    }

    private static void AddHostVariants( HashSet<string> hosts, string? host )
    {
        string normalized = NormalizeHost( host );
        if (normalized.Length == 0 || !normalized.Contains( '.', StringComparison.Ordinal ))
        {
            return;
        }

        hosts.Add( normalized );
        string apex = StripWww( normalized );
        if (apex.Length > 0)
        {
            hosts.Add( apex );
            hosts.Add( "www." + apex );
        }
    }

    private static string NormalizeHost( string? host )
    {
        if (string.IsNullOrWhiteSpace( host ))
        {
            return string.Empty;
        }

        return host.Trim().TrimEnd( '.' ).ToLowerInvariant();
    }

    private static string StripWww( string host )
    {
        const string prefix = "www.";
        return host.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
            ? host[prefix.Length..]
            : host;
    }
}
