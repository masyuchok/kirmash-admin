namespace backend.Models
{
    public class SupplierInventoryResponse
    {
        public List<SupplierInventoryRow> Rows { get; set; } = new();
        public DateTime? SalesSyncedAtUtc { get; set; }
        public bool SalesSyncInProgress { get; set; }
    }
}
