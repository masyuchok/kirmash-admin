import { apiCredentials, getApiBaseUrl, readErrorMessage } from '@/lib/api/common';

export type SupplyProductSavePayload = {
  shopifyProductId: string;
  shopifyVariantId?: string;
  quantity: number;
  supplierPrice: number;
  vatRatePercent: number;
  marginPercent: number;
  salePrice: number;
  syncWithShopify: boolean;
  isReturnFinalized?: boolean;
};

export type SaveSupplyPayload = {
  supplyId?: number;
  supplierId: number;
  date: string;
  products: SupplyProductSavePayload[];
};

export type SupplyInventoryUpdate = {
  shopifyProductId: string;
  previousAvailable: number;
  addedQuantity: number;
  newAvailable: number;
};

export async function saveSupply(payload: SaveSupplyPayload): Promise<{
  id: number;
  warning: string | null;
  inventoryUpdates: SupplyInventoryUpdate[];
}> {
  const res = await fetch(`${getApiBaseUrl()}/Supply/save`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося захаваць пастаўку');
    throw new Error(msg);
  }
  const data = (await res.json()) as {
    id?: number;
    warning?: string;
    inventoryUpdates?: Array<{
      shopifyProductId?: string;
      previousAvailable?: number;
      addedQuantity?: number;
      newAvailable?: number;
    }>;
  };
  const updates = Array.isArray(data.inventoryUpdates)
    ? data.inventoryUpdates.map((u) => ({
        shopifyProductId: typeof u.shopifyProductId === 'string' ? u.shopifyProductId : '',
        previousAvailable: typeof u.previousAvailable === 'number' ? u.previousAvailable : 0,
        addedQuantity: typeof u.addedQuantity === 'number' ? u.addedQuantity : 0,
        newAvailable: typeof u.newAvailable === 'number' ? u.newAvailable : 0,
      }))
    : [];
  return {
    id: typeof data.id === 'number' ? data.id : -1,
    warning: typeof data.warning === 'string' && data.warning.trim() ? data.warning : null,
    inventoryUpdates: updates,
  };
}
