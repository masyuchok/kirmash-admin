export type ProductWithSuppliers = {
  shopifyProductId: string;
  productName: string;
  productType: string;
  productAdminUrl: string;
  mainImageUrl: string | null;
  quantityInStock: number;
  suppliers: string[];
};
