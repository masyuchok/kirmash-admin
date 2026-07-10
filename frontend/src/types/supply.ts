/** One row in the supplies admin table (matches GET /Supply item). */
export type SupplyListItem = {
  id: string;
  supplierId: number;
  supplierName: string;
  /** ISO date string (YYYY-MM-DD). */
  date: string;
  /** Backend `ProductNumber`. */
  productNumber: number;
  /** Sum of product quantities in this supply. */
  totalQuantity: number;
};

export type SupplyDetailsProduct = {
  shopifyProductId: string;
  shopifyVariantId: string;
  quantity: number;
  supplierPrice: number;
  vatRatePercent: number;
  marginPercent: number;
  salePrice: number;
  syncWithShopify: boolean;
  isReturnFinalized: boolean;
};

export type SupplyDetails = {
  id: number;
  supplierId: number;
  supplierName: string;
  date: string;
  products: SupplyDetailsProduct[];
};
