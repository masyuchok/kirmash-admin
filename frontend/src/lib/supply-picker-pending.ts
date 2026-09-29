export type SupplyPickerPriceOverride = {
  supplierPrice?: number;
  salePrice?: number;
  /** Created via Book AI lookup — do not check «Абнаўляць» in the supply form. */
  fromAi?: boolean;
};

export type SupplyPendingProduct = {
  shopifyProductId: string;
  shopifyVariantId: string;
  title: string;
  productAuthor?: string;
  productType?: string;
  productAdminUrl?: string;
  mainImageUrl?: string | null;
  isbn?: string;
  fromAi?: boolean;
  salePrice?: number;
  unitCost?: number;
};

const PENDING_PREFIX = 'kirma.supplyPendingProducts.v1:';

function pendingKey(supplyId: string | undefined): string {
  return `${PENDING_PREFIX}${supplyId ?? 'new'}`;
}

export function readPendingSupplyProducts(
  supplyId: string | undefined
): Record<string, SupplyPendingProduct> {
  if (typeof window === 'undefined') return {};
  try {
    const raw = window.sessionStorage.getItem(pendingKey(supplyId));
    if (!raw) return {};
    const parsed = JSON.parse(raw) as Record<string, SupplyPendingProduct>;
    if (!parsed || typeof parsed !== 'object') return {};
    return parsed;
  } catch {
    return {};
  }
}

export function upsertPendingSupplyProduct(
  supplyId: string | undefined,
  product: SupplyPendingProduct
): void {
  if (typeof window === 'undefined') return;
  if (!product.shopifyProductId.trim()) return;
  try {
    const next = {
      ...readPendingSupplyProducts(supplyId),
      [product.shopifyProductId]: product,
    };
    window.sessionStorage.setItem(pendingKey(supplyId), JSON.stringify(next));
  } catch {
    /* ignore quota / private mode */
  }
}

export function clearPendingSupplyProducts(supplyId: string | undefined): void {
  if (typeof window === 'undefined') return;
  try {
    window.sessionStorage.removeItem(pendingKey(supplyId));
  } catch {
    /* ignore */
  }
}

/** Match bare productId or productId::variantId overrides (AI pin vs catalog Default Title). */
export function findPriceOverrideForProduct(
  prices: Record<string, SupplyPickerPriceOverride>,
  productId: string,
  lineKey?: string
): SupplyPickerPriceOverride | undefined {
  if (lineKey && prices[lineKey]) return prices[lineKey];
  if (prices[productId]) return prices[productId];
  for (const [key, override] of Object.entries(prices)) {
    if (key === productId || key.startsWith(`${productId}::`)) {
      return override;
    }
  }
  return undefined;
}

export function parseSelectedProductPrices(
  raw: string | undefined
): Record<string, SupplyPickerPriceOverride> {
  if (!raw?.trim()) return {};
  try {
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    if (!parsed || typeof parsed !== 'object') return {};
    const result: Record<string, SupplyPickerPriceOverride> = {};
    for (const [key, value] of Object.entries(parsed)) {
      if (!key.trim() || !value || typeof value !== 'object') continue;
      const row = value as Record<string, unknown>;
      const supplierPrice = Number(row.supplierPrice ?? row.unitCost);
      const salePrice = Number(row.salePrice);
      result[key] = {
        supplierPrice:
          Number.isFinite(supplierPrice) && supplierPrice > 0
            ? supplierPrice
            : undefined,
        salePrice:
          Number.isFinite(salePrice) && salePrice > 0 ? salePrice : undefined,
        fromAi: row.fromAi === true || row.FromAi === true,
      };
    }
    return result;
  } catch {
    return {};
  }
}
