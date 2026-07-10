import type { SupplierInventoryRow } from '@/types/supplier-inventory';
import { formatProductNameWithAuthor, isBookProductType } from '@/lib/supply-draft';

export function formatInventoryProductTitle(
  row: Pick<SupplierInventoryRow, 'productName' | 'productAuthor' | 'productType'>
): string {
  return isBookProductType(row.productType)
    ? formatProductNameWithAuthor(row.productName, row.productAuthor)
    : row.productName;
}

export function formatInventoryLineName(
  row: Pick<SupplierInventoryRow, 'productName' | 'productAuthor' | 'productType' | 'variantTitle'>
): string {
  const baseName = formatInventoryProductTitle(row);
  if (isNamedInventoryVariantTitle(row.variantTitle)) {
    return `${baseName} — ${row.variantTitle.trim()}`;
  }
  return baseName;
}

export type InventoryProductGroup = {
  key: string;
  productName: string;
  supplierId: number;
  supplierName: string;
  shopifyProductId: string;
  variants: SupplierInventoryRow[];
};

export type InventoryGroupTotals = Pick<
  SupplierInventoryRow,
  'receivedQuantity' | 'paidQuantity' | 'quantityInStock' | 'soldQuantity' | 'quantityToPay'
>;

export type InventoryDisplayRow =
  | { type: 'parent'; group: InventoryProductGroup; totals: InventoryGroupTotals }
  | { type: 'variant'; row: SupplierInventoryRow; groupKey: string; isVariantChild: boolean };

export function inventoryGroupKey(
  row: SupplierInventoryRow,
  includeSupplierInKey: boolean
): string {
  return includeSupplierInKey
    ? `${row.supplierId}::${row.shopifyProductId}`
    : row.shopifyProductId;
}

export function groupInventoryRows(
  rows: SupplierInventoryRow[],
  includeSupplierInKey: boolean
): InventoryProductGroup[] {
  const map = new Map<string, InventoryProductGroup>();

  for (const row of rows) {
    const key = inventoryGroupKey(row, includeSupplierInKey);
    const existing = map.get(key);
    if (existing) {
      existing.variants.push(row);
      continue;
    }

    map.set(key, {
      key,
      productName: row.productName,
      supplierId: row.supplierId,
      supplierName: row.supplierName,
      shopifyProductId: row.shopifyProductId,
      variants: [row],
    });
  }

  return [...map.values()].map((group) => ({
    ...group,
    variants: [...group.variants].sort((a, b) =>
      a.variantTitle.localeCompare(b.variantTitle, 'be')
    ),
  }));
}

export function isNamedInventoryVariantTitle(variantTitle: string): boolean {
  const trimmed = variantTitle.trim();
  return trimmed.length > 0 && trimmed.toLowerCase() !== 'default title';
}

export function shouldShowInventoryTree(group: InventoryProductGroup): boolean {
  if (group.variants.length > 1) return true;
  return group.variants.some((row) => isNamedInventoryVariantTitle(row.variantTitle));
}

export function sumInventoryGroup(variants: SupplierInventoryRow[]): InventoryGroupTotals {
  return variants.reduce<InventoryGroupTotals>(
    (acc, row) => ({
      receivedQuantity: acc.receivedQuantity + row.receivedQuantity,
      paidQuantity: acc.paidQuantity + row.paidQuantity,
      quantityInStock: acc.quantityInStock + row.quantityInStock,
      soldQuantity: acc.soldQuantity + row.soldQuantity,
      quantityToPay: acc.quantityToPay + row.quantityToPay,
    }),
    {
      receivedQuantity: 0,
      paidQuantity: 0,
      quantityInStock: 0,
      soldQuantity: 0,
      quantityToPay: 0,
    }
  );
}

export function flattenInventoryGroups(
  groups: InventoryProductGroup[],
  collapsed: Record<string, boolean>
): InventoryDisplayRow[] {
  const result: InventoryDisplayRow[] = [];

  for (const group of groups) {
    if (!shouldShowInventoryTree(group)) {
      result.push({
        type: 'variant',
        row: group.variants[0],
        groupKey: group.key,
        isVariantChild: false,
      });
      continue;
    }

    result.push({
      type: 'parent',
      group,
      totals: sumInventoryGroup(group.variants),
    });

    if (collapsed[group.key]) continue;

    for (const row of group.variants) {
      result.push({
        type: 'variant',
        row,
        groupKey: group.key,
        isVariantChild: true,
      });
    }
  }

  return result;
}
