export type SupplierInventoryResult = {
  rows: SupplierInventoryRow[];
  salesSyncedAtUtc: string | null;
};

export type SupplierInventoryRow = {
  supplierId: number;
  supplierName: string;
  shopifyProductId: string;
  productName: string;
  supplierPrice: number;
  quantityInStock: number;
  soldQuantity: number;
  paidQuantity: number;
  quantityToPay: number;
};
