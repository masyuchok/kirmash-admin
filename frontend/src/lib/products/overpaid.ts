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

type OverpaidRowScope = {
  shopifyProductId: string;
  shopifyVariantId?: string;
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
    if (scope.supplierId != null && line.supplierId !== scope.supplierId) return sum;

    const lineVariant = normalizeVariantId(line.shopifyVariantId);
    if (scope.isVariantChild) {
      const rowVariant = normalizeVariantId(scope.shopifyVariantId ?? '');
      if (!lineVariant) return sum;
      if (!rowVariant || lineVariant !== rowVariant) return sum;
    }

    return sum + line.overpaidQuantity;
  }, 0);
}
