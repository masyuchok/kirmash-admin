import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';

export type KirmaBukinistkaOffer = {
  id: number;
  direction: string;
  shopifyProductId: string;
  shopifyVariantId: string;
  productName: string;
  productAuthor: string;
  mainImageUrl: string | null;
  productAdminUrl: string;
  storefrontUrl: string;
  supplierName: string | null;
  quantity: number;
  grossUnitCost: number;
  status: 'Pending' | 'Accepted' | 'Rejected' | string;
  odooProductId: number | null;
  odooQuantityBeforeAccept: number | null;
  acceptedListPrice: number | null;
  syncOnSale: boolean;
  isAssignment: boolean;
  peerPriceChangePending: boolean;
  remainingQuantity: number;
  createdAtUtc: string;
  shopifySalePrice: number | null;
  bukinistkaSalePrice: number | null;
};

export type CreateKirmaBukinistkaOfferInput = {
  shopifyProductId: string;
  shopifyVariantId?: string;
  productName: string;
  productAuthor?: string;
  mainImageUrl?: string | null;
  productAdminUrl?: string;
  supplierName?: string | null;
  quantity: number;
  grossUnitCost: number;
  syncOnSale?: boolean;
};

function readNumber(...values: unknown[]): number {
  for (const value of values) {
    const n = Number(value);
    if (Number.isFinite(n)) return n;
  }
  return 0;
}

function readString(...values: unknown[]): string {
  for (const value of values) {
    if (typeof value === 'string') return value;
  }
  return '';
}

function readOptionalNumber(...values: unknown[]): number | null {
  for (const value of values) {
    if (value === null || value === undefined) continue;
    const n = Number(value);
    if (Number.isFinite(n)) return n;
  }
  return null;
}

function mapOffer(row: Record<string, unknown>): KirmaBukinistkaOffer {
  const mainImage =
    typeof row.mainImageUrl === 'string'
      ? row.mainImageUrl
      : typeof row.main_image_url === 'string'
        ? row.main_image_url
        : null;

  const supplier =
    typeof row.supplierName === 'string'
      ? row.supplierName
      : typeof row.supplier_name === 'string'
        ? row.supplier_name
        : null;

  return {
    id: readNumber(row.id),
    direction: readString(row.direction, row.Direction) || 'KirmaToBukinistka',
    shopifyProductId: readString(row.shopifyProductId, row.shopify_product_id),
    shopifyVariantId: readString(row.shopifyVariantId, row.shopify_variant_id),
    productName: readString(row.productName, row.product_name),
    productAuthor: readString(row.productAuthor, row.product_author),
    mainImageUrl: mainImage,
    productAdminUrl: readString(row.productAdminUrl, row.product_admin_url),
    storefrontUrl: readString(row.storefrontUrl, row.storefront_url),
    supplierName: supplier,
    quantity: readNumber(row.quantity),
    grossUnitCost: readNumber(row.grossUnitCost, row.gross_unit_cost),
    status: readString(row.status) || 'Pending',
    odooProductId: readOptionalNumber(row.odooProductId, row.odoo_product_id),
    odooQuantityBeforeAccept: readOptionalNumber(
      row.odooQuantityBeforeAccept,
      row.odoo_quantity_before_accept
    ),
    acceptedListPrice: readOptionalNumber(
      row.acceptedListPrice,
      row.accepted_list_price
    ),
    syncOnSale: Boolean(row.syncOnSale ?? row.SyncOnSale),
    isAssignment: Boolean(row.isAssignment ?? row.IsAssignment),
    peerPriceChangePending: Boolean(
      row.peerPriceChangePending ?? row.PeerPriceChangePending
    ),
    remainingQuantity: readNumber(
      row.remainingQuantity,
      row.RemainingQuantity,
      row.quantity
    ),
    createdAtUtc: readString(row.createdAtUtc, row.created_at_utc),
    shopifySalePrice: readOptionalNumber(
      row.shopifySalePrice,
      row.shopify_sale_price
    ),
    bukinistkaSalePrice: readOptionalNumber(
      row.bukinistkaSalePrice,
      row.bukinistka_sale_price
    ),
  };
}

export async function createKirmaBukinistkaOffer(
  input: CreateKirmaBukinistkaOfferInput
): Promise<KirmaBukinistkaOffer> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/offers`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      shopifyProductId: input.shopifyProductId,
      shopifyVariantId: input.shopifyVariantId || null,
      productName: input.productName,
      productAuthor: input.productAuthor || null,
      mainImageUrl: input.mainImageUrl || null,
      productAdminUrl: input.productAdminUrl || null,
      supplierName: input.supplierName || null,
      quantity: input.quantity,
      grossUnitCost: input.grossUnitCost,
      syncOnSale: Boolean(input.syncOnSale),
    }),
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося даслаць прапанову.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapOffer(data);
}

export async function createBukinistkaOfferToKirma(input: {
  odooProductId: number;
  quantity: number;
  grossUnitCost: number;
  syncOnSale?: boolean;
  isAssignment?: boolean;
}): Promise<KirmaBukinistkaOffer> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/from-bukinistka`,
    {
      method: 'POST',
      credentials: apiCredentials,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        odooProductId: input.odooProductId,
        quantity: input.quantity,
        grossUnitCost: input.grossUnitCost,
        syncOnSale: input.isAssignment
          ? (input.syncOnSale ?? false)
          : (input.syncOnSale ?? true),
        isAssignment: Boolean(input.isAssignment),
      }),
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося даслаць прапанову Кірмашу.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapOffer(data);
}

export async function fetchKirmaReceivedBukinistkaOffers(): Promise<
  KirmaBukinistkaOffer[]
> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/offers/received`, {
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць атрыманыя прапановы.')
    );
  }

  const data = (await res.json()) as unknown;
  const list = Array.isArray(data) ? data : [];
  return list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map(mapOffer)
    .filter((o) => o.id > 0);
}

export async function fetchBukinistkaSentOffers(): Promise<
  KirmaBukinistkaOffer[]
> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/sent-by-bukinistka`,
    { credentials: apiCredentials }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць высланыя прапановы.')
    );
  }

  const data = (await res.json()) as unknown;
  const list = Array.isArray(data) ? data : [];
  return list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map(mapOffer)
    .filter((o) => o.id > 0);
}

export type AcceptBukinistkaOfferByKirmaResult = {
  offer: KirmaBukinistkaOffer;
  shopifySyncWarning: string | null;
};

export async function acceptBukinistkaOfferByKirma(
  id: number,
  input: {
    shopifyProductId: string;
    shopifyVariantId?: string;
    salePrice?: number | null;
  }
): Promise<AcceptBukinistkaOfferByKirmaResult> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${id}/accept-by-kirma`,
    {
      method: 'POST',
      credentials: apiCredentials,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        shopifyProductId: input.shopifyProductId,
        shopifyVariantId: input.shopifyVariantId || null,
        salePrice: input.salePrice ?? null,
      }),
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося прыняць прапанову.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  const offerRaw =
    data.offer && typeof data.offer === 'object'
      ? (data.offer as Record<string, unknown>)
      : data;
  const warningRaw = data.shopifySyncWarning ?? data.shopify_sync_warning;
  return {
    offer: mapOffer(offerRaw),
    shopifySyncWarning:
      typeof warningRaw === 'string' && warningRaw.trim()
        ? warningRaw.trim()
        : null,
  };
}

export type KirmaCreateShopifyProductPreview = {
  productName: string;
  productAuthor: string;
  grossUnitCost: number;
  odooListPrice: number | null;
  vendor: string | null;
  productType: string | null;
  barcodeDigits: string | null;
  weightKg: string | null;
  descriptionHtml: string | null;
};

export type KirmaCreateShopifyProductResult = {
  shopifyProductId: string;
  shopifyVariantId: string;
  shopifyProductName: string;
  salePrice: number;
};

export type CreateKirmaShopifyProductOptions = {
  omitBarcode?: boolean;
  barcodeDigits?: string | null;
};

export class KirmaCreateShopifyProductError extends Error {
  code: string | null;

  constructor(message: string, code: string | null = null) {
    super(message);
    this.name = 'KirmaCreateShopifyProductError';
    this.code = code;
  }
}

/** Kirma: Odoo preview before creating a new Shopify product for a received offer. */
export async function fetchKirmaCreateShopifyProductPreview(
  offerId: number
): Promise<KirmaCreateShopifyProductPreview> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${offerId}/create-shopify-product-preview`,
    { credentials: apiCredentials }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(
        res,
        'Не ўдалося загрузіць даныя для новага прадукта.'
      )
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return {
    productName: readString(data.productName, data.product_name),
    productAuthor: readString(data.productAuthor, data.product_author),
    grossUnitCost: readNumber(data.grossUnitCost, data.gross_unit_cost),
    odooListPrice: readOptionalNumber(data.odooListPrice, data.odoo_list_price),
    vendor:
      typeof data.vendor === 'string' && data.vendor.trim()
        ? data.vendor.trim()
        : null,
    productType:
      typeof data.productType === 'string' && data.productType.trim()
        ? data.productType.trim()
        : typeof data.product_type === 'string' && data.product_type.trim()
          ? data.product_type.trim()
          : null,
    barcodeDigits:
      typeof data.barcodeDigits === 'string' && data.barcodeDigits.trim()
        ? data.barcodeDigits.trim()
        : typeof data.barcode_digits === 'string' && data.barcode_digits.trim()
          ? data.barcode_digits.trim()
          : null,
    weightKg:
      typeof data.weightKg === 'string' && data.weightKg.trim()
        ? data.weightKg.trim()
        : typeof data.weight_kg === 'string' && data.weight_kg.trim()
          ? data.weight_kg.trim()
          : null,
    descriptionHtml:
      typeof data.descriptionHtml === 'string' && data.descriptionHtml.trim()
        ? data.descriptionHtml.trim()
        : typeof data.description_html === 'string' &&
            data.description_html.trim()
          ? data.description_html.trim()
          : null,
  };
}

/** Kirma: create a new Shopify product card for a received Bukinistka offer. */
export async function createKirmaShopifyProduct(
  offerId: number,
  salePrice: number,
  options?: CreateKirmaShopifyProductOptions
): Promise<KirmaCreateShopifyProductResult> {
  const body: Record<string, unknown> = { salePrice };
  if (options?.omitBarcode === true) {
    body.omitBarcode = true;
  } else if (options?.barcodeDigits !== undefined) {
    body.barcodeDigits = options.barcodeDigits ?? '';
  }

  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${offerId}/create-shopify-product`,
    {
      method: 'POST',
      credentials: apiCredentials,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }
  );

  if (!res.ok) {
    let message = 'Не ўдалося стварыць картку ў Shopify.';
    let code: string | null = null;
    try {
      const data = (await res.json()) as Record<string, unknown>;
      if (typeof data.error === 'string' && data.error.trim()) {
        message = data.error.trim();
      }
      if (typeof data.code === 'string' && data.code.trim()) {
        code = data.code.trim();
      }
    } catch {
      message = await readErrorMessage(res, message);
    }
    throw new KirmaCreateShopifyProductError(message, code);
  }

  const data = (await res.json()) as Record<string, unknown>;
  return {
    shopifyProductId: readString(
      data.shopifyProductId,
      data.shopify_product_id
    ),
    shopifyVariantId: readString(
      data.shopifyVariantId,
      data.shopify_variant_id
    ),
    shopifyProductName: readString(
      data.shopifyProductName,
      data.shopify_product_name
    ),
    salePrice: readNumber(data.salePrice, data.sale_price),
  };
}

export async function rejectBukinistkaOfferByKirma(id: number): Promise<void> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${id}/reject-by-kirma`,
    {
      method: 'POST',
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося адхіліць прапанову.')
    );
  }
}

/** Kirma closes an accepted Buk→Kirma offer whose Shopify card was deleted. */
export async function closeOrphanedBukinistkaOfferByKirma(
  id: number
): Promise<void> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${id}/close-orphaned`,
    {
      method: 'POST',
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося закрыць прапанову.')
    );
  }
}

export async function cancelBukinistkaSentOffer(id: number): Promise<void> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/sent-by-bukinistka/${id}`,
    {
      method: 'DELETE',
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося адмяніць прапанову.')
    );
  }
}

export async function fetchKirmaProposeEligibility(
  shopifyProductId: string,
  shopifyVariantId?: string
): Promise<{ canPropose: boolean; blockReason: string | null }> {
  const params = new URLSearchParams({ shopifyProductId });
  if (shopifyVariantId) params.set('shopifyVariantId', shopifyVariantId);
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/eligibility/kirma?${params}`,
    { credentials: apiCredentials }
  );
  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося праверыць магчымасць прапановы.')
    );
  }
  const data = (await res.json()) as Record<string, unknown>;
  return {
    canPropose: Boolean(data.canPropose ?? data.CanPropose),
    blockReason:
      typeof data.blockReason === 'string'
        ? data.blockReason
        : typeof data.BlockReason === 'string'
          ? data.BlockReason
          : null,
  };
}

export async function fetchKirmaBukinistkaOffers(): Promise<
  KirmaBukinistkaOffer[]
> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/offers`, {
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць прапановы.')
    );
  }

  const data = (await res.json()) as unknown;
  const list = Array.isArray(data) ? data : [];
  return list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map(mapOffer)
    .filter((o) => o.id > 0);
}

/** Kirma panel: offers sent to Bukinistka. */
export async function fetchKirmaSentBukinistkaOffers(): Promise<
  KirmaBukinistkaOffer[]
> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/offers/sent`, {
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць высланыя прапановы.')
    );
  }

  const data = (await res.json()) as unknown;
  const list = Array.isArray(data) ? data : [];
  return list
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map(mapOffer)
    .filter((o) => o.id > 0);
}

export async function updateKirmaBukinistkaOffer(
  id: number,
  input: { quantity: number; grossUnitCost: number }
): Promise<KirmaBukinistkaOffer> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/offers/${id}`, {
    method: 'PUT',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      quantity: input.quantity,
      grossUnitCost: input.grossUnitCost,
    }),
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося абнавіць прапанову.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapOffer(data);
}

export async function updateBukinistkaSentOffer(
  id: number,
  input: { quantity: number; grossUnitCost: number }
): Promise<KirmaBukinistkaOffer> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/sent-by-bukinistka/${id}`,
    {
      method: 'PUT',
      credentials: apiCredentials,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        quantity: input.quantity,
        grossUnitCost: input.grossUnitCost,
      }),
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося абнавіць прапанову.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapOffer(data);
}

export async function applyOfferPriceChangeByKirma(
  id: number
): Promise<KirmaBukinistkaOffer> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${id}/apply-price-change`,
    {
      method: 'POST',
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося абнавіць кошт у Shopify.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapOffer(data);
}

export async function updateOfferShopifySalePriceByKirma(
  id: number,
  salePrice: number
): Promise<KirmaBukinistkaOffer> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${id}/shopify-sale-price`,
    {
      method: 'POST',
      credentials: apiCredentials,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ salePrice }),
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося абнавіць цану ў Shopify.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapOffer(data);
}

export async function applyOfferPriceChangeByBukinistka(
  id: number
): Promise<KirmaBukinistkaOffer> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${id}/apply-price-change-by-bukinistka`,
    {
      method: 'POST',
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося абнавіць кошт у Odoo.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapOffer(data);
}

export async function cancelKirmaBukinistkaOffer(id: number): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/offers/${id}`, {
    method: 'DELETE',
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося адмяніць прапанову.')
    );
  }
}

/** Bukinistka rejects a pending offer from Kirma. */
export async function rejectKirmaBukinistkaOffer(id: number): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/offers/${id}/reject`, {
    method: 'POST',
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося адхіліць прапанову.')
    );
  }
}

/** Bukinistka accepts a pending offer and links it to an Odoo product. */
export async function acceptKirmaBukinistkaOffer(
  id: number,
  input: {
    odooProductId: number;
    listPrice?: number | null;
    applyKirmaCostPrice?: boolean | null;
  }
): Promise<KirmaBukinistkaOffer> {
  const body: Record<string, unknown> = {
    odooProductId: input.odooProductId,
  };
  if (input.listPrice !== undefined && input.listPrice !== null) {
    body.listPrice = input.listPrice;
  }
  if (
    input.applyKirmaCostPrice !== undefined &&
    input.applyKirmaCostPrice !== null
  ) {
    body.applyKirmaCostPrice = input.applyKirmaCostPrice;
  }

  const res = await fetch(`${getApiBaseUrl()}/bukinistka/offers/${id}/accept`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося прыняць прапанову.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapOffer(data);
}

export type BukinistkaCreateProductPreview = {
  productName: string;
  productAuthor: string;
  grossUnitCost: number;
  shopifySalePrice: number | null;
  vendor: string | null;
  productType: string | null;
  barcodeDigits: string | null;
  weightKg: string | null;
};

export type BukinistkaCreateProductResult = {
  odooProductId: number;
  odooProductName: string;
  odooUrl: string;
  listPrice: number;
};

/** Bukinistka: Shopify preview before creating a new Odoo product. */
export async function fetchBukinistkaCreateProductPreview(
  offerId: number
): Promise<BukinistkaCreateProductPreview> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${offerId}/create-product-preview`,
    { credentials: apiCredentials }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(
        res,
        'Не ўдалося загрузіць даныя для новага прадукта.'
      )
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return {
    productName: readString(data.productName, data.product_name),
    productAuthor: readString(data.productAuthor, data.product_author),
    grossUnitCost: readNumber(data.grossUnitCost, data.gross_unit_cost),
    shopifySalePrice: readOptionalNumber(
      data.shopifySalePrice,
      data.shopify_sale_price
    ),
    vendor:
      typeof data.vendor === 'string' && data.vendor.trim()
        ? data.vendor.trim()
        : null,
    productType:
      typeof data.productType === 'string' && data.productType.trim()
        ? data.productType.trim()
        : typeof data.product_type === 'string' && data.product_type.trim()
          ? data.product_type.trim()
          : null,
    barcodeDigits:
      typeof data.barcodeDigits === 'string' && data.barcodeDigits.trim()
        ? data.barcodeDigits.trim()
        : typeof data.barcode_digits === 'string' && data.barcode_digits.trim()
          ? data.barcode_digits.trim()
          : null,
    weightKg:
      typeof data.weightKg === 'string' && data.weightKg.trim()
        ? data.weightKg.trim()
        : typeof data.weight_kg === 'string' && data.weight_kg.trim()
          ? data.weight_kg.trim()
          : null,
  };
}

export type CreateBukinistkaOdooProductOptions = {
  /** Skip barcode/ISBN on create (magazines often share barcodes in Odoo). */
  omitBarcode?: boolean;
  /** Override Shopify barcode; empty/whitespace omits barcode. */
  barcodeDigits?: string | null;
};

export class BukinistkaCreateProductError extends Error {
  code: string | null;

  constructor(message: string, code: string | null = null) {
    super(message);
    this.name = 'BukinistkaCreateProductError';
    this.code = code;
  }
}

/** Bukinistka: create a new Odoo product card for the offer. */
export async function createBukinistkaOdooProduct(
  offerId: number,
  listPrice: number,
  options?: CreateBukinistkaOdooProductOptions
): Promise<BukinistkaCreateProductResult> {
  const body: Record<string, unknown> = { listPrice };
  if (options?.omitBarcode === true) {
    body.omitBarcode = true;
  } else if (options?.barcodeDigits !== undefined) {
    body.barcodeDigits = options.barcodeDigits ?? '';
  }

  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/${offerId}/create-product`,
    {
      method: 'POST',
      credentials: apiCredentials,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }
  );

  if (!res.ok) {
    let message = 'Не ўдалося стварыць картку ў Odoo.';
    let code: string | null = null;
    try {
      const data = (await res.json()) as Record<string, unknown>;
      if (typeof data.error === 'string' && data.error.trim()) {
        message = data.error.trim();
      } else if (typeof data.message === 'string' && data.message.trim()) {
        message = data.message.trim();
      }
      if (typeof data.code === 'string' && data.code.trim()) {
        code = data.code.trim();
      }
    } catch {
      message = await readErrorMessage(res, message);
    }
    throw new BukinistkaCreateProductError(message, code);
  }

  const data = (await res.json()) as Record<string, unknown>;
  return {
    odooProductId: readNumber(data.odooProductId, data.odoo_product_id),
    odooProductName: readString(data.odooProductName, data.odoo_product_name),
    odooUrl: readString(data.odooUrl, data.odoo_url),
    listPrice: readNumber(data.listPrice, data.list_price),
  };
}

export type BukinistkaOfferReceiptLineInput = {
  offerId: number;
  odooProductId: number;
  odooProductName?: string;
  listPrice?: number | null;
  applyKirmaCostPrice?: boolean | null;
};

export type BukinistkaOfferReceiptResult = {
  pickingId: number;
  pickingName: string;
  offers: KirmaBukinistkaOffer[];
};

export type BukinistkaReceiptDraftLine = {
  offerId: number;
  odooProductId: number;
  odooProductName: string;
  listPrice: number | null;
  applyKirmaCostPrice: boolean | null;
};

export type BukinistkaReceiptDraft = {
  id: number;
  status: 'Open' | 'Failed' | 'Completed' | string;
  lastError: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  lines: BukinistkaReceiptDraftLine[];
};

function mapReceiptDraftLine(
  row: Record<string, unknown>
): BukinistkaReceiptDraftLine {
  const listRaw = row.listPrice ?? row.ListPrice;
  const applyRaw = row.applyKirmaCostPrice ?? row.ApplyKirmaCostPrice;
  return {
    offerId: readNumber(row.offerId, row.OfferId),
    odooProductId: readNumber(row.odooProductId, row.OdooProductId),
    odooProductName: readString(row.odooProductName, row.OdooProductName),
    listPrice:
      listRaw === null || listRaw === undefined ? null : readNumber(listRaw),
    applyKirmaCostPrice:
      typeof applyRaw === 'boolean'
        ? applyRaw
        : applyRaw === null || applyRaw === undefined
          ? null
          : Boolean(applyRaw),
  };
}

function mapReceiptDraft(
  data: Record<string, unknown>
): BukinistkaReceiptDraft {
  const linesRaw = (data.lines ?? data.Lines ?? []) as unknown[];
  return {
    id: readNumber(data.id, data.Id),
    status: readString(data.status, data.Status) || 'Open',
    lastError: (() => {
      const v = data.lastError ?? data.LastError;
      return typeof v === 'string' && v.trim() ? v : null;
    })(),
    createdAtUtc: readString(data.createdAtUtc, data.CreatedAtUtc),
    updatedAtUtc: readString(data.updatedAtUtc, data.UpdatedAtUtc),
    lines: linesRaw
      .filter(
        (item): item is Record<string, unknown> =>
          !!item && typeof item === 'object'
      )
      .map(mapReceiptDraftLine)
      .filter((l) => l.offerId > 0 && l.odooProductId > 0),
  };
}

/** Bukinistka: current Open/Failed receipt draft, or null. */
export async function fetchBukinistkaReceiptDraft(): Promise<BukinistkaReceiptDraft | null> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/receipt-draft`,
    {
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць чарнавік прыёмкі.')
    );
  }

  // Ok(null) may be 204 / empty body — treat as no draft.
  if (res.status === 204) return null;
  const text = (await res.text()).trim();
  if (!text || text === 'null') return null;

  const data = JSON.parse(text) as unknown;
  if (data === null || data === undefined) return null;
  if (typeof data !== 'object') return null;
  return mapReceiptDraft(data as Record<string, unknown>);
}

/** Bukinistka: upsert active receipt draft (full line replace). */
export async function upsertBukinistkaReceiptDraft(
  lines: BukinistkaOfferReceiptLineInput[]
): Promise<BukinistkaReceiptDraft> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/receipt-draft`,
    {
      method: 'PUT',
      credentials: apiCredentials,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        lines: lines.map((line) => ({
          offerId: line.offerId,
          odooProductId: line.odooProductId,
          odooProductName: line.odooProductName || '',
          listPrice:
            line.listPrice === undefined || line.listPrice === null
              ? null
              : line.listPrice,
          applyKirmaCostPrice:
            line.applyKirmaCostPrice === undefined
              ? null
              : line.applyKirmaCostPrice,
        })),
      }),
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося захаваць чарнавік прыёмкі.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  return mapReceiptDraft(data);
}

/** Bukinistka: discard active Open/Failed receipt draft. */
export async function deleteBukinistkaReceiptDraft(): Promise<void> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/receipt-draft`,
    {
      method: 'DELETE',
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося выдаліць чарнавік прыёмкі.')
    );
  }
}

/** Bukinistka saves a batch Odoo receipt and accepts linked offers. */
export async function saveBukinistkaOfferReceipt(
  lines: BukinistkaOfferReceiptLineInput[]
): Promise<BukinistkaOfferReceiptResult> {
  const res = await fetch(`${getApiBaseUrl()}/bukinistka/offers/receipt`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      lines: lines.map((line) => ({
        offerId: line.offerId,
        odooProductId: line.odooProductId,
        listPrice:
          line.listPrice === undefined || line.listPrice === null
            ? null
            : line.listPrice,
        applyKirmaCostPrice:
          line.applyKirmaCostPrice === undefined
            ? null
            : line.applyKirmaCostPrice,
      })),
    }),
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося захаваць прыёмку.')
    );
  }

  const data = (await res.json()) as Record<string, unknown>;
  const pickingId = readNumber(data.pickingId, data.picking_id);
  const pickingName = readString(data.pickingName, data.picking_name);
  const offersRaw = (data.offers ?? data.Offers ?? []) as unknown[];
  const offers = offersRaw
    .filter(
      (item): item is Record<string, unknown> =>
        !!item && typeof item === 'object'
    )
    .map(mapOffer)
    .filter((o) => o.id > 0);

  return {
    pickingId,
    pickingName: pickingName || (pickingId > 0 ? `#${pickingId}` : ''),
    offers,
  };
}

/** Kirma admin: number of pending (unprocessed) offers from Bukinistka. */
export async function fetchKirmaReceivedPendingOffersCount(): Promise<number> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/received/pending-count`,
    {
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць колькасць прапаноў.')
    );
  }

  const data = (await res.json()) as { count?: unknown; Count?: unknown };
  const n = Number(data.count ?? data.Count ?? 0);
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
}

export function notifyBukinistkaOffersChanged(): void {
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new Event('bukinistka-offers-changed'));
  }
}

/** Bukinistka: number of pending (unprocessed) offers from Kirma. */
export async function fetchBukinistkaPendingOffersCount(): Promise<number> {
  const res = await fetch(
    `${getApiBaseUrl()}/bukinistka/offers/pending-count`,
    {
      credentials: apiCredentials,
    }
  );

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць колькасць прапаноў.')
    );
  }

  const data = (await res.json()) as { count?: unknown; Count?: unknown };
  const n = Number(data.count ?? data.Count ?? 0);
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
}
