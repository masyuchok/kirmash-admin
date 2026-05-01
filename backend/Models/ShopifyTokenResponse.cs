namespace backend.Models
{
    public class ShopifyTokenResponse
    {
        public string? access_token { get; set; }
        public string? scope { get; set; }
        public string? associated_user_scope { get; set; }
        public dynamic? associated_user { get; set; }
    }
}
