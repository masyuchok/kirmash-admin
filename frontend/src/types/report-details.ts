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
  shopifyVariantId?: string;
  variantTitle?: string;
  productTitle: string;
  productType: string;
  quantity: number;
  unitPrice: number;
  grossAmount: number;
  assignedVatRatePercent: number;
  assignmentReason: string;
};

export type VatReportCashSaleRow = {
  id: number;
  shopifyProductId: string;
  shopifyVariantId: string;
  productTitle: string;
  quantity: number;
  unitPrice: number;
  grossAmount: number;
  createdAtUtc: string;
};

export type VatReportUnpaidProductRow = {
  shopifyProductId: string;
  shopifyVariantId?: string;
  shopifyVariantTitle?: string;
  shopifyOrderId?: string;
  productTitle: string;
  quantity: number;
  supplierId?: number | null;
  supplierName: string;
  unitSupplyPrice: number;
  estimatedCogs: number;
  saleOrderDateUtc?: string | null;
  isManuallyLinked?: boolean;
  linkedExpenseId?: number | null;
  linkedPaymentLabel?: string;
};

export type VatReportOverpaidExpenseProductOption = {
  expenseProductId: number;
  expenseId: number;
  expenseDateUtc: string;
  invoiceNumber: string;
  comment: string;
  productTitle: string;
  shopifyProductId: string;
  shopifyVariantId?: string;
  shopifyVariantTitle?: string;
  quantity: number;
  overpaidQuantity: number;
};

export type VatReportSupplierExpenseOption = {
  expenseId: number;
  expenseDateUtc: string;
  invoiceNumber: string;
  comment: string;
  expenseInvoiceTypeName: string;
  grossAmount: number;
  totalProductUnits: number;
  hasInvoice: boolean;
};

export type VatReportUnpaidLinkOptions = {
  overpaidProducts: VatReportOverpaidExpenseProductOption[];
  supplierInvoices: VatReportSupplierExpenseOption[];
  supplierPaymentRecords: VatReportSupplierExpenseOption[];
};

export type VatReportSummaryRow = {
  type: 'poland' | 'foreign' | 'expense' | 'cash' | 'unpaid';
  name: string;
  shopifyOrderId: string;
  orderDateUtc?: string | null;
  deliveryName?: string;
  deliveryAddress?: string;
  shippingAddress?: string;
  billingAddress?: string;
  shippingCountryCode?: string;
  billingCountryCode?: string;
  grossAmount?: number;
  vat: number;
  netAmount?: number;
  polandRows: VatReportPolandDetailRow[];
  expenseRows?: VatReportExpenseRow[];
  cashSaleRows?: VatReportCashSaleRow[];
  unpaidProductRows?: VatReportUnpaidProductRow[];
};

export type VatReportExpenseProductRow = {
  id: number;
  shopifyProductId: string;
  shopifyVariantId: string;
  productTitle: string;
  quantity: number;
  unitGrossPrice: number;
};

export type VatReportExpenseRow = {
  id: number;
  grossAmount: number;
  vatAmount: number;
  netAmount: number;
  expenseDateUtc: string;
  comment: string;
  invoiceNumber: string;
  isPaid: boolean;
  isByProsvet: boolean;
  includeVatInTotal: boolean;
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
  isLocked: boolean;
  vat: number;
  profit: number;
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
