using System.Text;
using System.Text.Json;

namespace backend.Services.Shopify;

public class ShopifyGraphqlClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ShopifyGraphqlClient> _logger;

    public ShopifyGraphqlClient( IHttpClientFactory httpClientFactory, ILogger<ShopifyGraphqlClient> logger )
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<JsonDocument> ExecuteAsync(
        string shop,
        string accessToken,
        string query,
        object? variables = null,
        CancellationToken cancellationToken = default )
    {
        (bool success, JsonDocument? document, string? error) = await TryExecuteAsync(
            shop,
            accessToken,
            query,
            variables,
            cancellationToken
        );
        if (!success || document is null)
        {
            throw new InvalidOperationException( error ?? "Shopify GraphQL request failed." );
        }

        return document;
    }

    public async Task<(bool Success, JsonDocument? Document, string? Error)> TryExecuteAsync(
        string shop,
        string accessToken,
        string query,
        object? variables = null,
        CancellationToken cancellationToken = default )
    {
        HttpClient client = _httpClientFactory.CreateClient( "Shopify" );
        string payload = JsonSerializer.Serialize( new { query, variables } );
        using StringContent content = new( payload, Encoding.UTF8, "application/json" );
        using HttpResponseMessage response = await ShopifyAuthorizedHttp.SendAsync(
            client,
            accessToken,
            HttpMethod.Post,
            ShopifyApi.GraphQlUrl( shop ),
            content
        );

        string body = await response.Content.ReadAsStringAsync( cancellationToken );
        if (!response.IsSuccessStatusCode)
        {
            string error = $"Shopify GraphQL HTTP {(int)response.StatusCode}: {(body.Length > 500 ? body[..500] : body)}";
            _logger.LogWarning( "{Error}", error );
            return (false, null, error);
        }

        JsonDocument document = JsonDocument.Parse( body );
        if (document.RootElement.TryGetProperty( "errors", out JsonElement errorsEl ) &&
            errorsEl.ValueKind == JsonValueKind.Array &&
            errorsEl.GetArrayLength() > 0)
        {
            string error = $"Shopify GraphQL errors: {errorsEl}";
            _logger.LogWarning( "{Error}", error );
            document.Dispose();
            return (false, null, error);
        }

        return (true, document, null);
    }
}
