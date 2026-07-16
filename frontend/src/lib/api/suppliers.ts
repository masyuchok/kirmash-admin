/**
 * Supplier HTTP helpers. Point `NEXT_PUBLIC_API_URL` at your API.
 * - createSupplier → POST /suppliers/add (existing)
 * - fetchSupplierById → GET /suppliers/:id (optional; falls back to GET /suppliers + pick id)
 * - updateSupplier → PATCH /suppliers/:id (adjust method/path/body when backend is ready)
 */
import type { Supplier } from '@/types/supplier';
import {
  type SupplierFormValues,
  mapApiDetailToFormValues,
  mapListSupplierToFormValues,
} from '@/lib/suppliers/supplierFormTypes';
import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';
import { readBoolean, readInt, readNumber, readString } from '@/lib/api/json';
import type {
  SupplierInventoryResult,
  SupplierInventoryRow,
} from '@/types/supplier-inventory';

export type SupplierOption = {
  id: number;
  name: string;
  isVatPayer: boolean;
};

function mapInventoryRow(row: Record<string, unknown>): SupplierInventoryRow {
  return {
    supplierId: readInt(row.supplierId ?? row.SupplierId),
    supplierName: String(row.supplierName ?? row.SupplierName ?? ''),
    shopifyProductId: String(
      row.shopifyProductId ?? row.ShopifyProductId ?? ''
    ),
    shopifyVariantId: String(
      row.shopifyVariantId ?? row.ShopifyVariantId ?? ''
    ),
    variantTitle: String(row.variantTitle ?? row.VariantTitle ?? ''),
    productName: String(row.productName ?? row.ProductName ?? ''),
    productAuthor: String(row.productAuthor ?? row.ProductAuthor ?? ''),
    productType: String(row.productType ?? row.ProductType ?? ''),
    supplierPrice: readNumber(row.supplierPrice ?? row.SupplierPrice),
    vatRatePercent: readNumber(row.vatRatePercent ?? row.VatRatePercent) || 23,
    grossUnitPrice: readNumber(row.grossUnitPrice ?? row.GrossUnitPrice),
    supplierIsVatPayer: Boolean(
      row.supplierIsVatPayer ?? row.SupplierIsVatPayer
    ),
    hasPriceOverride: Boolean(row.hasPriceOverride ?? row.HasPriceOverride),
    marginPercent: readNumber(row.marginPercent ?? row.MarginPercent),
    salePrice: readNumber(row.salePrice ?? row.SalePrice),
    shopifyPrice: readNumber(row.shopifyPrice ?? row.ShopifyPrice),
    receivedQuantity: readInt(row.receivedQuantity ?? row.ReceivedQuantity),
    quantityInStock: readInt(row.quantityInStock ?? row.QuantityInStock),
    soldQuantity: readInt(row.soldQuantity ?? row.SoldQuantity),
    paidQuantity: readInt(row.paidQuantity ?? row.PaidQuantity),
    quantityToPay: readInt(row.quantityToPay ?? row.QuantityToPay),
  };
}

export async function fetchSupplierInventory(
  supplierId?: number,
  options?: { refresh?: boolean }
): Promise<SupplierInventoryResult> {
  const params = new URLSearchParams();
  if (supplierId) params.set('supplierId', String(supplierId));
  if (options?.refresh) params.set('refresh', 'true');
  const query = params.toString() ? `?${params.toString()}` : '';
  const res = await fetch(`${getApiBaseUrl()}/suppliers/inventory${query}`, {
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (res.status === 401 && typeof window !== 'undefined') {
    const shop = process.env.NEXT_PUBLIC_SHOP_DOMAIN?.trim();
    const loginUrl = new URL('/api/auth/login', window.location.origin);
    if (shop) loginUrl.searchParams.set('shop', shop);
    (window.top ?? window).location.assign(loginUrl.toString());
    throw new Error('Сесія скончана. Перазайдзіце праз Shopify.');
  }
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося загрузіць інвентарызацыю'
    );
    throw new Error(msg);
  }
  const data = (await res.json()) as unknown;
  if (Array.isArray(data)) {
    return {
      rows: data.map((item) =>
        mapInventoryRow(item as Record<string, unknown>)
      ),
      salesSyncedAtUtc: null,
    };
  }
  const payload = data as Record<string, unknown>;
  const rowsRaw = payload.rows ?? payload.Rows;
  return {
    rows: Array.isArray(rowsRaw)
      ? rowsRaw.map((item) => mapInventoryRow(item as Record<string, unknown>))
      : [],
    salesSyncedAtUtc:
      (payload.salesSyncedAtUtc ?? payload.SalesSyncedAtUtc)
        ? String(payload.salesSyncedAtUtc ?? payload.SalesSyncedAtUtc)
        : null,
  };
}

export async function updateSupplierInventoryPricing(payload: {
  supplierId: number;
  shopifyProductId: string;
  shopifyVariantId: string;
  netUnitPrice: number;
  vatRatePercent: number;
  marginPercent: number;
  salePrice: number;
  syncWithShopify: boolean;
}): Promise<SupplierInventoryRow> {
  const res = await fetch(`${getApiBaseUrl()}/suppliers/inventory/pricing`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({
      supplierId: payload.supplierId,
      shopifyProductId: payload.shopifyProductId,
      shopifyVariantId: payload.shopifyVariantId,
      netUnitPrice: payload.netUnitPrice,
      vatRatePercent: payload.vatRatePercent,
      marginPercent: payload.marginPercent,
      salePrice: payload.salePrice,
      syncWithShopify: payload.syncWithShopify,
    }),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося захаваць цану');
    throw new Error(msg);
  }
  const data = (await res.json()) as Record<string, unknown>;
  return mapInventoryRow(data);
}

export async function refreshSupplierInventorySales(): Promise<string | null> {
  const res = await fetch(`${getApiBaseUrl()}/suppliers/inventory/refresh`, {
    method: 'POST',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося абнавіць продажы');
    throw new Error(msg);
  }
  const data = (await res.json()) as Record<string, unknown>;
  const synced = data.salesSyncedAtUtc ?? data.SalesSyncedAtUtc;
  return synced ? String(synced) : null;
}

export async function fetchSuppliers(): Promise<
  import('@/types/supplier').Supplier[]
> {
  const res = await fetch(`${getApiBaseUrl()}/suppliers`, {
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося загрузіць пастаўшчыкоў'
    );
    throw new Error(msg);
  }
  const data = (await res.json()) as unknown;
  if (!Array.isArray(data)) return [];
  return data.map((item) => {
    const row = item as Record<string, unknown>;
    return {
      id: readInt(row.id ?? row.Id),
      name: readString(row.name ?? row.Name),
      telegram: readString(
        row.telegram ?? row.tgContact ?? row.tGContact ?? row.TGContact
      ),
      website: readString(row.website ?? row.Website),
      country: readString(row.country ?? row.Country),
      city: readString(row.city ?? row.City),
      isVatPayer: readBoolean(
        row.isVATPayer ?? row.isVatPayer ?? row.IsVATPayer ?? row.IsVatPayer
      ),
    };
  });
}

export async function fetchSupplierOptions(): Promise<SupplierOption[]> {
  const suppliers = await fetchSuppliers();
  return suppliers
    .filter((s) => s.id > 0 && s.name.trim().length > 0)
    .map((s) => ({ id: s.id, name: s.name, isVatPayer: s.isVatPayer }));
}

/** Response shape for GET /suppliers/:id — extend when backend is ready. */
export type SupplierApiDetail = Partial<SupplierFormValues> & {
  id: number;
  telegram?: string;
  isVatPayer?: boolean;
};

export async function createSupplier(
  payload: SupplierFormValues
): Promise<{ id?: number }> {
  const res = await fetch(`${getApiBaseUrl()}/suppliers/add`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Памылка пры захаванні');
    throw new Error(msg);
  }
  const data = (await res.json().catch(() => ({}))) as { id?: number };
  return data;
}

/**
 * Placeholder for the future update endpoint.
 * Adjust `method` and path to match your backend (e.g. PUT /suppliers/:id).
 */
export async function updateSupplier(
  id: number,
  payload: SupplierFormValues
): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/suppliers/${id}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Памылка пры абнаўленні');
    throw new Error(msg);
  }
}

/**
 * Load one supplier for editing. Falls back to list fetch + merge if GET by id fails (optional UX).
 */
export async function fetchSupplierById(
  id: number
): Promise<SupplierFormValues> {
  const res = await fetch(`${getApiBaseUrl()}/suppliers/${id}`, {
    credentials: apiCredentials,
  });
  if (res.ok) {
    const detail = (await res.json()) as SupplierApiDetail;
    return mapApiDetailToFormValues({ ...detail, id: detail.id ?? id });
  }

  // Fallback: use list endpoint and pick row (works before dedicated GET exists).
  const listRes = await fetch(`${getApiBaseUrl()}/suppliers`, {
    credentials: apiCredentials,
  });
  if (!listRes.ok) {
    const msg = await readErrorMessage(
      listRes,
      'Не ўдалося загрузіць пастаўшчыка'
    );
    throw new Error(msg);
  }
  const list = (await listRes.json()) as Supplier[];
  const row = list.find((s) => s.id === id);
  if (!row) {
    throw new Error('Пастаўшчык не знойдзены');
  }
  return mapListSupplierToFormValues(row);
}
