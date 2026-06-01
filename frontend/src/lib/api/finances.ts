import { apiCredentials, getApiBaseUrl, readErrorMessage } from '@/lib/api/common';

export type FinanceMovementKind =
  | 'outgoingTransfer'
  | 'incomingTransfer'
  | 'payment'
  | 'kirmaPayout'
  | 'debtToKirma'
  | 'debtFromKirma';

export type FinancePerson = {
  id: number;
  name: string;
  sortOrder: number;
};

export type FinanceMovement = {
  id: number;
  personId: number;
  kind: FinanceMovementKind;
  amount: number;
  description: string;
  movementDate: string;
  isFromRecurring: boolean;
  recurringExpenseId: number | null;
};

export type FinanceRecurringExpense = {
  id: number;
  personId: number;
  kind: FinanceMovementKind;
  amount: number;
  description: string;
  dayOfMonth: number;
  isActive: boolean;
};

export type FinanceSummary = {
  totalOutgoingTransfer: number;
  totalIncomingTransfer: number;
  totalPayment: number;
  totalKirmaPayout: number;
  personOwesKirma: number;
  kirmaOwesPerson: number;
};

export type FinancePersonOverview = {
  person: FinancePerson;
  summary: FinanceSummary;
  movements: FinanceMovement[];
  recurringExpenses: FinanceRecurringExpense[];
};

function pick<T>(row: Record<string, unknown>, camel: string, pascal: string): T {
  return (row[camel] ?? row[pascal]) as T;
}

function mapPerson(row: Record<string, unknown>): FinancePerson {
  return {
    id: Number(pick(row, 'id', 'Id')) || 0,
    name: String(pick(row, 'name', 'Name') ?? ''),
    sortOrder: Number(pick(row, 'sortOrder', 'SortOrder')) || 0,
  };
}

function mapMovementKind(raw: unknown): FinanceMovementKind {
  const value = String(raw ?? 'payment');
  if (
    value === 'outgoingTransfer' ||
    value === 'incomingTransfer' ||
    value === 'payment' ||
    value === 'kirmaPayout' ||
    value === 'debtToKirma' ||
    value === 'debtFromKirma'
  ) {
    return value;
  }
  const lower = value.toLowerCase();
  if (lower === 'outgoingtransfer') return 'outgoingTransfer';
  if (lower === 'incomingtransfer') return 'incomingTransfer';
  if (lower === 'kirmapayout') return 'kirmaPayout';
  if (lower === 'debttokirma') return 'debtToKirma';
  if (lower === 'debtfromkirma') return 'debtFromKirma';
  return 'payment';
}

function mapMovement(row: Record<string, unknown>): FinanceMovement {
  return {
    id: Number(pick(row, 'id', 'Id')) || 0,
    personId: Number(pick(row, 'personId', 'PersonId')) || 0,
    kind: mapMovementKind(pick(row, 'kind', 'Kind')),
    amount: Number(pick(row, 'amount', 'Amount')) || 0,
    description: String(pick(row, 'description', 'Description') ?? ''),
    movementDate: String(pick(row, 'movementDate', 'MovementDate') ?? ''),
    isFromRecurring: Boolean(pick(row, 'isFromRecurring', 'IsFromRecurring')),
    recurringExpenseId:
      pick<number | null>(row, 'recurringExpenseId', 'RecurringExpenseId') ?? null,
  };
}

function mapRecurring(row: Record<string, unknown>): FinanceRecurringExpense {
  return {
    id: Number(pick(row, 'id', 'Id')) || 0,
    personId: Number(pick(row, 'personId', 'PersonId')) || 0,
    kind: mapMovementKind(pick(row, 'kind', 'Kind')),
    amount: Number(pick(row, 'amount', 'Amount')) || 0,
    description: String(pick(row, 'description', 'Description') ?? ''),
    dayOfMonth: Number(pick(row, 'dayOfMonth', 'DayOfMonth')) || 1,
    isActive: Boolean(pick(row, 'isActive', 'IsActive') ?? true),
  };
}

function mapSummary(row: Record<string, unknown>): FinanceSummary {
  const personOwes =
    Number(pick(row, 'personOwesKirma', 'PersonOwesKirma')) ||
    Number(pick(row, 'totalDebtToKirma', 'TotalDebtToKirma')) ||
    0;
  const kirmaOwes =
    Number(pick(row, 'kirmaOwesPerson', 'KirmaOwesPerson')) ||
    Number(pick(row, 'totalDebtFromKirma', 'TotalDebtFromKirma')) ||
    0;
  return {
    totalOutgoingTransfer: Number(pick(row, 'totalOutgoingTransfer', 'TotalOutgoingTransfer')) || 0,
    totalIncomingTransfer: Number(pick(row, 'totalIncomingTransfer', 'TotalIncomingTransfer')) || 0,
    totalPayment: Number(pick(row, 'totalPayment', 'TotalPayment')) || 0,
    totalKirmaPayout: Number(pick(row, 'totalKirmaPayout', 'TotalKirmaPayout')) || 0,
    personOwesKirma: personOwes,
    kirmaOwesPerson: kirmaOwes,
  };
}

/** Types available when creating/editing a movement. */
export const MOVEMENT_KIND_OPTIONS: { value: FinanceMovementKind; label: string }[] = [
  { value: 'payment', label: 'Аплата' },
  { value: 'kirmaPayout', label: 'Выплата' },
];

const MOVEMENT_KIND_LABELS: Record<FinanceMovementKind, string> = {
  outgoingTransfer: 'Аплата',
  incomingTransfer: 'Выплата',
  payment: 'Аплата',
  kirmaPayout: 'Выплата',
  debtToKirma: 'Даўг (устар.)',
  debtFromKirma: 'Даўг (устар.)',
};

export function movementKindLabel(kind: FinanceMovementKind): string {
  return MOVEMENT_KIND_LABELS[kind] ?? kind;
}

export function formatMoney(value: number): string {
  return new Intl.NumberFormat('pl-PL', {
    style: 'currency',
    currency: 'PLN',
    minimumFractionDigits: 2,
  }).format(value);
}

export async function fetchFinancePersons(): Promise<FinancePerson[]> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/persons`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося загрузіць спіс асоб'));
  }
  const data = (await res.json()) as unknown;
  if (!Array.isArray(data)) return [];
  return data.map((row) => mapPerson(row as Record<string, unknown>));
}

export async function createFinancePerson(name: string): Promise<FinancePerson> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/persons`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({ name }),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося дадаць асобу'));
  }
  return mapPerson((await res.json()) as Record<string, unknown>);
}

export async function updateFinancePerson(id: number, name: string): Promise<FinancePerson> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/persons/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify({ name }),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося абнавіць асобу'));
  }
  return mapPerson((await res.json()) as Record<string, unknown>);
}

export async function deleteFinancePerson(id: number): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/persons/${id}`, {
    method: 'DELETE',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося выдаліць асобу'));
  }
}

export async function fetchFinanceOverview(personId: number): Promise<FinancePersonOverview> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/persons/${personId}/overview`, {
    method: 'GET',
    credentials: apiCredentials,
    cache: 'no-store',
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося загрузіць фінансы'));
  }
  const data = (await res.json()) as Record<string, unknown>;
  const person = mapPerson((data.person ?? data.Person) as Record<string, unknown>);
  const summary = mapSummary((data.summary ?? data.Summary) as Record<string, unknown>);
  const movementsRaw = (data.movements ?? data.Movements) as unknown;
  const recurringRaw = (data.recurringExpenses ?? data.RecurringExpenses) as unknown;
  return {
    person,
    summary,
    movements: Array.isArray(movementsRaw)
      ? movementsRaw.map((row) => mapMovement(row as Record<string, unknown>))
      : [],
    recurringExpenses: Array.isArray(recurringRaw)
      ? recurringRaw.map((row) => mapRecurring(row as Record<string, unknown>))
      : [],
  };
}

export type FinanceMovementPayload = {
  personId: number;
  kind: FinanceMovementKind;
  amount: number;
  description: string;
  movementDate: string;
};

export async function createFinanceMovement(payload: FinanceMovementPayload): Promise<FinanceMovement> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/movements`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося дадаць рух'));
  }
  return mapMovement((await res.json()) as Record<string, unknown>);
}

export async function updateFinanceMovement(
  id: number,
  payload: Omit<FinanceMovementPayload, 'personId'>,
): Promise<FinanceMovement> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/movements/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося абнавіць рух'));
  }
  return mapMovement((await res.json()) as Record<string, unknown>);
}

export async function deleteFinanceMovement(id: number): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/movements/${id}`, {
    method: 'DELETE',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося выдаліць рух'));
  }
}

export type FinanceRecurringPayload = {
  personId: number;
  kind: FinanceMovementKind;
  amount: number;
  description: string;
  dayOfMonth: number;
};

export async function createFinanceRecurring(payload: FinanceRecurringPayload): Promise<FinanceRecurringExpense> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/recurring`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося дадаць рэгулярны расход'));
  }
  return mapRecurring((await res.json()) as Record<string, unknown>);
}

export async function updateFinanceRecurring(
  id: number,
  payload: Omit<FinanceRecurringPayload, 'personId'> & { isActive: boolean },
): Promise<FinanceRecurringExpense> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/recurring/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    credentials: apiCredentials,
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося абнавіць рэгулярны расход'));
  }
  return mapRecurring((await res.json()) as Record<string, unknown>);
}

export async function deleteFinanceRecurring(id: number): Promise<void> {
  const res = await fetch(`${getApiBaseUrl()}/Finances/recurring/${id}`, {
    method: 'DELETE',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося выдаліць рэгулярны расход'));
  }
}
