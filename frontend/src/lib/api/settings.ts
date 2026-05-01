import { apiCredentials, getApiBaseUrl, readErrorMessage } from '@/lib/api/common';

export type InvoiceSettingsPayload = {
  companyName: string;
  address: string;
  email: string;
  website: string;
  nip: string;
  currency: string;
};

export async function fetchInvoiceSettings(): Promise<InvoiceSettingsPayload> {
  const res = await fetch(`${getApiBaseUrl()}/Settings/invoice`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося загрузіць налады фактур');
    throw new Error(msg);
  }

  const data = (await res.json()) as Record<string, unknown>;
  const currencyRaw = String(data.currency ?? data.Currency ?? '').trim();
  return {
    companyName: String(data.companyName ?? data.CompanyName ?? ''),
    address: String(data.address ?? data.Address ?? ''),
    email: String(data.email ?? data.Email ?? ''),
    website: String(data.website ?? data.Website ?? ''),
    nip: String(data.nip ?? data.Nip ?? ''),
    currency: currencyRaw || 'PLN',
  };
}

export async function saveInvoiceSettings(payload: InvoiceSettingsPayload): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Settings/invoice`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(res, 'Не ўдалося захаваць налады фактур');
    throw new Error(msg);
  }
}
