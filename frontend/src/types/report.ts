export type VatReportPeriod = {
  periodYear: number;
  periodMonth: number;
  totalVat: number;
  profit: number;
  isLocked: boolean;
  primaryReportId: number;
  reports: VatReport[];
};

export type VatReport = {
  id: number;
  periodYear: number;
  periodMonth: number;
  type: 'poland' | 'foreign';
  name: string;
  document: string | null;
  vat: number;
  vatCredit: number;
  vatToPay: number;
  documents: string[];
  shopifyOrderIds: string[];
  isLocked: boolean;
};
