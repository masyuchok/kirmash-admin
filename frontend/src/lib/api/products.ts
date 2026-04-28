import { apiCredentials, getApiBaseUrl, readErrorMessage } from '@/lib/api/common';
import type { ProductWithSuppliers } from '@/types/product';

function readString(v: unknown): string {
  return typeof v === 'string' ? v : '';
}

function readInt(v: unknown): number {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '') {
    const n = Number(v);
    return Number.isFinite(n) ? n : 0;
  }
  return 0;
}

export async function fetchProductsWithSuppliers(): Promise<ProductWithSuppliers[]> {
  const res = await fetch(`${getApiBaseUrl()}/Products`, {
    method: 'GET',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць прадукты');
    throw new Error(msg);
  }

  const data = (await res.json()) as unknown;
  if (!Array.isArray(data)) {
    throw new Error('Некарэктны адказ сервера');
  }

  return data.map((row) => {
    const r = row as Record<string, unknown>;
    const suppliersRaw = r.suppliers ?? r.Suppliers;
    const suppliers = Array.isArray(suppliersRaw)
      ? suppliersRaw.filter((s): s is string => typeof s === 'string' && s.trim().length > 0)
      : [];

    return {
      shopifyProductId: readString(r.shopifyProductId ?? r.ShopifyProductId),
      productName: readString(r.productName ?? r.ProductName) || '—',
      productType: readString(r.productType ?? r.ProductType),
      productAdminUrl: readString(r.productAdminUrl ?? r.ProductAdminUrl),
      mainImageUrl: readString(r.mainImageUrl ?? r.MainImageUrl) || null,
      quantityInStock: readInt(r.quantityInStock ?? r.QuantityInStock),
      shopifyQuantityInStock: readInt(r.shopifyQuantityInStock ?? r.ShopifyQuantityInStock),
      hasSupplyQuantityOverride: Boolean(
        r.hasSupplyQuantityOverride ?? r.HasSupplyQuantityOverride ?? false
      ),
      suppliers,
    };
  });
}
