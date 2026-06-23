export type ProductHistorySupplyEvent = {
  date: string;
  supplyId: number;
  supplierId: number;
  supplierName: string;
  shopifyVariantId: string;
  variantTitle: string;
  quantity: number;
};

export type ProductHistorySaleEvent = {
  dateUtc: string;
  source: 'order' | 'cash' | string;
  orderNumber: string;
  reportId: number | null;
  shopifyVariantId: string;
  variantTitle: string;
  quantity: number;
};

export type ProductHistoryPaymentEvent = {
  dateUtc: string;
  expenseId: number;
  reportId: number;
  supplierId: number | null;
  supplierName: string;
  invoiceNumber: string;
  shopifyVariantId: string;
  variantTitle: string;
  quantity: number;
};

export type ProductHistory = {
  shopifyProductId: string;
  productName: string;
  supplies: ProductHistorySupplyEvent[];
  sales: ProductHistorySaleEvent[];
  payments: ProductHistoryPaymentEvent[];
};

export type ProductHistoryQuery = {
  shopifyVariantId?: string;
  supplierId?: number;
  variantTitle?: string;
};
