import type { ChangeEvent } from 'react';
import type { ProductWithSuppliers } from '@/types/product';
import { makeSupplyLineKey } from '@/lib/supply-line-key';

export type SupplyProductDraft = {
  lineKey: string;
  productId: string;
  variantId: string;
  variantName: string;
  productName: string;
  productType: string;
  syncWithShopify: boolean;
  quantity: string;
  supplierPrice: string;
  vatRatePercent: string;
  marginPercent: string;
  salePrice: string;
};

/** Read input/select value synchronously (safe for React synthetic events). */
export function readFieldValue(e: ChangeEvent<HTMLInputElement | HTMLSelectElement>): string {
  return e.target.value;
}

/** Ensures lineKey/productId exist (legacy session rows may omit lineKey). */
export function normalizeSupplyDraftRow(row: Partial<SupplyProductDraft>): SupplyProductDraft {
  const productId = String(row.productId ?? '').trim();
  const variantId = String(row.variantId ?? '').trim();
  const lineKey =
    String(row.lineKey ?? '').trim() || makeSupplyLineKey(productId, variantId || undefined);
  return {
    lineKey,
    productId,
    variantId,
    variantName: String(row.variantName ?? ''),
    productName: String(row.productName ?? productId),
    productType: String(row.productType ?? ''),
    syncWithShopify: row.syncWithShopify ?? true,
    quantity: String(row.quantity ?? ''),
    supplierPrice: String(row.supplierPrice ?? ''),
    vatRatePercent: String(row.vatRatePercent ?? '23'),
    marginPercent: String(row.marginPercent ?? ''),
    salePrice: String(row.salePrice ?? ''),
  };
}

export function formatProductNameWithAuthor(productName: string, author?: string | null): string {
  const name = productName.trim() || '—';
  const trimmedAuthor = author?.trim();
  if (!trimmedAuthor) return name;
  return `${name}, ${trimmedAuthor}`;
}

export type SupplyProductSnapshot = {
  shopifyProductId: string;
  shopifyVariantId?: string;
  quantity: number;
  supplierPrice: number;
  vatRatePercent: number;
  marginPercent: number;
  salePrice: number;
  syncWithShopify: boolean;
};

export function formatDraftQuantity(value: number): string {
  if (!Number.isFinite(value) || value === 0) return '';
  return Number.isInteger(value) ? String(value) : String(value);
}

export function formatDraftMoney(value: number): string {
  if (!Number.isFinite(value)) return '';
  const rounded = Math.round((value + Number.EPSILON) * 100) / 100;
  if (rounded === 0) return '0';
  return Number.isInteger(rounded) ? String(rounded) : rounded.toFixed(2);
}

export function formatDraftMargin(value: number): string {
  if (!Number.isFinite(value)) return '';
  return String(Math.round(value));
}

export function createDraftRowFromSupplyProduct(
  product: SupplyProductSnapshot,
  productMap: Map<string, ProductWithSuppliers>,
  normalizeVatRatePercent: (value: number) => number
): SupplyProductDraft {
  const match = productMap.get(product.shopifyProductId);
  const variantMatch = match?.variants.find((v) => v.variantId === product.shopifyVariantId);
  return normalizeSupplyDraftRow({
    lineKey: makeSupplyLineKey(product.shopifyProductId, product.shopifyVariantId),
    productId: product.shopifyProductId,
    variantId: product.shopifyVariantId ?? '',
    variantName: variantMatch?.variantName ?? '',
    productName: match?.productName ?? product.shopifyProductId,
    productType: match?.productType ?? '',
    syncWithShopify: product.syncWithShopify,
    quantity: formatDraftQuantity(product.quantity),
    supplierPrice: formatDraftMoney(product.supplierPrice),
    vatRatePercent: String(
      normalizeVatRatePercent(product.vatRatePercent > 0 ? product.vatRatePercent : 23)
    ),
    marginPercent: formatDraftMargin(product.marginPercent),
    salePrice: formatDraftMoney(product.salePrice),
  });
}

export function displayDraftLabel(row: SupplyProductDraft, author?: string | null): string {
  const title = formatProductNameWithAuthor(row.productName, author);
  if (row.variantName.trim()) {
    return `${title} — ${row.variantName}`;
  }
  return title;
}

export function createDraftLinesForProduct(
  product: ProductWithSuppliers,
  quantities: Record<string, string>,
  defaultVatRatePercent: number
): SupplyProductDraft[] {
  const variants = (product.variants ?? []).filter(
    (v) => (v.variantId?.trim() || v.variantName?.trim()) && v.variantName !== 'Default Title'
  );

  const base = {
    productId: product.shopifyProductId,
    productName: product.productName,
    productType: product.productType,
    syncWithShopify: true,
    supplierPrice: '',
    vatRatePercent: String(defaultVatRatePercent),
    marginPercent: '',
    salePrice: '',
  };

  if (variants.length > 1) {
    return variants.map((v) => {
      const lineKey = makeSupplyLineKey(product.shopifyProductId, v.variantId);
      return {
        ...base,
        lineKey,
        variantId: v.variantId,
        variantName: v.variantName,
        quantity: quantities[lineKey] ?? '',
      };
    });
  }

  const only = variants[0];
  const lineKey = makeSupplyLineKey(product.shopifyProductId, only?.variantId);
  return [
    {
      ...base,
      lineKey,
      variantId: only?.variantId ?? '',
      variantName: only?.variantName ?? '',
      quantity:
        quantities[lineKey] ??
        quantities[product.shopifyProductId] ??
        '',
    },
  ];
}
