import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';
import type { ProductWithSuppliers } from '@/types/product';
import type {
  ProductHistory,
  ProductHistoryQuery,
} from '@/types/product-history';

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

function readNumber(v: unknown): number {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '') {
    const n = Number(v);
    return Number.isFinite(n) ? n : 0;
  }
  return 0;
}

export async function fetchProductsWithSuppliers(
  forceFresh = false
): Promise<ProductWithSuppliers[]> {
  const suffix = forceFresh ? `?_=${Date.now()}` : '';
  const res = await fetch(`${getApiBaseUrl()}/Products${suffix}`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
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
      ? suppliersRaw.filter(
          (s): s is string => typeof s === 'string' && s.trim().length > 0
        )
      : [];
    const supplierPricesRaw = r.supplierPrices ?? r.SupplierPrices;
    const supplierPrices = Array.isArray(supplierPricesRaw)
      ? supplierPricesRaw.map((item) => {
          const p = item as Record<string, unknown>;
          return {
            supplierId: readInt(p.supplierId ?? p.SupplierId),
            supplierName: readString(p.supplierName ?? p.SupplierName),
            supplierPrice: readNumber(p.supplierPrice ?? p.SupplierPrice),
            salePrice: readNumber(p.salePrice ?? p.SalePrice),
          };
        })
      : [];
    const unsyncedSuppliersRaw = r.unsyncedSuppliers ?? r.UnsyncedSuppliers;
    const unsyncedSuppliers = Array.isArray(unsyncedSuppliersRaw)
      ? unsyncedSuppliersRaw.map((item) => {
          const s = item as Record<string, unknown>;
          return {
            supplierId: readInt(s.supplierId ?? s.SupplierId),
            supplierName: readString(s.supplierName ?? s.SupplierName),
            quantity: readInt(s.quantity ?? s.Quantity),
          };
        })
      : [];
    const variantsRaw = r.variants ?? r.Variants;
    const variants = Array.isArray(variantsRaw)
      ? variantsRaw.map((item) => {
          const v = item as Record<string, unknown>;
          return {
            variantId: readString(v.variantId ?? v.VariantId),
            variantName: readString(v.variantName ?? v.VariantName),
            quantityInStock: readInt(v.quantityInStock ?? v.QuantityInStock),
          };
        })
      : [];

    const overpaidLinesRaw = r.overpaidLines ?? r.OverpaidLines;
    const overpaidLines = Array.isArray(overpaidLinesRaw)
      ? overpaidLinesRaw.map((item) => {
          const o = item as Record<string, unknown>;
          return {
            supplierId: readInt(o.supplierId ?? o.SupplierId),
            supplierName: readString(o.supplierName ?? o.SupplierName),
            shopifyProductId: readString(
              o.shopifyProductId ?? o.ShopifyProductId
            ),
            shopifyVariantId: readString(
              o.shopifyVariantId ?? o.ShopifyVariantId
            ),
            shopifyVariantTitle: readString(
              o.shopifyVariantTitle ?? o.ShopifyVariantTitle
            ),
            overpaidQuantity: readInt(o.overpaidQuantity ?? o.OverpaidQuantity),
          };
        })
      : [];

    return {
      shopifyProductId: readString(r.shopifyProductId ?? r.ShopifyProductId),
      productName: readString(r.productName ?? r.ProductName) || '—',
      productAuthor: readString(r.productAuthor ?? r.ProductAuthor),
      productType: readString(r.productType ?? r.ProductType),
      productAdminUrl: readString(r.productAdminUrl ?? r.ProductAdminUrl),
      mainImageUrl: readString(r.mainImageUrl ?? r.MainImageUrl) || null,
      quantityInStock: readInt(r.quantityInStock ?? r.QuantityInStock),
      shopifyQuantityInStock: readInt(
        r.shopifyQuantityInStock ?? r.ShopifyQuantityInStock
      ),
      hasSupplyQuantityOverride: Boolean(
        r.hasSupplyQuantityOverride ?? r.HasSupplyQuantityOverride ?? false
      ),
      lastSyncedSupplierName: readString(
        r.lastSyncedSupplierName ?? r.LastSyncedSupplierName
      ),
      suppliers,
      unsyncedSuppliers,
      variants,
      supplierPrices,
      overpaidLines,
    };
  });
}

function readNullableInt(v: unknown): number | null {
  if (v == null) return null;
  const n = readInt(v);
  return Number.isFinite(n) ? n : null;
}

export async function fetchProductHistory(
  shopifyProductId: string,
  query: ProductHistoryQuery = {}
): Promise<ProductHistory> {
  const params = new URLSearchParams();
  if (query.shopifyVariantId?.trim()) {
    params.set('shopifyVariantId', query.shopifyVariantId.trim());
  }
  if (query.supplierId != null && query.supplierId > 0) {
    params.set('supplierId', String(query.supplierId));
  }
  if (query.variantTitle?.trim()) {
    params.set('variantTitle', query.variantTitle.trim());
  }
  const suffix = params.toString() ? `?${params.toString()}` : '';
  const encodedId = encodeURIComponent(shopifyProductId);
  const res = await fetch(
    `${getApiBaseUrl()}/Products/${encodedId}/history${suffix}`,
    {
      method: 'GET',
      credentials: apiCredentials,
      cache: 'no-store',
    }
  );
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося загрузіць гісторыю прадукту'
    );
    throw new Error(msg);
  }

  const data = (await res.json()) as Record<string, unknown>;
  const suppliesRaw = data.supplies ?? data.Supplies;
  const salesRaw = data.sales ?? data.Sales;
  const paymentsRaw = data.payments ?? data.Payments;

  const supplies = Array.isArray(suppliesRaw)
    ? suppliesRaw.map((item) => {
        const s = item as Record<string, unknown>;
        return {
          date: readString(s.date ?? s.Date),
          supplyId: readInt(s.supplyId ?? s.SupplyId),
          supplierId: readInt(s.supplierId ?? s.SupplierId),
          supplierName: readString(s.supplierName ?? s.SupplierName),
          shopifyVariantId: readString(
            s.shopifyVariantId ?? s.ShopifyVariantId
          ),
          variantTitle: readString(s.variantTitle ?? s.VariantTitle),
          quantity: readInt(s.quantity ?? s.Quantity),
        };
      })
    : [];

  const sales = Array.isArray(salesRaw)
    ? salesRaw.map((item) => {
        const s = item as Record<string, unknown>;
        return {
          dateUtc: readString(s.dateUtc ?? s.DateUtc),
          source: readString(s.source ?? s.Source),
          orderNumber: readString(s.orderNumber ?? s.OrderNumber),
          reportId: readNullableInt(s.reportId ?? s.ReportId),
          shopifyVariantId: readString(
            s.shopifyVariantId ?? s.ShopifyVariantId
          ),
          variantTitle: readString(s.variantTitle ?? s.VariantTitle),
          quantity: readInt(s.quantity ?? s.Quantity),
        };
      })
    : [];

  const payments = Array.isArray(paymentsRaw)
    ? paymentsRaw.map((item) => {
        const p = item as Record<string, unknown>;
        return {
          dateUtc: readString(p.dateUtc ?? p.DateUtc),
          expenseId: readInt(p.expenseId ?? p.ExpenseId),
          reportId: readInt(p.reportId ?? p.ReportId),
          supplierId: readNullableInt(p.supplierId ?? p.SupplierId),
          supplierName: readString(p.supplierName ?? p.SupplierName),
          invoiceNumber: readString(p.invoiceNumber ?? p.InvoiceNumber),
          shopifyVariantId: readString(
            p.shopifyVariantId ?? p.ShopifyVariantId
          ),
          variantTitle: readString(p.variantTitle ?? p.VariantTitle),
          quantity: readInt(p.quantity ?? p.Quantity),
        };
      })
    : [];

  return {
    shopifyProductId: readString(
      data.shopifyProductId ?? data.ShopifyProductId
    ),
    productName: readString(data.productName ?? data.ProductName),
    supplies,
    sales,
    payments,
  };
}

export async function syncUnsyncedProductRow(
  shopifyProductId: string,
  supplierId: number
): Promise<{
  syncedQuantity: number;
  previousAvailable: number;
  newAvailable: number;
}> {
  const res = await fetch(`${getApiBaseUrl()}/Products/sync-unsynced`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({ shopifyProductId, supplierId }),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося сінхранізаваць радок з Shopify'
    );
    throw new Error(msg);
  }
  const data = (await res.json()) as {
    syncedQuantity?: number;
    previousAvailable?: number;
    newAvailable?: number;
  };
  return {
    syncedQuantity:
      typeof data.syncedQuantity === 'number' ? data.syncedQuantity : 0,
    previousAvailable:
      typeof data.previousAvailable === 'number' ? data.previousAvailable : 0,
    newAvailable: typeof data.newAvailable === 'number' ? data.newAvailable : 0,
  };
}
