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
  canProposeToKirma: boolean;
  proposeBlockReason: string | null;
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
    canProposeToKirma: Boolean(
      row.canProposeToKirma ?? row.CanProposeToKirma ?? true
    ),
    proposeBlockReason:
      typeof row.proposeBlockReason === 'string'
        ? row.proposeBlockReason
        : typeof row.ProposeBlockReason === 'string'
          ? row.ProposeBlockReason
          : null,
  };
}

export type BukinistkaProductListResult = {
  products: BukinistkaProduct[];
  totalCount: number;
  isTruncated: boolean;
};

function mapListResponse(
  data: Record<string, unknown>
): BukinistkaProductListResult {
  const list = (data.products ?? data.Products ?? []) as unknown[];
  const products = list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map(mapProduct)
    .filter((p) => p.id > 0);

  const totalCount = readNumber(data.totalCount, data.total_count);
  const isTruncated = Boolean(data.isTruncated ?? data.is_truncated);

  return {
    products,
    totalCount: totalCount > 0 ? totalCount : products.length,
    isTruncated,
  };
}

export async function fetchBukinistkaProducts(options?: {
  search?: string;
}): Promise<BukinistkaProductListResult> {
  const params = new URLSearchParams();
  const search = options?.search?.trim();
  if (search) {
    params.set('search', search);
  }

  const query = params.toString();
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/products${query ? `?${query}` : ''}`,
    {
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць прадукты Odoo.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapListResponse(data);
}
