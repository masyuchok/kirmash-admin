/**
 * Supplier HTTP helpers. Point `NEXT_PUBLIC_API_URL` at your API.
 * - createSupplier → POST /suppliers/add (existing)
 * - fetchSupplierById → GET /suppliers/:id (optional; falls back to GET /suppliers + pick id)
 * - updateSupplier → PATCH /suppliers/:id (adjust method/path/body when backend is ready)
 */
import type { Supplier } from '@/types/supplier';
import {
  type SupplierFormValues,
  mapApiDetailToFormValues,
  mapListSupplierToFormValues,
} from '@/lib/suppliers/supplierFormTypes';

const baseUrl = () => {
  const url = process.env.NEXT_PUBLIC_API_URL;
  if (!url) throw new Error('NEXT_PUBLIC_API_URL is not set');
  return url.replace(/\/$/, '');
};

/** Response shape for GET /suppliers/:id — extend when backend is ready. */
export type SupplierApiDetail = Partial<SupplierFormValues> & {
  id: number;
  telegram?: string;
  isVatPayer?: boolean;
};

function readErrorMessage(res: Response, fallback: string): Promise<string> {
  return res
    .json()
    .then((data) => (typeof data?.error === 'string' ? data.error : data?.message) || fallback)
    .catch(() => res.text().catch(() => fallback));
}

export async function createSupplier(
  payload: SupplierFormValues
): Promise<{ id?: number }> {
  const res = await fetch(`${baseUrl()}/suppliers/add`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Памылка пры захаванні');
    throw new Error(msg);
  }
  const data = (await res.json().catch(() => ({}))) as { id?: number };
  return data;
}

/**
 * Placeholder for the future update endpoint.
 * Adjust `method` and path to match your backend (e.g. PUT /suppliers/:id).
 */
export async function updateSupplier(
  id: number,
  payload: SupplierFormValues
): Promise<void> {
  const res = await fetch(`${baseUrl()}/suppliers/${id}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Памылка пры абнаўленні');
    throw new Error(msg);
  }
}

/**
 * Load one supplier for editing. Falls back to list fetch + merge if GET by id fails (optional UX).
 */
export async function fetchSupplierById(id: number): Promise<SupplierFormValues> {
  const res = await fetch(`${baseUrl()}/suppliers/${id}`, {
    credentials: 'include',
  });
  if (res.ok) {
    const detail = (await res.json()) as SupplierApiDetail;
    return mapApiDetailToFormValues({ ...detail, id: detail.id ?? id });
  }

  // Fallback: use list endpoint and pick row (works before dedicated GET exists).
  const listRes = await fetch(`${baseUrl()}/suppliers`, { credentials: 'include' });
  if (!listRes.ok) {
    const msg = await readErrorMessage(listRes, 'Не ўдалося загрузіць пастаўшчыка');
    throw new Error(msg);
  }
  const list = (await listRes.json()) as Supplier[];
  const row = list.find((s) => s.id === id);
  if (!row) {
    throw new Error('Пастаўшчык не знойдзены');
  }
  return mapListSupplierToFormValues(row);
}
