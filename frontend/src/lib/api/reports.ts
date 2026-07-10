import { apiCredentials, getApiBaseUrl, readErrorMessage } from '@/lib/api/common';
import { readInt, readNumber, readStringArray } from '@/lib/api/json';
import type { VatReport, VatReportPeriod } from '@/types/report';
import type { VatReportDetails, VatReportSourceOrderOption } from '@/types/report-details';

function mapVatReportListItem(row: Record<string, unknown>): VatReport {
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
    isLocked: Boolean(row.isLocked ?? row.IsLocked),
  };
}

function mapVatReportPeriod(row: Record<string, unknown>): VatReportPeriod {
  const reportsRaw = row.reports ?? row.Reports;
  return {
    periodYear: readInt(row.periodYear ?? row.PeriodYear),
    periodMonth: readInt(row.periodMonth ?? row.PeriodMonth),
    totalVat: readNumber(row.totalVat ?? row.TotalVat),
    profit: readNumber(row.profit ?? row.Profit),
    isLocked: Boolean(row.isLocked ?? row.IsLocked),
    primaryReportId: readInt(row.primaryReportId ?? row.PrimaryReportId),
    reports: Array.isArray(reportsRaw)
      ? reportsRaw.map((item) => mapVatReportListItem(item as Record<string, unknown>))
      : [],
  };
}

export async function fetchVatReportPeriods(): Promise<VatReportPeriod[]> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/periods`, {
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
  return data.map((item) => mapVatReportPeriod(item as Record<string, unknown>));
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

  return data.map((item) => mapVatReportListItem(item as Record<string, unknown>));
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

  return mapVatReportListItem((await res.json()) as Record<string, unknown>);
}

function mapSummaryRows(rowsRaw: unknown): VatReportDetails['rows'] {
  if (!Array.isArray(rowsRaw)) return [];
  return rowsRaw.map((item) => {
          const r = item as Record<string, unknown>;
          const polandRowsRaw = r.polandRows ?? r.PolandRows;
          const expenseRowsRaw = r.expenseRows ?? r.ExpenseRows;
          const cashSaleRowsRaw = r.cashSaleRows ?? r.CashSaleRows;
          const unpaidProductRowsRaw = r.unpaidProductRows ?? r.UnpaidProductRows;
          return {
            type: ((): 'poland' | 'foreign' | 'expense' | 'cash' | 'unpaid' => {
              const normalized = String(r.type ?? r.Type ?? 'poland').toLowerCase();
              if (normalized === 'foreign') return 'foreign';
              if (normalized === 'expense') return 'expense';
              if (normalized === 'cash') return 'cash';
              if (normalized === 'unpaid') return 'unpaid';
              return 'poland';
            })(),
            name: String(r.name ?? r.Name ?? ''),
            shopifyOrderId: String(r.shopifyOrderId ?? r.ShopifyOrderId ?? ''),
            orderDateUtc: (r.orderDateUtc ?? r.OrderDateUtc)
              ? String(r.orderDateUtc ?? r.OrderDateUtc)
              : null,
            deliveryName: String(r.deliveryName ?? r.DeliveryName ?? ''),
            deliveryAddress: String(r.deliveryAddress ?? r.DeliveryAddress ?? ''),
            shippingAddress: String(r.shippingAddress ?? r.ShippingAddress ?? ''),
            billingAddress: String(r.billingAddress ?? r.BillingAddress ?? ''),
            shippingCountryCode: String(r.shippingCountryCode ?? r.ShippingCountryCode ?? ''),
            billingCountryCode: String(r.billingCountryCode ?? r.BillingCountryCode ?? ''),
            grossAmount: readNumber(r.grossAmount ?? r.GrossAmount),
            vat: readNumber(r.vat ?? r.Vat),
            netAmount: readNumber(r.netAmount ?? r.NetAmount),
            expenseRows: Array.isArray(expenseRowsRaw)
              ? expenseRowsRaw.map((x: unknown) => {
                  const e = x as Record<string, unknown>;
                  return {
                    id: readInt(e.id ?? e.Id),
                    grossAmount: readNumber(e.grossAmount ?? e.GrossAmount),
                    vatAmount: readNumber(e.vatAmount ?? e.VatAmount),
                    netAmount: readNumber(e.netAmount ?? e.NetAmount),
                    expenseDateUtc: String(e.expenseDateUtc ?? e.ExpenseDateUtc ?? ''),
                    comment: String(e.comment ?? e.Comment ?? ''),
                    invoiceNumber: String(e.invoiceNumber ?? e.InvoiceNumber ?? ''),
                    isPaid: Boolean(e.isPaid ?? e.IsPaid ?? false),
                    isByProsvet: Boolean(e.isByProsvet ?? e.IsByProsvet ?? false),
                    includeVatInTotal: Boolean(e.includeVatInTotal ?? e.IncludeVatInTotal ?? true),
                    expenseInvoiceTypeId: readInt(e.expenseInvoiceTypeId ?? e.ExpenseInvoiceTypeId),
                    expenseInvoiceTypeName: String(
                      e.expenseInvoiceTypeName ?? e.ExpenseInvoiceTypeName ?? ''
                    ),
                    invoiceFileName: String(e.invoiceFileName ?? e.InvoiceFileName ?? ''),
                    createdAtUtc: String(e.createdAtUtc ?? e.CreatedAtUtc ?? ''),
                    supplierId: (() => {
                      const raw = e.supplierId ?? e.SupplierId;
                      if (raw == null) return null;
                      const n = readInt(raw);
                      return n > 0 ? n : null;
                    })(),
                    supplierName: String(e.supplierName ?? e.SupplierName ?? ''),
                    products: (() => {
                      const productsRaw = e.products ?? e.Products;
                      if (!Array.isArray(productsRaw)) return [];
                      return productsRaw.map((p: unknown) => {
                        const row = p as Record<string, unknown>;
                        return {
                          id: readInt(row.id ?? row.Id),
                          shopifyProductId: String(row.shopifyProductId ?? row.ShopifyProductId ?? ''),
                          shopifyVariantId: String(row.shopifyVariantId ?? row.ShopifyVariantId ?? ''),
                          productTitle: String(row.productTitle ?? row.ProductTitle ?? ''),
                          quantity: readInt(row.quantity ?? row.Quantity),
                          unitGrossPrice: readNumber(row.unitGrossPrice ?? row.UnitGrossPrice),
                        };
                      });
                    })(),
                  };
                })
              : [],
            cashSaleRows: Array.isArray(cashSaleRowsRaw)
              ? cashSaleRowsRaw.map((x: unknown) => {
                  const c = x as Record<string, unknown>;
                  return {
                    id: readInt(c.id ?? c.Id),
                    shopifyProductId: String(c.shopifyProductId ?? c.ShopifyProductId ?? ''),
                    shopifyVariantId: String(c.shopifyVariantId ?? c.ShopifyVariantId ?? ''),
                    productTitle: String(c.productTitle ?? c.ProductTitle ?? ''),
                    quantity: readInt(c.quantity ?? c.Quantity),
                    unitPrice: readNumber(c.unitPrice ?? c.UnitPrice),
                    grossAmount: readNumber(c.grossAmount ?? c.GrossAmount),
                    createdAtUtc: String(c.createdAtUtc ?? c.CreatedAtUtc ?? ''),
                  };
                })
              : [],
            unpaidProductRows: Array.isArray(unpaidProductRowsRaw)
              ? unpaidProductRowsRaw.map((x: unknown) => {
                  const u = x as Record<string, unknown>;
                  return {
                    shopifyProductId: String(u.shopifyProductId ?? u.ShopifyProductId ?? ''),
                    shopifyVariantId: String(u.shopifyVariantId ?? u.ShopifyVariantId ?? ''),
                    shopifyVariantTitle: String(u.shopifyVariantTitle ?? u.ShopifyVariantTitle ?? ''),
                    shopifyOrderId: String(u.shopifyOrderId ?? u.ShopifyOrderId ?? ''),
                    productTitle: String(u.productTitle ?? u.ProductTitle ?? ''),
                    quantity: readInt(u.quantity ?? u.Quantity),
                    supplierId: (() => {
                      const raw = u.supplierId ?? u.SupplierId;
                      if (raw == null) return null;
                      const n = readInt(raw);
                      return n > 0 ? n : null;
                    })(),
                    supplierName: String(u.supplierName ?? u.SupplierName ?? ''),
                    unitSupplyPrice: readNumber(u.unitSupplyPrice ?? u.UnitSupplyPrice),
                    estimatedCogs: readNumber(u.estimatedCogs ?? u.EstimatedCogs),
                    saleOrderDateUtc: (u.saleOrderDateUtc ?? u.SaleOrderDateUtc)
                      ? String(u.saleOrderDateUtc ?? u.SaleOrderDateUtc)
                      : null,
                    isManuallyLinked: Boolean(u.isManuallyLinked ?? u.IsManuallyLinked),
                    linkedExpenseId: (() => {
                      const raw = u.linkedExpenseId ?? u.LinkedExpenseId;
                      if (raw == null) return null;
                      const n = readInt(raw);
                      return n > 0 ? n : null;
                    })(),
                    linkedPaymentLabel: String(u.linkedPaymentLabel ?? u.LinkedPaymentLabel ?? ''),
                  };
                })
              : [],
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
                            shopifyVariantId: String(i.shopifyVariantId ?? i.ShopifyVariantId ?? ''),
                            variantTitle: String(i.variantTitle ?? i.VariantTitle ?? ''),
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
        });
}

function mapVatReportDetails(row: Record<string, unknown>): VatReportDetails {
  const rowsRaw = row.rows ?? row.Rows;
  return {
    id: readInt(row.id ?? row.Id),
    periodYear: readInt(row.periodYear ?? row.PeriodYear),
    periodMonth: readInt(row.periodMonth ?? row.PeriodMonth),
    isLocked: Boolean(row.isLocked ?? row.IsLocked),
    vat: readNumber(row.vat ?? row.Vat),
    profit: readNumber(row.profit ?? row.Profit),
    rows: mapSummaryRows(rowsRaw),
  };
}

export async function setVatReportLocked(reportId: number, locked: boolean): Promise<VatReport[]> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${reportId}/${locked ? 'lock' : 'unlock'}`, {
    method: 'POST',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      locked ? 'Не ўдалося заблакаваць справаздачу' : 'Не ўдалося разблакаваць справаздачу'
    );
    throw new Error(msg);
  }
  const data = (await res.json()) as unknown;
  if (!Array.isArray(data)) {
    throw new Error('Некарэктны адказ сервера');
  }
  return data.map((item) => mapVatReportListItem(item as Record<string, unknown>));
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
  return mapVatReportDetails(row);
}

export async function fetchVatReportCombinedDetails(
  id: number
): Promise<{ details: VatReportDetails; foreignRows: VatReportDetails['rows'] }> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${id}/combined-details`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць злучаную справаздачу');
    throw new Error(msg);
  }
  const payload = (await res.json()) as Record<string, unknown>;
  const detailsRaw = (payload.details ?? payload.Details) as Record<string, unknown>;
  const foreignRowsRaw = payload.foreignRows ?? payload.ForeignRows;
  return {
    details: mapVatReportDetails(detailsRaw),
    foreignRows: mapSummaryRows(foreignRowsRaw),
  };
}

export async function createVatReportCashSale(
  reportId: number,
  payload: {
    shopifyProductId: string;
    shopifyVariantId?: string;
    productTitle: string;
    quantity: number;
    unitPrice: number;
  }
): Promise<number> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${reportId}/cash-sales`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося дадаць наяўную продажу');
    throw new Error(msg);
  }
  const data = (await res.json()) as Record<string, unknown>;
  return readInt(data.id ?? data.Id);
}

export async function deleteVatReportCashSale(cashSaleId: number): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/cash-sales/${cashSaleId}`, {
    method: 'DELETE',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося выдаліць наяўную продажу');
    throw new Error(msg);
  }
}

export async function createVatReportExpense(
  reportId: number,
  payload: {
    grossAmount: number;
    vatAmount: number;
    netAmount: number;
    expenseDateUtc: string;
    comment?: string;
    invoiceNumber?: string;
    isPaid: boolean;
    isByProsvet: boolean;
    includeVatInTotal: boolean;
    expenseInvoiceTypeId: number;
    supplierId?: number;
    products?: Array<{
      shopifyProductId: string;
      shopifyVariantId?: string;
      productTitle: string;
      quantity: number;
      unitGrossPrice: number;
      vatRatePercent: number;
    }>;
  }
): Promise<number> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${reportId}/expenses`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося дадаць расход');
    throw new Error(msg);
  }
  const data = (await res.json()) as Record<string, unknown>;
  return readInt(data.id ?? data.Id);
}

export async function updateVatReportExpense(
  expenseId: number,
  payload: {
    grossAmount: number;
    vatAmount: number;
    netAmount: number;
    expenseDateUtc: string;
    comment?: string;
    invoiceNumber?: string;
    isPaid: boolean;
    isByProsvet: boolean;
    includeVatInTotal: boolean;
    expenseInvoiceTypeId: number;
    supplierId?: number;
    products?: Array<{
      shopifyProductId: string;
      shopifyVariantId?: string;
      productTitle: string;
      quantity: number;
      unitGrossPrice: number;
      vatRatePercent: number;
    }>;
  }
): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/expenses/${expenseId}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося змяніць расход');
    throw new Error(msg);
  }
}

export async function updateVatReportExpensePaid(expenseId: number, isPaid: boolean): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/expenses/${expenseId}/paid`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({ isPaid }),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося змяніць аплату');
    throw new Error(msg);
  }
}

export async function uploadVatReportExpenseInvoice(expenseId: number, file: File): Promise<void> {
  const formData = new FormData();
  formData.append('file', file);
  const res = await fetch(`${getApiBaseUrl()}/Reports/expenses/${expenseId}/invoice`, {
    method: 'POST',
    credentials: apiCredentials,
    body: formData,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць фактуру');
    throw new Error(msg);
  }
}

export async function downloadVatReportExpenseInvoice(
  expenseId: number
): Promise<{ blob: Blob; fileName: string }> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/expenses/${expenseId}/invoice`, {
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
  const fileName = match ? decodeURIComponent(match[1]) : `expense-${expenseId}.pdf`;
  return { blob, fileName };
}

export async function deleteVatReportExpense(expenseId: number): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/expenses/${expenseId}`, {
    method: 'DELETE',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося выдаліць расход');
    throw new Error(msg);
  }
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

export async function createVatReportForeignRow(
  reportId: number,
  payload: {
    orderNumber: string;
    orderDateUtc: string;
    deliveryName: string;
    deliveryAddress: string;
    countryCode: string;
    shippingGrossAmount: number;
    items: Array<{
      shopifyProductId: string;
      shopifyVariantId?: string;
      variantTitle?: string;
      productTitle: string;
      productType?: string;
      quantity: number;
      unitPrice: number;
    }>;
  }
): Promise<string> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${reportId}/foreign-rows`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося дадаць замежны радок');
    throw new Error(msg);
  }
  const data = (await res.json()) as Record<string, unknown>;
  return String(data.shopifyOrderId ?? data.ShopifyOrderId ?? '');
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
    shopifyOrderId?: string;
  }
): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/${reportId}/rows`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({
      orderNumber: payload.orderNumber,
      orderDateUtc: payload.orderDateUtc,
      vatRatePercent: payload.vatRatePercent,
      grossAmount: payload.grossAmount,
      vatAmount: payload.vatAmount,
      netAmount: payload.netAmount,
      shopifyOrderId: payload.shopifyOrderId ?? null,
    }),
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
  return mapVatReportListItem((await res.json()) as Record<string, unknown>);
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

function mapOverpaidOption(row: Record<string, unknown>) {
  return {
    expenseProductId: readInt(row.expenseProductId ?? row.ExpenseProductId),
    expenseId: readInt(row.expenseId ?? row.ExpenseId),
    expenseDateUtc: String(row.expenseDateUtc ?? row.ExpenseDateUtc ?? ''),
    invoiceNumber: String(row.invoiceNumber ?? row.InvoiceNumber ?? ''),
    comment: String(row.comment ?? row.Comment ?? ''),
    productTitle: String(row.productTitle ?? row.ProductTitle ?? ''),
    shopifyProductId: String(row.shopifyProductId ?? row.ShopifyProductId ?? ''),
    shopifyVariantId: String(row.shopifyVariantId ?? row.ShopifyVariantId ?? ''),
    shopifyVariantTitle: String(row.shopifyVariantTitle ?? row.ShopifyVariantTitle ?? ''),
    quantity: readInt(row.quantity ?? row.Quantity),
    overpaidQuantity: readInt(row.overpaidQuantity ?? row.OverpaidQuantity),
  };
}

function mapSupplierPaymentOption(row: Record<string, unknown>) {
  return {
    expenseId: readInt(row.expenseId ?? row.ExpenseId),
    expenseDateUtc: String(row.expenseDateUtc ?? row.ExpenseDateUtc ?? ''),
    invoiceNumber: String(row.invoiceNumber ?? row.InvoiceNumber ?? ''),
    comment: String(row.comment ?? row.Comment ?? ''),
    expenseInvoiceTypeName: String(row.expenseInvoiceTypeName ?? row.ExpenseInvoiceTypeName ?? ''),
    grossAmount: readNumber(row.grossAmount ?? row.GrossAmount),
    totalProductUnits: readInt(row.totalProductUnits ?? row.TotalProductUnits),
    hasInvoice: Boolean(row.hasInvoice ?? row.HasInvoice),
  };
}

export async function fetchUnpaidLinkOptions(params: {
  supplierId: number;
  periodYear: number;
  periodMonth: number;
  shopifyProductId: string;
  shopifyVariantId?: string;
}) {
  const query = new URLSearchParams({
    supplierId: String(params.supplierId),
    periodYear: String(params.periodYear),
    periodMonth: String(params.periodMonth),
    shopifyProductId: params.shopifyProductId,
  });
  if (params.shopifyVariantId?.trim()) {
    query.set('shopifyVariantId', params.shopifyVariantId.trim());
  }
  const res = await fetch(`${getApiBaseUrl()}/Reports/unpaid/link-options?${query.toString()}`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць варыянты прывязкі');
    throw new Error(msg);
  }
  const data = (await res.json()) as Record<string, unknown>;
  const overpaidRaw = data.overpaidProducts ?? data.OverpaidProducts;
  const invoicesRaw = data.supplierInvoices ?? data.SupplierInvoices;
  const paymentsRaw = data.supplierPaymentRecords ?? data.SupplierPaymentRecords;
  return {
    overpaidProducts: Array.isArray(overpaidRaw)
      ? overpaidRaw.map((row) => mapOverpaidOption(row as Record<string, unknown>))
      : [],
    supplierInvoices: Array.isArray(invoicesRaw)
      ? invoicesRaw.map((row) => mapSupplierPaymentOption(row as Record<string, unknown>))
      : [],
    supplierPaymentRecords: Array.isArray(paymentsRaw)
      ? paymentsRaw.map((row) => mapSupplierPaymentOption(row as Record<string, unknown>))
      : [],
  };
}

export async function linkUnpaidProduct(payload: {
  periodYear: number;
  periodMonth: number;
  shopifyProductId: string;
  shopifyVariantId?: string;
  productTitle: string;
  supplierId: number;
  quantity: number;
  mode: 'replace' | 'link';
  linkSource?: 'invoice' | 'payment';
  expenseProductId?: number;
  expenseId?: number;
}): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Reports/unpaid/link`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося прывязаць неаплочаны тавар');
    throw new Error(msg);
  }
}
