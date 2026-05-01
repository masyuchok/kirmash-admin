namespace backend.Models
{
    public class InvoiceSettings
    {
        public int Id { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Website { get; set; } = string.Empty;
        public string Nip { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
        public DateTime UpdatedAtUtc { get; set; }
    }
}
