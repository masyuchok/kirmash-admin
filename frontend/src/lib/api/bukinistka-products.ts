import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';

export type BukinistkaProduct = {
  id: number;
  name: string;
  defaultCode: string | null;
  barcode: string | null;
  quantityInStock: number;
  listPrice: number;
  standardPrice: number;
  uomName: string | null;
  supplierName: string | null;
  odooUrl: string;
};

function readNumber(...values: unknown[]): number {
  for (const value of values) {
    const n = Number(value);
    if (Number.isFinite(n)) return n;
  }
  return 0;
}

function mapProduct(row: Record<string, unknown>): BukinistkaProduct {
  return {
    id: Number(row.id) || 0,
    name: typeof row.name === 'string' ? row.name : String(row.name ?? ''),
    defaultCode:
      typeof row.defaultCode === 'string'
        ? row.defaultCode
        : typeof row.default_code === 'string'
          ? row.default_code
          : null,
    barcode: typeof row.barcode === 'string' ? row.barcode : null,
    quantityInStock: readNumber(row.quantityInStock, row.quantity_in_stock),
    listPrice: readNumber(row.listPrice, row.list_price),
    standardPrice: readNumber(row.standardPrice, row.standard_price),
    uomName:
      typeof row.uomName === 'string'
        ? row.uomName
        : typeof row.uom_name === 'string'
          ? row.uom_name
          : null,
    supplierName:
      typeof row.supplierName === 'string'
        ? row.supplierName
        : typeof row.supplier_name === 'string'
          ? row.supplier_name
          : null,
    odooUrl:
      typeof row.odooUrl === 'string'
        ? row.odooUrl
        : typeof row.odoo_url === 'string'
          ? row.odoo_url
          : '',
  };
}

export async function fetchBukinistkaProducts(): Promise<BukinistkaProduct[]> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/products`, {
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць прадукты Odoo.')
    );
  }

  const data = (await res.json()) as {
    products?: unknown;
    Products?: unknown;
  };
  const list = (data.products ?? data.Products ?? []) as unknown[];
  return list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map(mapProduct)
    .filter((p) => p.id > 0);
}
