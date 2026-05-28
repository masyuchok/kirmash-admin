namespace backend.Models
{
    public class SupplierInventoryRow
    {
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public decimal SupplierPrice { get; set; }
        public int QuantityInStock { get; set; }
        public int SoldQuantity { get; set; }
        public int PaidQuantity { get; set; }
        public int QuantityToPay { get; set; }
    }
}
