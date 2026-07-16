import type { ProductOverpaidLine } from '@/types/product';

function normalizeProductId(id: string): string {
  const trimmed = id.trim();
  const prefix = 'gid://shopify/Product/';
  if (trimmed.toLowerCase().startsWith(prefix.toLowerCase())) {
    return trimmed.slice(prefix.length);
  }
  return trimmed;
}

function normalizeVariantId(id: string): string {
  const trimmed = id.trim();
  const prefix = 'gid://shopify/ProductVariant/';
  if (trimmed.toLowerCase().startsWith(prefix.toLowerCase())) {
    return trimmed.slice(prefix.length);
  }
  return trimmed;
}

function normalizeVariantName(name: string): string {
  return name.trim().toLocaleLowerCase('be');
}

function variantLinesMatch(
  rowVariantId: string,
  rowVariantName: string,
  lineVariantId: string,
  lineVariantName: string
): boolean {
  if (rowVariantId && lineVariantId) {
    return rowVariantId === lineVariantId;
  }

  if (rowVariantName && lineVariantName) {
    return (
      normalizeVariantName(rowVariantName) ===
      normalizeVariantName(lineVariantName)
    );
  }

  return false;
}

type OverpaidRowScope = {
  shopifyProductId: string;
  shopifyVariantId?: string;
  variantName?: string;
  supplierId?: number | null;
  isVariantChild?: boolean;
  rowSource?: 'shopify' | 'supply';
};

export function getRowOverpaidQuantity(
  lines: ProductOverpaidLine[],
  scope: OverpaidRowScope
): number {
  if (lines.length === 0) return 0;

  const productId = normalizeProductId(scope.shopifyProductId);

  return lines.reduce((sum, line) => {
    if (normalizeProductId(line.shopifyProductId) !== productId) return sum;
    if (scope.supplierId != null && line.supplierId !== scope.supplierId)
      return sum;

    if (scope.isVariantChild) {
      const rowVariant = normalizeVariantId(scope.shopifyVariantId ?? '');
      const lineVariant = normalizeVariantId(line.shopifyVariantId);
      const rowVariantName = scope.variantName?.trim() ?? '';
      const lineVariantName = line.shopifyVariantTitle?.trim() ?? '';

      if (
        !variantLinesMatch(
          rowVariant,
          rowVariantName,
          lineVariant,
          lineVariantName
        )
      ) {
        return sum;
      }
    }

    return sum + line.overpaidQuantity;
  }, 0);
}
