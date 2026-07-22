import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';

export type BukinistkaPosSale = {
  id: number;
  odooPosOrderId: number;
  odooPosOrderName: string | null;
  odooProductId: number;
  shopifyProductId: string;
  quantity: number;
  productName: string;
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

function readNumber(...values: unknown[]): number {
  for (const value of values) {
    const n = Number(value);
    if (Number.isFinite(n)) return n;
  }
  return 0;
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
  return {
    id: readNumber(row.id),
    odooPosOrderId: readNumber(row.odooPosOrderId, row.odoo_pos_order_id),
    odooPosOrderName: readOptionalString(
      row.odooPosOrderName,
      row.odoo_pos_order_name
    ),
    odooProductId: readNumber(row.odooProductId, row.odoo_product_id),
    shopifyProductId: readString(row.shopifyProductId, row.shopify_product_id),
    quantity: readNumber(row.quantity),
    productName: readString(row.productName, row.product_name),
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
