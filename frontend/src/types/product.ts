export type ProductWithSuppliers = {
  shopifyProductId: string;
  productName: string;
  productType: string;
  productAdminUrl: string;
  mainImageUrl: string | null;
  quantityInStock: number;
  shopifyQuantityInStock: number;
  hasSupplyQuantityOverride: boolean;
  suppliers: string[];
};
