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
  items: VatReportPolandDetailItem[];
};

export type VatReportPolandDetailItem = {
  productTitle: string;
  productType: string;
  quantity: number;
  unitPrice: number;
  grossAmount: number;
  assignedVatRatePercent: number;
  assignmentReason: string;
};

export type VatReportSummaryRow = {
  type: 'poland' | 'foreign';
  name: string;
  shopifyOrderId: string;
  orderDateUtc?: string | null;
  deliveryName?: string;
  deliveryAddress?: string;
  grossAmount?: number;
  vat: number;
  netAmount?: number;
  polandRows: VatReportPolandDetailRow[];
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
