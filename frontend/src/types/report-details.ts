export type VatReportPolandDetailRow = {
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
  vat: number;
  polandRows: VatReportPolandDetailRow[];
};

export type VatReportDetails = {
  id: number;
  periodYear: number;
  periodMonth: number;
  vat: number;
  rows: VatReportSummaryRow[];
};
