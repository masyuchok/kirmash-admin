export function roundMoney(value: number): number {
  return Math.round(value * 100) / 100;
}

export function calcGrossUnitPrice(
  netUnitPrice: number,
  vatRatePercent: number,
  supplierIsVatPayer: boolean
): number {
  if (!supplierIsVatPayer) {
    return roundMoney(netUnitPrice);
  }
  return roundMoney(netUnitPrice * (1 + vatRatePercent / 100));
}

/** Net from supply gross when supplier is a VAT payer (matches expense VAT extraction). */
export function calcNetUnitPriceFromGross(grossUnitPrice: number, vatRatePercent: number): number {
  if (grossUnitPrice <= 0 || vatRatePercent <= 0) {
    return roundMoney(grossUnitPrice);
  }
  const rate = vatRatePercent / 100;
  const vatPart = roundMoney((grossUnitPrice * rate) / (1 + rate));
  return roundMoney(grossUnitPrice - vatPart);
}

export function calcGrossLineTotal(
  netUnitPrice: number,
  vatRatePercent: number,
  supplierIsVatPayer: boolean,
  quantity: number
): number {
  return roundMoney(calcGrossUnitPrice(netUnitPrice, vatRatePercent, supplierIsVatPayer) * quantity);
}

export function formatMoneyInput(value: number): string {
  if (!Number.isFinite(value)) return '';
  return value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

export function parseMoneyInput(raw: string): number | null {
  const normalized = raw.trim().replace(/\s/g, '').replace(',', '.');
  if (!normalized) return null;
  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

export function parsePercentInput(raw: string): number | null {
  const normalized = raw.trim().replace(',', '.');
  if (!normalized) return null;
  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

export function inventoryRowKey(row: {
  supplierId: number;
  shopifyProductId: string;
  shopifyVariantId: string;
}): string {
  return `${row.supplierId}:${row.shopifyProductId}:${row.shopifyVariantId}`;
}
