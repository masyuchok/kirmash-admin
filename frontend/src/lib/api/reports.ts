import { apiCredentials, getApiBaseUrl, readErrorMessage } from '@/lib/api/common';
import type { VatReport } from '@/types/report';
import type { VatReportDetails } from '@/types/report-details';

function readInt(v: unknown): number {
  if (typeof v === 'number' && Number.isFinite(v)) return Math.trunc(v);
  if (typeof v === 'string' && v.trim() !== '') {
    const n = Number(v);
    return Number.isFinite(n) ? Math.trunc(n) : 0;
  }
  return 0;
}

function readNumber(v: unknown): number {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '') {
    const n = Number(v);
    return Number.isFinite(n) ? n : 0;
  }
  return 0;
}

export async function fetchVatReports(): Promise<VatReport[]> {
  const res = await fetch(`${getApiBaseUrl()}/Reports`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць справаздачы');
    throw new Error(msg);
  }

  const data = (await res.json()) as unknown;
  if (!Array.isArray(data)) {
    throw new Error('Некарэктны адказ сервера');
  }

  return data.map((item) => {
    const row = item as Record<string, unknown>;
    const docsRaw = row.documents ?? row.Documents;
    return {
      id: readInt(row.id ?? row.Id),
      periodYear: readInt(row.periodYear ?? row.PeriodYear),
      periodMonth: readInt(row.periodMonth ?? row.PeriodMonth),
      type: (String(row.type ?? row.Type ?? 'poland').toLowerCase() === 'foreign' ? 'foreign' : 'poland'),
      name: String(row.name ?? row.Name ?? ''),
      document: (row.document ?? row.Document) ? String(row.document ?? row.Document) : null,
      vat: readNumber(row.vat ?? row.Vat),
      vatCredit: readNumber(row.vatCredit ?? row.VatCredit),
      vatToPay: readNumber(row.vatToPay ?? row.VatToPay),
      documents: Array.isArray(docsRaw)
        ? docsRaw.filter((doc): doc is string => typeof doc === 'string' && doc.trim().length > 0)
        : [],
      shopifyOrderIds: Array.isArray(row.shopifyOrderIds ?? row.ShopifyOrderIds)
        ? (row.shopifyOrderIds ?? row.ShopifyOrderIds).filter(
            (id): id is string => typeof id === 'string' && id.trim().length > 0
          )
        : [],
    };
  });
}

export async function generateVatReport(
  periodYear: number,
  periodMonth: number
): Promise<VatReport> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/generate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({ periodYear, periodMonth, type: 'poland' }),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося згенераваць справаздачу');
    throw new Error(msg);
  }

  const row = (await res.json()) as Record<string, unknown>;
  const docsRaw = row.documents ?? row.Documents;
  return {
    id: readInt(row.id ?? row.Id),
    periodYear: readInt(row.periodYear ?? row.PeriodYear),
    periodMonth: readInt(row.periodMonth ?? row.PeriodMonth),
    type: (String(row.type ?? row.Type ?? 'poland').toLowerCase() === 'foreign' ? 'foreign' : 'poland'),
    name: String(row.name ?? row.Name ?? ''),
    document: (row.document ?? row.Document) ? String(row.document ?? row.Document) : null,
    vat: readNumber(row.vat ?? row.Vat),
    vatCredit: readNumber(row.vatCredit ?? row.VatCredit),
    vatToPay: readNumber(row.vatToPay ?? row.VatToPay),
    documents: Array.isArray(docsRaw)
      ? docsRaw.filter((doc): doc is string => typeof doc === 'string' && doc.trim().length > 0)
      : [],
    shopifyOrderIds: Array.isArray(row.shopifyOrderIds ?? row.ShopifyOrderIds)
      ? (row.shopifyOrderIds ?? row.ShopifyOrderIds).filter(
          (id): id is string => typeof id === 'string' && id.trim().length > 0
        )
      : [],
  };
}

export async function fetchVatReportDetails(id: number): Promise<VatReportDetails> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${id}`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць дэталі справаздачы');
    throw new Error(msg);
  }
  const row = (await res.json()) as Record<string, unknown>;
  const rowsRaw = row.rows ?? row.Rows;
  return {
    id: readInt(row.id ?? row.Id),
    periodYear: readInt(row.periodYear ?? row.PeriodYear),
    periodMonth: readInt(row.periodMonth ?? row.PeriodMonth),
    vat: readNumber(row.vat ?? row.Vat),
    rows: Array.isArray(rowsRaw)
      ? rowsRaw.map((item) => {
          const r = item as Record<string, unknown>;
          const polandRowsRaw = r.polandRows ?? r.PolandRows;
          return {
            type: (String(r.type ?? r.Type ?? 'poland').toLowerCase() === 'foreign' ? 'foreign' : 'poland') as
              | 'poland'
              | 'foreign',
            name: String(r.name ?? r.Name ?? ''),
            shopifyOrderId: String(r.shopifyOrderId ?? r.ShopifyOrderId ?? ''),
            vat: readNumber(r.vat ?? r.Vat),
            polandRows: Array.isArray(polandRowsRaw)
              ? polandRowsRaw.map((p) => {
                  const d = p as Record<string, unknown>;
                  return {
                    orderNumber: String(d.orderNumber ?? d.OrderNumber ?? ''),
                    orderDateUtc: String(d.orderDateUtc ?? d.OrderDateUtc ?? ''),
                    vatRatePercent: readNumber(d.vatRatePercent ?? d.VatRatePercent),
                    grossAmount: readNumber(d.grossAmount ?? d.GrossAmount),
                    vatAmount: readNumber(d.vatAmount ?? d.VatAmount),
                    netAmount: readNumber(d.netAmount ?? d.NetAmount),
                    shippingGrossAmount: readNumber(d.shippingGrossAmount ?? d.ShippingGrossAmount),
                    shippingNetAmount: readNumber(d.shippingNetAmount ?? d.ShippingNetAmount),
                    items: Array.isArray(d.items ?? d.Items)
                      ? (d.items ?? d.Items).map((it) => {
                          const i = it as Record<string, unknown>;
                          return {
                            productTitle: String(i.productTitle ?? i.ProductTitle ?? ''),
                            productType: String(i.productType ?? i.ProductType ?? ''),
                            quantity: readInt(i.quantity ?? i.Quantity),
                            unitPrice: readNumber(i.unitPrice ?? i.UnitPrice),
                            grossAmount: readNumber(i.grossAmount ?? i.GrossAmount),
                            assignedVatRatePercent: readNumber(
                              i.assignedVatRatePercent ?? i.AssignedVatRatePercent
                            ),
                            assignmentReason: String(i.assignmentReason ?? i.AssignmentReason ?? ''),
                          };
                        })
                      : [],
                  };
                })
              : [],
          };
        })
      : [],
  };
}
