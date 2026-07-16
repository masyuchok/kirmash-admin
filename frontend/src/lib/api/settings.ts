import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';

export type InvoiceSettingsPayload = {
  companyName: string;
  address: string;
  email: string;
  website: string;
  nip: string;
  currency: string;
};

export type ExpenseInvoiceType = {
  id: number;
  name: string;
  isSystem: boolean;
};

export type VatAutoFinanceSettings = {
  isEnabled: boolean;
  financePersonId: number | null;
};

export async function fetchInvoiceSettings(): Promise<InvoiceSettingsPayload> {
  const res = await fetch(`${getApiBaseUrl()}/Settings/invoice`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося загрузіць налады фактур'
    );
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

export async function saveInvoiceSettings(
  payload: InvoiceSettingsPayload
): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Settings/invoice`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося захаваць налады фактур'
    );
    throw new Error(msg);
  }
}

export async function fetchExpenseInvoiceTypes(): Promise<
  ExpenseInvoiceType[]
> {
  const res = await fetch(`${getApiBaseUrl()}/Settings/invoice-expense-types`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося загрузіць тыпы расходных фактур'
    );
    throw new Error(msg);
  }
  const data = (await res.json()) as unknown;
  if (!Array.isArray(data)) return [];
  return data.map((row) => {
    const item = row as Record<string, unknown>;
    return {
      id: Number(item.id ?? item.Id ?? 0) || 0,
      name: String(item.name ?? item.Name ?? ''),
      isSystem: Boolean(item.isSystem ?? item.IsSystem ?? false),
    };
  });
}

export async function createExpenseInvoiceType(name: string): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Settings/invoice-expense-types`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({ name }),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося дадаць тып расходнай фактуры'
    );
    throw new Error(msg);
  }
}

export async function updateExpenseInvoiceType(
  id: number,
  name: string
): Promise<void> {
  const res = await fetch(
    `${getApiBaseUrl()}/Settings/invoice-expense-types/${id}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      credentials: apiCredentials,
      body: JSON.stringify({ name }),
    }
  );
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося абнавіць тып расходнай фактуры'
    );
    throw new Error(msg);
  }
}

export async function fetchVatAutoFinanceSettings(): Promise<VatAutoFinanceSettings> {
  const res = await fetch(`${getApiBaseUrl()}/Settings/vat-auto-finance`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося загрузіць налады аўтарасходу VAT'
    );
    throw new Error(msg);
  }
  const data = (await res.json()) as Record<string, unknown>;
  const personId = Number(data.financePersonId ?? data.FinancePersonId ?? 0);
  return {
    isEnabled: Boolean(data.isEnabled ?? data.IsEnabled),
    financePersonId: personId > 0 ? personId : null,
  };
}

export async function saveVatAutoFinanceSettings(
  payload: VatAutoFinanceSettings
): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Settings/vat-auto-finance`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({
      isEnabled: payload.isEnabled,
      financePersonId: payload.isEnabled ? payload.financePersonId : null,
    }),
  });
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося захаваць налады аўтарасходу VAT'
    );
    throw new Error(msg);
  }
}

export async function deleteExpenseInvoiceType(id: number): Promise<void> {
  const res = await fetch(
    `${getApiBaseUrl()}/Settings/invoice-expense-types/${id}`,
    {
      method: 'DELETE',
      credentials: apiCredentials,
    }
  );
  if (!res.ok) {
    const msg = await readErrorMessage(
      res,
      'Не ўдалося выдаліць тып расходнай фактуры'
    );
    throw new Error(msg);
  }
}
