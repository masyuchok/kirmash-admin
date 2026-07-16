const VARIANT_SEP = '::';

/** Unique key for a supply line (product or product+variant). */
export function makeSupplyLineKey(
  productId: string,
  variantId?: string
): string {
  const v = variantId?.trim() ?? '';
  return v ? `${productId}${VARIANT_SEP}${v}` : productId;
}

export function parseSupplyLineKey(lineKey: string): {
  productId: string;
  variantId: string;
} {
  const idx = lineKey.indexOf(VARIANT_SEP);
  if (idx === -1) return { productId: lineKey, variantId: '' };
  return {
    productId: lineKey.slice(0, idx),
    variantId: lineKey.slice(idx + VARIANT_SEP.length),
  };
}
