import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';

export type BukinistkaPosSale = {
  id: number;
  odooPosOrderId: number;
  odooPosOrderName: string | null;
  offerId: number | null;
  odooProductId: number;
  shopifyProductId: string;
  shopifyVariantId: string;
  quantity: number;
  productName: string;
  grossUnitCost: number | null;
  supplierName: string | null;
  soldAtUtc: string;
  createdAtUtc: string;
};

export type BukinistkaPosSyncResult = {
  skipped: boolean;
  skipReason: string | null;
  ordersScanned: number;
  linesProcessed: number;
  unitsSynced: number;
  syncedAtUtc: string;
};

export type BukinistkaPosInvoiceResult = {
  vatReportId: number;
  vatReportRowId: number;
  periodYear: number;
  periodMonth: number;
  orderNumber: string;
  grossAmount: number;
  vatAmount: number;
  netAmount: number;
  invoicedSaleCount: number;
};

function readNumber(...values: unknown[]): number {
  for (const value of values) {
    const n = Number(value);
    if (Number.isFinite(n)) return n;
  }
  return 0;
}

function readOptionalNumber(...values: unknown[]): number | null {
  for (const value of values) {
    if (value == null || value === '') continue;
    const n = Number(value);
    if (Number.isFinite(n)) return n;
  }
  return null;
}

function readString(...values: unknown[]): string {
  for (const value of values) {
    if (typeof value === 'string') return value;
  }
  return '';
}

function readOptionalString(...values: unknown[]): string | null {
  for (const value of values) {
    if (typeof value === 'string') {
      const t = value.trim();
      return t ? t : null;
    }
  }
  return null;
}

function mapSale(row: Record<string, unknown>): BukinistkaPosSale {
  const offerId = readOptionalNumber(row.offerId, row.offer_id);
  return {
    id: readNumber(row.id),
    odooPosOrderId: readNumber(row.odooPosOrderId, row.odoo_pos_order_id),
    odooPosOrderName: readOptionalString(
      row.odooPosOrderName,
      row.odoo_pos_order_name
    ),
    offerId: offerId != null && offerId > 0 ? offerId : null,
    odooProductId: readNumber(row.odooProductId, row.odoo_product_id),
    shopifyProductId: readString(row.shopifyProductId, row.shopify_product_id),
    shopifyVariantId: readString(row.shopifyVariantId, row.shopify_variant_id),
    quantity: readNumber(row.quantity),
    productName: readString(row.productName, row.product_name),
    grossUnitCost: readOptionalNumber(row.grossUnitCost, row.gross_unit_cost),
    supplierName: readOptionalString(row.supplierName, row.supplier_name),
    soldAtUtc: readString(row.soldAtUtc, row.sold_at_utc),
    createdAtUtc: readString(row.createdAtUtc, row.created_at_utc),
  };
}

export async function fetchBukinistkaPosSales(): Promise<BukinistkaPosSale[]> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/sales`, {
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць продажы.')
    );
  }

  const data = (await res.json()) as unknown;
  const list = Array.isArray(data) ? data : [];
  return list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map(mapSale)
    .filter((s) => s.id > 0);
}

export async function syncBukinistkaPosSales(): Promise<BukinistkaPosSyncResult> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/sales/sync`, {
    method: 'POST',
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося сінхранізаваць продажы.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return {
    skipped: Boolean(data.skipped ?? data.Skipped),
    skipReason: readOptionalString(data.skipReason, data.skip_reason),
    ordersScanned: readNumber(data.ordersScanned, data.orders_scanned),
    linesProcessed: readNumber(data.linesProcessed, data.lines_processed),
    unitsSynced: readNumber(data.unitsSynced, data.units_synced),
    syncedAtUtc: readString(data.syncedAtUtc, data.synced_at_utc),
  };
}

export async function invoiceBukinistkaPosSales(input: {
  saleIds: number[];
  invoiceDateUtc: string;
  invoiceNumber?: string;
  vatRatePercent?: 5 | 23;
}): Promise<BukinistkaPosInvoiceResult> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/sales/invoice`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      saleIds: input.saleIds,
      invoiceDateUtc: input.invoiceDateUtc,
      invoiceNumber: input.invoiceNumber?.trim() || undefined,
      vatRatePercent: input.vatRatePercent ?? 5,
    }),
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося выставіць фактуру.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return {
    vatReportId: readNumber(data.vatReportId, data.vat_report_id),
    vatReportRowId: readNumber(data.vatReportRowId, data.vat_report_row_id),
    periodYear: readNumber(data.periodYear, data.period_year),
    periodMonth: readNumber(data.periodMonth, data.period_month),
    orderNumber: readString(data.orderNumber, data.order_number),
    grossAmount: readNumber(data.grossAmount, data.gross_amount),
    vatAmount: readNumber(data.vatAmount, data.vat_amount),
    netAmount: readNumber(data.netAmount, data.net_amount),
    invoicedSaleCount: readNumber(
      data.invoicedSaleCount,
      data.invoiced_sale_count
    ),
  };
}

export type BukinistkaPortalSale = {
  id: number;
  productName: string;
  quantity: number;
  grossUnitCost: number | null;
  supplierName: string | null;
  orderLabel: string;
  soldAtUtc: string;
};

export type BukinistkaShopifyDeliverySale = {
  id: number;
  offerId: number;
  shopifyOrderId: string;
  shopifyOrderNumber: string | null;
  odooPickingName: string | null;
  quantity: number;
  productName: string;
  grossUnitCost: number | null;
  supplierName: string | null;
  soldAtUtc: string;
  createdAtUtc: string;
};

function mapPortalSaleFromPos(row: BukinistkaPosSale): BukinistkaPortalSale {
  return {
    id: row.id,
    productName: row.productName,
    quantity: row.quantity,
    grossUnitCost: row.grossUnitCost,
    supplierName: row.supplierName,
    orderLabel: row.odooPosOrderName?.trim() || `#${row.odooPosOrderId}`,
    soldAtUtc: row.soldAtUtc,
  };
}

function mapPortalSaleFromShopify(
  row: BukinistkaShopifyDeliverySale
): BukinistkaPortalSale {
  return {
    id: row.id,
    productName: row.productName,
    quantity: row.quantity,
    grossUnitCost: row.grossUnitCost,
    supplierName: row.supplierName,
    orderLabel:
      row.shopifyOrderNumber?.trim() ||
      row.odooPickingName?.trim() ||
      row.shopifyOrderId,
    soldAtUtc: row.soldAtUtc,
  };
}

function mapShopifyDeliverySale(
  row: Record<string, unknown>
): BukinistkaShopifyDeliverySale {
  return {
    id: readNumber(row.id),
    offerId: readNumber(row.offerId, row.offer_id),
    shopifyOrderId: readString(row.shopifyOrderId, row.shopify_order_id),
    shopifyOrderNumber: readOptionalString(
      row.shopifyOrderNumber,
      row.shopify_order_number
    ),
    odooPickingName: readOptionalString(
      row.odooPickingName,
      row.odoo_picking_name
    ),
    quantity: readNumber(row.quantity),
    productName: readString(row.productName, row.product_name),
    grossUnitCost: readOptionalNumber(row.grossUnitCost, row.gross_unit_cost),
    supplierName: readOptionalString(row.supplierName, row.supplier_name),
    soldAtUtc: readString(row.soldAtUtc, row.sold_at_utc),
    createdAtUtc: readString(row.createdAtUtc, row.created_at_utc),
  };
}

export async function fetchBukinistkaPortalReceivedSales(): Promise<
  BukinistkaPortalSale[]
> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/sales/portal/received`,
    {
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(
        res,
        'Не ўдалося загрузіць продажы прынятага тавару.'
      )
    );
  }

  const data = (await res.json()) as unknown;
  const list = Array.isArray(data) ? data : [];
  return list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map((item) => mapPortalSaleFromPos(mapSale(item)))
    .filter((s) => s.id > 0);
}

export async function fetchBukinistkaPortalSentSales(): Promise<
  BukinistkaPortalSale[]
> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/sales/portal/sent`, {
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(
        res,
        'Не ўдалося загрузіць продажы высланага тавару.'
      )
    );
  }

  const data = (await res.json()) as unknown;
  const list = Array.isArray(data) ? data : [];
  return list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map((item) => mapPortalSaleFromShopify(mapShopifyDeliverySale(item)))
    .filter((s) => s.id > 0);
}

export async function syncBukinistkaPortalSales(): Promise<BukinistkaPosSyncResult> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/sales/portal/sync`, {
    method: 'POST',
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося сінхранізаваць продажы.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return {
    skipped: Boolean(data.skipped ?? data.Skipped),
    skipReason: readOptionalString(data.skipReason, data.skip_reason),
    ordersScanned: readNumber(data.ordersScanned, data.orders_scanned),
    linesProcessed: readNumber(data.linesProcessed, data.lines_processed),
    unitsSynced: readNumber(data.unitsSynced, data.units_synced),
    syncedAtUtc: readString(data.syncedAtUtc, data.synced_at_utc),
  };
}
