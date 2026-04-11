import type { SupplyListItem } from '@/types/supply';
import { apiCredentials, getApiBaseUrl, readErrorMessage } from '@/lib/api/common';

function readInt(v: unknown): number {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '') {
    const n = Number(v);
    return Number.isFinite(n) ? n : 0;
  }
  return 0;
}

/** Raw element from GET /Supply (camelCase or PascalCase). */
function mapSupplyJsonToListItem(raw: unknown): SupplyListItem {
  const r = raw as Record<string, unknown>;

  const idRaw = r.id ?? r.Id;
  const idNum =
    typeof idRaw === 'number'
      ? idRaw
      : typeof idRaw === 'string'
        ? Number(idRaw)
        : NaN;

  const nameRaw = r.supplierName ?? r.SupplierName;
  const supplierName =
    typeof nameRaw === 'string' && nameRaw.length > 0 ? nameRaw : '—';

  const booksNumber = readInt(r.booksNumber ?? r.BooksNumber);

  const dateVal = r.date ?? r.Date;
  let date = '';
  if (typeof dateVal === 'string') {
    date = dateVal;
  } else if (dateVal && typeof dateVal === 'object') {
    const d = dateVal as { year?: number; month?: number; day?: number };
    if (d.year != null && d.month != null && d.day != null) {
      date = `${String(d.year).padStart(4, '0')}-${String(d.month).padStart(2, '0')}-${String(d.day).padStart(2, '0')}`;
    }
  }

  return {
    id: String(Number.isFinite(idNum) ? idNum : ''),
    supplierName,
    date,
    booksNumber,
  };
}

/**
 * GET /Supply — matches `SupplyController` route `[controller]` → "Supply".
 */
export async function fetchSupplies(): Promise<SupplyListItem[]> {
  const res = await fetch(`${getApiBaseUrl()}/Supply`, {
    method: 'GET',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць пастаўкі');
    throw new Error(msg);
  }
  const data = (await res.json()) as unknown;
  if (!Array.isArray(data)) {
    throw new Error('Некарэктны адказ сервера');
  }
  return data.map(mapSupplyJsonToListItem);
}
