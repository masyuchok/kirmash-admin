export type ProductSupplierPrice = {
  supplierId: number;
  supplierName: string;
  supplierPrice: number;
  salePrice: number;
};

export type ProductUnsyncedSupplier = {
  supplierId: number;
  supplierName: string;
  quantity: number;
};

export type ProductVariant = {
  variantId: string;
  variantName: string;
  quantityInStock: number;
};

export type ProductWithSuppliers = {
  shopifyProductId: string;
  productName: string;
  productAuthor: string;
  productType: string;
  productAdminUrl: string;
  mainImageUrl: string | null;
  quantityInStock: number;
  shopifyQuantityInStock: number;
  hasSupplyQuantityOverride: boolean;
  lastSyncedSupplierName: string;
  suppliers: string[];
  unsyncedSuppliers: ProductUnsyncedSupplier[];
  variants: ProductVariant[];
  supplierPrices: ProductSupplierPrice[];
};
