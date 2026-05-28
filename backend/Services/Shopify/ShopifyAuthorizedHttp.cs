namespace backend.Services.Shopify;

internal static class ShopifyAuthorizedHttp
{
    public static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string accessToken,
        HttpMethod method,
        string url,
        HttpContent? content = null )
    {
        HttpRequestMessage request = new( method, url );
        request.Headers.Add( "X-Shopify-Access-Token", accessToken );
        if (content is not null)
        {
            request.Content = content;
        }

        return client.SendAsync( request );
    }
}
