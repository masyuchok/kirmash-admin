import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';
import { readNumber } from '@/lib/api/json';

export type KirmashListItem = {
  id: number;
  title: string;
  description: string;
  eventDate: string;
  status: string;
  linesCount: number;
  tagsCount: number;
  unitsCount: number;
  createdAtUtc: string;
};

export type KirmashLine = {
  id: number;
  shopifyProductId: string;
  shopifyVariantId: string;
  title: string;
  unitPrice: number;
  quantity: number;
};

export type KirmashDetail = {
  id: number;
  title: string;
  description: string;
  eventDate: string;
  status: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  lines: KirmashLine[];
  priceTagsCount: number;
};

export type KirmashPriceTag = {
  id: number;
  kirmashLineId: number;
  sequence: number;
  title: string;
  unitPrice: number;
  checkoutUrl: string;
};

export type KirmashUpsertPayload = {
  title: string;
  description: string;
  eventDate: string;
  lines: Array<{
    shopifyProductId: string;
    shopifyVariantId: string;
    title: string;
    unitPrice: number;
    quantity: number;
  }>;
};

function mapListItem(raw: Record<string, unknown>): KirmashListItem {
  return {
    id: readNumber(raw.id ?? raw.Id),
    title: String(raw.title ?? raw.Title ?? ''),
    description: String(raw.description ?? raw.Description ?? ''),
    eventDate: String(raw.eventDate ?? raw.EventDate ?? ''),
    status: String(raw.status ?? raw.Status ?? ''),
    linesCount: readNumber(raw.linesCount ?? raw.LinesCount),
    tagsCount: readNumber(raw.tagsCount ?? raw.TagsCount),
    unitsCount: readNumber(raw.unitsCount ?? raw.UnitsCount),
    createdAtUtc: String(raw.createdAtUtc ?? raw.CreatedAtUtc ?? ''),
  };
}

function mapLine(raw: Record<string, unknown>): KirmashLine {
  return {
    id: readNumber(raw.id ?? raw.Id),
    shopifyProductId: String(
      raw.shopifyProductId ?? raw.ShopifyProductId ?? ''
    ),
    shopifyVariantId: String(
      raw.shopifyVariantId ?? raw.ShopifyVariantId ?? ''
    ),
    title: String(raw.title ?? raw.Title ?? ''),
    unitPrice: readNumber(raw.unitPrice ?? raw.UnitPrice),
    quantity: Math.max(
      1,
      Math.trunc(readNumber(raw.quantity ?? raw.Quantity) || 1)
    ),
  };
}

function mapDetail(raw: Record<string, unknown>): KirmashDetail {
  const linesRaw = raw.lines ?? raw.Lines;
  return {
    id: readNumber(raw.id ?? raw.Id),
    title: String(raw.title ?? raw.Title ?? ''),
    description: String(raw.description ?? raw.Description ?? ''),
    eventDate: String(raw.eventDate ?? raw.EventDate ?? ''),
    status: String(raw.status ?? raw.Status ?? ''),
    createdAtUtc: String(raw.createdAtUtc ?? raw.CreatedAtUtc ?? ''),
    updatedAtUtc: String(raw.updatedAtUtc ?? raw.UpdatedAtUtc ?? ''),
    priceTagsCount: readNumber(raw.priceTagsCount ?? raw.PriceTagsCount),
    lines: Array.isArray(linesRaw)
      ? linesRaw.map((row) => mapLine(row as Record<string, unknown>))
      : [],
  };
}

function mapTag(raw: Record<string, unknown>): KirmashPriceTag {
  return {
    id: readNumber(raw.id ?? raw.Id),
    kirmashLineId: readNumber(raw.kirmashLineId ?? raw.KirmashLineId),
    sequence: readNumber(raw.sequence ?? raw.Sequence),
    title: String(raw.title ?? raw.Title ?? ''),
    unitPrice: readNumber(raw.unitPrice ?? raw.UnitPrice),
    checkoutUrl: String(raw.checkoutUrl ?? raw.CheckoutUrl ?? ''),
  };
}

export async function fetchKirmashes(): Promise<KirmashListItem[]> {
  const res = await fetch(`${getApiBaseUrl()}/Kirmashes`, {
    credentials: apiCredentials,
  });
  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць кірмашы')
    );
  }
  const data = (await res.json()) as unknown;
  return Array.isArray(data)
    ? data.map((row) => mapListItem(row as Record<string, unknown>))
    : [];
}

export async function fetchKirmash(id: number): Promise<KirmashDetail> {
  const res = await fetch(`${getApiBaseUrl()}/Kirmashes/${id}`, {
    credentials: apiCredentials,
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося загрузіць кірмаш'));
  }
  return mapDetail((await res.json()) as Record<string, unknown>);
}

export async function createKirmash(
  payload: KirmashUpsertPayload
): Promise<KirmashDetail> {
  const res = await fetch(`${getApiBaseUrl()}/Kirmashes`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    let message = 'Не ўдалося стварыць кірмаш';
    try {
      const body = (await res.json()) as { error?: string; details?: string };
      if (typeof body.error === 'string' && body.error.trim()) {
        message = body.error.trim();
      }
      if (typeof body.details === 'string' && body.details.trim()) {
        message = `${message}: ${body.details.trim()}`;
      }
    } catch {
      // keep fallback
    }
    throw new Error(message);
  }
  return mapDetail((await res.json()) as Record<string, unknown>);
}

export async function updateKirmash(
  id: number,
  payload: KirmashUpsertPayload
): Promise<KirmashDetail> {
  const res = await fetch(`${getApiBaseUrl()}/Kirmashes/${id}`, {
    method: 'PUT',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося захаваць кірмаш'));
  }
  return mapDetail((await res.json()) as Record<string, unknown>);
}

export async function deleteKirmash(id: number): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Kirmashes/${id}`, {
    method: 'DELETE',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося выдаліць кірмаш'));
  }
}

export async function fetchKirmashPriceTags(
  id: number
): Promise<KirmashPriceTag[]> {
  const res = await fetch(`${getApiBaseUrl()}/Kirmashes/${id}/price-tags`, {
    credentials: apiCredentials,
  });
  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць цэннікі')
    );
  }
  const data = (await res.json()) as unknown;
  return Array.isArray(data)
    ? data.map((row) => mapTag(row as Record<string, unknown>))
    : [];
}

export async function generateKirmashPriceTags(
  id: number
): Promise<KirmashPriceTag[]> {
  const res = await fetch(
    `${getApiBaseUrl()}/Kirmashes/${id}/price-tags/generate`,
    {
      method: 'POST',
      credentials: apiCredentials,
    }
  );
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося стварыць цэннікі'));
  }
  const data = (await res.json()) as unknown;
  return Array.isArray(data)
    ? data.map((row) => mapTag(row as Record<string, unknown>))
    : [];
}
