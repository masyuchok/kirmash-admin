import { apiCredentials, getApiBaseUrl, readErrorMessage } from '@/lib/api/common';
import type { VatReport } from '@/types/report';
import type { VatReportDetails, VatReportSourceOrderOption } from '@/types/report-details';

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

function readStringArray(v: unknown): string[] {
  if (!Array.isArray(v)) return [];
  return v.filter((item): item is string => typeof item === 'string' && item.trim().length > 0);
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
      shopifyOrderIds: readStringArray(row.shopifyOrderIds ?? row.ShopifyOrderIds),
    };
  });
}

export async function generateVatReport(
  periodYear: number,
  periodMonth: number,
  type: 'poland' | 'foreign' = 'poland'
): Promise<VatReport> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/generate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({ periodYear, periodMonth, type }),
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
    shopifyOrderIds: readStringArray(row.shopifyOrderIds ?? row.ShopifyOrderIds),
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
            orderDateUtc: (r.orderDateUtc ?? r.OrderDateUtc)
              ? String(r.orderDateUtc ?? r.OrderDateUtc)
              : null,
            deliveryName: String(r.deliveryName ?? r.DeliveryName ?? ''),
            deliveryAddress: String(r.deliveryAddress ?? r.DeliveryAddress ?? ''),
            shippingAddress: String(r.shippingAddress ?? r.ShippingAddress ?? ''),
            billingAddress: String(r.billingAddress ?? r.BillingAddress ?? ''),
            grossAmount: readNumber(r.grossAmount ?? r.GrossAmount),
            vat: readNumber(r.vat ?? r.Vat),
            netAmount: readNumber(r.netAmount ?? r.NetAmount),
            polandRows: Array.isArray(polandRowsRaw)
              ? polandRowsRaw.map((p) => {
                  const d = p as Record<string, unknown>;
                  const detailItemsRaw = d.items ?? d.Items;
                  return {
                    id: readInt(d.id ?? d.Id),
                    orderNumber: String(d.orderNumber ?? d.OrderNumber ?? ''),
                    orderDateUtc: String(d.orderDateUtc ?? d.OrderDateUtc ?? ''),
                    vatRatePercent: readNumber(d.vatRatePercent ?? d.VatRatePercent),
                    grossAmount: readNumber(d.grossAmount ?? d.GrossAmount),
                    vatAmount: readNumber(d.vatAmount ?? d.VatAmount),
                    netAmount: readNumber(d.netAmount ?? d.NetAmount),
                    shippingGrossAmount: readNumber(d.shippingGrossAmount ?? d.ShippingGrossAmount),
                    shippingNetAmount: readNumber(d.shippingNetAmount ?? d.ShippingNetAmount),
                    invoiceFileName: String(d.invoiceFileName ?? d.InvoiceFileName ?? ''),
                    items: Array.isArray(detailItemsRaw)
                      ? detailItemsRaw.map((it) => {
                          const i = it as Record<string, unknown>;
                          return {
                            id: readInt(i.id ?? i.Id),
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

export async function updateVatReportRow(payload: {
  rowId: number;
  vatRatePercent: number;
  grossAmount: number;
  vatAmount: number;
  netAmount: number;
  shippingGrossAmount?: number;
}): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/rows/${payload.rowId}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({
      vatRatePercent: payload.vatRatePercent,
      grossAmount: payload.grossAmount,
      vatAmount: payload.vatAmount,
      netAmount: payload.netAmount,
      ...(typeof payload.shippingGrossAmount === 'number'
        ? { shippingGrossAmount: payload.shippingGrossAmount }
        : {}),
    }),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося захаваць радок справаздачы');
    throw new Error(msg);
  }
}

export async function updateVatReportRowItemVat(payload: {
  itemId: number;
  vatRatePercent: number;
}): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/rows/items/${payload.itemId}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({
      vatRatePercent: payload.vatRatePercent,
    }),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося захаваць VAT па тавары');
    throw new Error(msg);
  }
}

export async function fetchVatReportSourceOrders(reportId: number): Promise<VatReportSourceOrderOption[]> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${reportId}/source-orders`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць спіс замоў');
    throw new Error(msg);
  }

  const data = (await res.json()) as unknown;
  if (!Array.isArray(data)) return [];
  return data.map((item) => {
    const row = item as Record<string, unknown>;
    return {
      shopifyOrderId: String(row.shopifyOrderId ?? row.ShopifyOrderId ?? ''),
      orderNumber: String(row.orderNumber ?? row.OrderNumber ?? ''),
      orderDateUtc: String(row.orderDateUtc ?? row.OrderDateUtc ?? ''),
      vatRatePercent: readNumber(row.vatRatePercent ?? row.VatRatePercent),
      grossAmount: readNumber(row.grossAmount ?? row.GrossAmount),
      vatAmount: readNumber(row.vatAmount ?? row.VatAmount),
      netAmount: readNumber(row.netAmount ?? row.NetAmount),
    };
  });
}

export async function createVatReportRow(
  reportId: number,
  payload: {
    orderNumber: string;
    orderDateUtc: string;
    vatRatePercent: number;
    grossAmount: number;
    vatAmount: number;
    netAmount: number;
  }
): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${reportId}/rows`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося дадаць радок справаздачы');
    throw new Error(msg);
  }
}

export async function deleteVatReportRow(rowId: number): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/rows/${rowId}`, {
    method: 'DELETE',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося выдаліць радок справаздачы');
    throw new Error(msg);
  }
}

export async function moveVatReportRowToForeign(payload: {
  rowId: number;
  deliveryName: string;
  deliveryAddress: string;
}): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/rows/${payload.rowId}/move-to-foreign`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({
      deliveryName: payload.deliveryName,
      deliveryAddress: payload.deliveryAddress,
    }),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося перанесці радок у замежныя');
    throw new Error(msg);
  }
}

export async function regenerateVatReport(id: number): Promise<VatReport> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${id}/regenerate`, {
    method: 'POST',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося перегенераваць справаздачу');
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
    shopifyOrderIds: readStringArray(row.shopifyOrderIds ?? row.ShopifyOrderIds),
  };
}

export async function uploadVatReportRowInvoice(rowId: number, file: File): Promise<void> {
  const formData = new FormData();
  formData.append('file', file);
  const res = await fetch(`${getApiBaseUrl()}/Reports/rows/${rowId}/invoice`, {
    method: 'POST',
    credentials: apiCredentials,
    body: formData,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць фактуру');
    throw new Error(msg);
  }
}

export async function downloadVatReportRowInvoice(rowId: number): Promise<{ blob: Blob; fileName: string }> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/rows/${rowId}/invoice`, {
    method: 'GET',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося атрымаць фактуру');
    throw new Error(msg);
  }
  const blob = await res.blob();
  const contentDisposition = res.headers.get('content-disposition') ?? '';
  const match = /filename\*?=(?:UTF-8'')?\"?([^\";]+)\"?/i.exec(contentDisposition);
  const fileName = match ? decodeURIComponent(match[1]) : `invoice-${rowId}.pdf`;
  return { blob, fileName };
}
