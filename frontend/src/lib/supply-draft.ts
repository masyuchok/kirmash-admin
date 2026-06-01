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

export function displayDraftLabel(row: SupplyProductDraft): string {
  if (row.variantName.trim()) {
    return `${row.productName} — ${row.variantName}`;
  }
  return row.productName;
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
