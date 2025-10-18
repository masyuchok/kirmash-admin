namespace backend.Models
{
    public class Supplier
    {
        public int? Id { get; set; }
        public string Name { get; set; }
        public string? Phone { get; set; }
        public string? ContactName { get; set; }
        public string? TGContact {  get; set; }
        public string? Instagram { get; set; }
        public string? Email { get; set; }
        public string? Website { get; set; }
        public string? Country { get; set; }
        public string? City { get; set; }
        public string Currency { get; set; }
        public DateOnly? WorkStart { get; set; }
        public bool isVATPayer { get; set; }

        public List<Product> Products { get; set; } = new List<Product>( );
    }
}
