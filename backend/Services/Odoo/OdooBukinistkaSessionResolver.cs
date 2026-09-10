using System.Security.Claims;
using backend.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace backend.Services.Odoo;

/// <summary>
/// Resolves an Odoo RPC session for Bukinistka panel actions.
/// App JWT can outlive Odoo's session_id, so prefer SyncLogin for a fresh Odoo session.
/// </summary>
public sealed class OdooBukinistkaSessionResolver
{
    private const string SyncSessionCacheKey = "odoo:bukinistka:sync-session";
    private static readonly TimeSpan SyncSessionTtl = TimeSpan.FromMinutes( 2 );

    private readonly IConfiguration _config;
    private readonly OdooAuthService _auth;
    private readonly IMemoryCache _cache;

    public OdooBukinistkaSessionResolver(
        IConfiguration config,
        OdooAuthService auth,
        IMemoryCache cache )
    {
        _config = config;
        _auth = auth;
        _cache = cache;
    }

    public async Task<OdooSession> ResolveAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false )
    {
        _ = cancellationToken;

        ClaimsPrincipal? principal = BukinistkaJwtAuthentication.TryValidateCookie( request, _config );
        if (principal is null)
        {
            throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
        }

        string syncLogin = (_config["Odoo:SyncLogin"] ?? string.Empty).Trim();
        string syncPassword = _config["Odoo:SyncPassword"] ?? string.Empty;
        if (!string.IsNullOrWhiteSpace( syncLogin ) && !string.IsNullOrWhiteSpace( syncPassword ))
        {
            if (!forceRefresh
                && _cache.TryGetValue( SyncSessionCacheKey, out OdooSession? cached )
                && cached is not null
                && !string.IsNullOrWhiteSpace( cached.SessionId ))
            {
                return cached;
            }

            OdooSession fresh = await _auth.AuthenticateAsync( syncLogin, syncPassword );
            _cache.Set(
                SyncSessionCacheKey,
                fresh,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = SyncSessionTtl,
                } );
            return fresh;
        }

        if (OdooSessionReader.TryGetFromPrincipal( principal, out OdooSession userSession ))
        {
            return userSession;
        }

        throw new UnauthorizedAccessException( "Няма актыўнай сесіі Bukinistka." );
    }

    public void InvalidateSyncSession() => _cache.Remove( SyncSessionCacheKey );

    /// <summary>
    /// Odoo session via SyncLogin (no Bukinistka JWT). Used from Kirma admin and background jobs.
    /// </summary>
    public async Task<OdooSession> ResolveSyncSessionAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false )
    {
        _ = cancellationToken;

        string syncLogin = (_config["Odoo:SyncLogin"] ?? string.Empty).Trim();
        string syncPassword = _config["Odoo:SyncPassword"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace( syncLogin ) || string.IsNullOrWhiteSpace( syncPassword ))
        {
            throw new InvalidOperationException( "Odoo SyncLogin/SyncPassword не наладжаныя ў канфігу." );
        }

        if (!forceRefresh
            && _cache.TryGetValue( SyncSessionCacheKey, out OdooSession? cached )
            && cached is not null
            && !string.IsNullOrWhiteSpace( cached.SessionId ))
        {
            return cached;
        }

        OdooSession fresh = await _auth.AuthenticateAsync( syncLogin, syncPassword );
        _cache.Set(
            SyncSessionCacheKey,
            fresh,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = SyncSessionTtl,
            } );
        return fresh;
    }

    public static bool IsSessionExpiredMessage( string? message )
    {
        if (string.IsNullOrWhiteSpace( message ))
        {
            return false;
        }

        return message.Contains( "session expired", StringComparison.OrdinalIgnoreCase )
               || message.Contains( "SessionExpired", StringComparison.OrdinalIgnoreCase )
               || message.Contains( "odoo.http.SessionExpiredException", StringComparison.OrdinalIgnoreCase )
               || message.Contains( "SessionExpiredException", StringComparison.OrdinalIgnoreCase )
               || message.Contains( "invalid session", StringComparison.OrdinalIgnoreCase );
    }

    public static string UserFriendlySessionExpiredMessage =>
        "Сесія Odoo скончылася. Паўтарыце дзеянне — сесія абновіцца аўтаматычна.";
}
