namespace backend.Models
{
    public class ProductSupplierPriceItem
    {
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public decimal SupplierPrice { get; set; }
        public decimal SalePrice { get; set; }
    }

    public class ProductUnsyncedSupplierItem
    {
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    public class ProductVariantItem
    {
        public string VariantId { get; set; } = string.Empty;
        public string VariantName { get; set; } = string.Empty;
        public int QuantityInStock { get; set; }
    }

    public class ProductOverpaidLineItem
    {
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ShopifyVariantId { get; set; } = string.Empty;
        public string ShopifyVariantTitle { get; set; } = string.Empty;
        public int OverpaidQuantity { get; set; }
    }

    public class ProductWithSuppliersListItem
    {
        public string ShopifyProductId { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ProductAuthor { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public string ProductAdminUrl { get; set; } = string.Empty;
        public string? MainImageUrl { get; set; }
        public int QuantityInStock { get; set; }
        public int ShopifyQuantityInStock { get; set; }
        public bool HasSupplyQuantityOverride { get; set; }
        public string LastSyncedSupplierName { get; set; } = string.Empty;
        public List<string> Suppliers { get; set; } = new();
        public List<ProductUnsyncedSupplierItem> UnsyncedSuppliers { get; set; } = new();
        public List<ProductVariantItem> Variants { get; set; } = new();
        public List<ProductSupplierPriceItem> SupplierPrices { get; set; } = new();
        public List<ProductOverpaidLineItem> OverpaidLines { get; set; } = new();
    }
}
