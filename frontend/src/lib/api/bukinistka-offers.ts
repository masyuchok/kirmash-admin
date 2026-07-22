import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';

export type KirmaBukinistkaOffer = {
  id: number;
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
  createdAtUtc: string;
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
    createdAtUtc: readString(row.createdAtUtc, row.created_at_utc),
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

export type BukinistkaOfferReceiptLineInput = {
  offerId: number;
  odooProductId: number;
  listPrice?: number | null;
  applyKirmaCostPrice?: boolean | null;
};

export type BukinistkaOfferReceiptResult = {
  pickingId: number;
  pickingName: string;
  offers: KirmaBukinistkaOffer[];
};

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
