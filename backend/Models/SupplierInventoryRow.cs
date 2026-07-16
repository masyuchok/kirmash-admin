namespace backend.Models
{
    public class SupplierInventoryRow
    {
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string VariantTitle { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ProductAuthor { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public decimal SupplierPrice { get; set; }
        public decimal VatRatePercent { get; set; }
        public decimal GrossUnitPrice { get; set; }
        public bool SupplierIsVatPayer { get; set; }
        public bool HasPriceOverride { get; set; }
        public decimal MarginPercent { get; set; }
        public decimal SalePrice { get; set; }
        public decimal ShopifyPrice { get; set; }
        public int ReceivedQuantity { get; set; }
        public int QuantityInStock { get; set; }
        public int SoldQuantity { get; set; }
        public int PaidQuantity { get; set; }
        public int QuantityToPay { get; set; }
    }
}
