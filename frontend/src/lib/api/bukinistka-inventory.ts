import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';

export type BukinistkaInventoryRow = {
  odooProductId: number;
  productName: string;
  mainImageUrl: string | null;
  odooUrl: string;
  acceptedQty: number;
  quantityInStock: number;
  soldQty: number;
  paidQty: number;
  quantityToPay: number;
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

function mapRow(row: Record<string, unknown>): BukinistkaInventoryRow {
  const mainImage =
    typeof row.mainImageUrl === 'string'
      ? row.mainImageUrl
      : typeof row.main_image_url === 'string'
        ? row.main_image_url
        : null;

  return {
    odooProductId: readNumber(row.odooProductId, row.odoo_product_id),
    productName: readString(row.productName, row.product_name),
    mainImageUrl: mainImage && mainImage.trim() ? mainImage.trim() : null,
    odooUrl: readString(row.odooUrl, row.odoo_url),
    acceptedQty: readNumber(row.acceptedQty, row.accepted_qty),
    quantityInStock: readNumber(row.quantityInStock, row.quantity_in_stock),
    soldQty: readNumber(row.soldQty, row.sold_qty),
    paidQty: readNumber(row.paidQty, row.paid_qty),
    quantityToPay: readNumber(row.quantityToPay, row.quantity_to_pay),
  };
}

export async function fetchBukinistkaInventory(): Promise<
  BukinistkaInventoryRow[]
> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/inventory`, {
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(
        res,
        'Не ўдалося загрузіць інвентарызацыю з Кірмашом.'
      )
    );
  }

  const data = (await res.json()) as {
    rows?: unknown;
    Rows?: unknown;
  };
  const list = (data.rows ?? data.Rows ?? []) as unknown[];
  return list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map(mapRow)
    .filter((row) => row.odooProductId > 0);
}
