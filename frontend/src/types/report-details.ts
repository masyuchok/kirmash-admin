export type VatReportPolandDetailRow = {
  id: number;
  orderNumber: string;
  orderDateUtc: string;
  vatRatePercent: number;
  grossAmount: number;
  vatAmount: number;
  netAmount: number;
  shippingGrossAmount: number;
  shippingNetAmount: number;
  invoiceFileName: string;
  items: VatReportPolandDetailItem[];
};

export type VatReportPolandDetailItem = {
  id: number;
  productTitle: string;
  productType: string;
  quantity: number;
  unitPrice: number;
  grossAmount: number;
  assignedVatRatePercent: number;
  assignmentReason: string;
};

export type VatReportSummaryRow = {
  type: 'poland' | 'foreign' | 'expense';
  name: string;
  shopifyOrderId: string;
  orderDateUtc?: string | null;
  deliveryName?: string;
  deliveryAddress?: string;
  shippingAddress?: string;
  billingAddress?: string;
  grossAmount?: number;
  vat: number;
  netAmount?: number;
  polandRows: VatReportPolandDetailRow[];
  expenseRows?: VatReportExpenseRow[];
};

export type VatReportExpenseProductRow = {
  id: number;
  shopifyProductId: string;
  productTitle: string;
  quantity: number;
};

export type VatReportExpenseRow = {
  id: number;
  grossAmount: number;
  vatAmount: number;
  netAmount: number;
  expenseDateUtc: string;
  comment: string;
  isPaid: boolean;
  expenseInvoiceTypeId: number;
  expenseInvoiceTypeName: string;
  invoiceFileName: string;
  createdAtUtc: string;
  supplierId?: number | null;
  supplierName: string;
  products: VatReportExpenseProductRow[];
};

export type VatReportDetails = {
  id: number;
  periodYear: number;
  periodMonth: number;
  vat: number;
  rows: VatReportSummaryRow[];
};

export type VatReportSourceOrderOption = {
  shopifyOrderId: string;
  orderNumber: string;
  orderDateUtc: string;
  vatRatePercent: number;
  grossAmount: number;
  vatAmount: number;
  netAmount: number;
};
