export type SupplierInventoryResult = {
  rows: SupplierInventoryRow[];
  salesSyncedAtUtc: string | null;
};

export type SupplierInventoryRow = {
  supplierId: number;
  supplierName: string;
  shopifyProductId: string;
  shopifyVariantId: string;
  variantTitle: string;
  productName: string;
  supplierPrice: number;
  vatRatePercent: number;
  grossUnitPrice: number;
  supplierIsVatPayer: boolean;
  hasPriceOverride: boolean;
  receivedQuantity: number;
  quantityInStock: number;
  soldQuantity: number;
  paidQuantity: number;
  quantityToPay: number;
};
