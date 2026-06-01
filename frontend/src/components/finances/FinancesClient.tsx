'use client';

import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { useTopbar } from '@/components/topbar/TopbarContext';
import {
  createFinanceMovement,
  createFinancePerson,
  createFinanceRecurring,
  deleteFinanceMovement,
  deleteFinancePerson,
  deleteFinanceRecurring,
  fetchFinanceOverview,
  fetchFinancePersons,
  formatMoney,
  movementKindLabel,
  MOVEMENT_KIND_OPTIONS,
  updateFinanceMovement,
  updateFinanceRecurring,
  type FinanceMovement,
  type FinanceMovementKind,
  type FinancePerson,
  type FinancePersonOverview,
  type FinanceRecurringExpense,
} from '@/lib/api/finances';
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { FiChevronDown, FiChevronRight, FiEdit2, FiPlus, FiTrash2, FiX } from 'react-icons/fi';

function todayIso(): string {
  const d = new Date();
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

type MovementFieldsState = {
  kind: FinanceMovementKind;
  amount: string;
  description: string;
};

type MovementFormState = MovementFieldsState & {
  movementDate: string;
};

const emptyMovementForm = (): MovementFormState => ({
  kind: 'payment',
  amount: '',
  description: '',
  movementDate: todayIso(),
});

type RecurringFormState = MovementFieldsState & {
  dayOfMonth: string;
  isActive: boolean;
};

const emptyRecurringForm = (): RecurringFormState => ({
  kind: 'payment',
  amount: '',
  description: '',
  dayOfMonth: '1',
  isActive: true,
});

export default function FinancesClient() {
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [persons, setPersons] = useState<FinancePerson[]>([]);
  const [activePersonId, setActivePersonId] = useState<number | null>(null);
  const [overview, setOverview] = useState<FinancePersonOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [overviewLoading, setOverviewLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [movementFormOpen, setMovementFormOpen] = useState(false);
  const [editingMovement, setEditingMovement] = useState<FinanceMovement | null>(null);
  const [movementForm, setMovementForm] = useState<MovementFormState>(emptyMovementForm);

  const [recurringFormOpen, setRecurringFormOpen] = useState(false);
  const [editingRecurring, setEditingRecurring] = useState<FinanceRecurringExpense | null>(null);
  const [recurringForm, setRecurringForm] = useState<RecurringFormState>(emptyRecurringForm);

  const [newPersonName, setNewPersonName] = useState('');

  useEffect(() => {
    setTopbarPage({
      title: 'Фінансы',
      subtitle: 'Рухі сродкаў і даўгі па асобах',
    });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [setTopbarButtons, setTopbarPage]);

  const loadOverview = useCallback(async (personId: number) => {
    setOverviewLoading(true);
    setError(null);
    try {
      const data = await fetchFinanceOverview(personId);
      setOverview(data);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка загрузкі');
    } finally {
      setOverviewLoading(false);
    }
  }, []);

  const loadPersons = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const list = await fetchFinancePersons();
      setPersons(list);
      setActivePersonId((prev) => {
        if (prev && list.some((p) => p.id === prev)) return prev;
        return list[0]?.id ?? null;
      });
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка загрузкі');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadPersons();
  }, [loadPersons]);

  useEffect(() => {
    if (activePersonId == null) {
      setOverview(null);
      return;
    }
    void loadOverview(activePersonId);
  }, [activePersonId, loadOverview]);

  const refresh = async () => {
    if (activePersonId == null) return;
    await loadOverview(activePersonId);
  };

  const handleAddPerson = async () => {
    const name = newPersonName.trim();
    if (!name) return;
    setSaving(true);
    setError(null);
    try {
      const created = await createFinancePerson(name);
      setNewPersonName('');
      await loadPersons();
      setActivePersonId(created.id);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка');
    } finally {
      setSaving(false);
    }
  };

  const handleDeletePerson = async (id: number) => {
    if (!window.confirm('Выдаліць гэтую асобу і ўсе яе фінансавыя дадзеныя?')) return;
    setSaving(true);
    setError(null);
    try {
      await deleteFinancePerson(id);
      await loadPersons();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка');
    } finally {
      setSaving(false);
    }
  };

  const openCreateMovement = () => {
    setEditingMovement(null);
    setMovementForm(emptyMovementForm());
    setMovementFormOpen(true);
  };

  const openEditMovement = (row: FinanceMovement) => {
    if (
      row.kind === 'debtToKirma' ||
      row.kind === 'debtFromKirma' ||
      row.kind === 'outgoingTransfer' ||
      row.kind === 'incomingTransfer'
    ) {
      setError('Стары запіс. Выдаліце яго і дадайце «Аплата» або «Выплата».');
      return;
    }
    setEditingMovement(row);
    setMovementForm({
      kind: row.kind,
      amount: String(row.amount),
      description: row.description,
      movementDate: row.movementDate,
    });
    setMovementFormOpen(true);
  };

  const submitMovement = async () => {
    if (activePersonId == null) return;
    const amount = Number(movementForm.amount.replace(',', '.'));
    if (!Number.isFinite(amount) || amount <= 0) {
      setError('Увядзіце карэктную суму.');
      return;
    }
    if (!movementForm.description.trim()) {
      setError('Апісанне абавязковае.');
      return;
    }

    setSaving(true);
    setError(null);
    try {
      if (editingMovement) {
        await updateFinanceMovement(editingMovement.id, {
          kind: movementForm.kind,
          amount,
          description: movementForm.description.trim(),
          movementDate: movementForm.movementDate,
        });
      } else {
        await createFinanceMovement({
          personId: activePersonId,
          kind: movementForm.kind,
          amount,
          description: movementForm.description.trim(),
          movementDate: movementForm.movementDate,
        });
      }
      setMovementFormOpen(false);
      await refresh();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка');
    } finally {
      setSaving(false);
    }
  };

  const handleDeleteMovement = async (id: number) => {
    if (!window.confirm('Выдаліць гэты рух?')) return;
    setSaving(true);
    setError(null);
    try {
      await deleteFinanceMovement(id);
      await refresh();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка');
    } finally {
      setSaving(false);
    }
  };

  const openCreateRecurring = () => {
    setEditingRecurring(null);
    setRecurringForm(emptyRecurringForm());
    setRecurringFormOpen(true);
  };

  const openEditRecurring = (row: FinanceRecurringExpense) => {
    setEditingRecurring(row);
    setRecurringForm({
      kind: row.kind,
      amount: String(row.amount),
      description: row.description,
      dayOfMonth: String(row.dayOfMonth),
      isActive: row.isActive,
    });
    setRecurringFormOpen(true);
  };

  const submitRecurring = async () => {
    if (activePersonId == null) return;
    const amount = Number(recurringForm.amount.replace(',', '.'));
    const day = Number(recurringForm.dayOfMonth);
    if (!Number.isFinite(amount) || amount <= 0) {
      setError('Увядзіце карэктную суму.');
      return;
    }
    if (!recurringForm.description.trim()) {
      setError('Апісанне абавязковае.');
      return;
    }
    if (!Number.isInteger(day) || day < 1 || day > 28) {
      setError('Дзень месяца: ад 1 да 28.');
      return;
    }

    setSaving(true);
    setError(null);
    try {
      if (editingRecurring) {
        await updateFinanceRecurring(editingRecurring.id, {
          kind: recurringForm.kind,
          amount,
          description: recurringForm.description.trim(),
          dayOfMonth: day,
          isActive: recurringForm.isActive,
        });
      } else {
        await createFinanceRecurring({
          personId: activePersonId,
          kind: recurringForm.kind,
          amount,
          description: recurringForm.description.trim(),
          dayOfMonth: day,
        });
      }
      setRecurringFormOpen(false);
      await refresh();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка');
    } finally {
      setSaving(false);
    }
  };

  const handleDeleteRecurring = async (id: number) => {
    if (!window.confirm('Выдаліць рэгулярны расход?')) return;
    setSaving(true);
    setError(null);
    try {
      await deleteFinanceRecurring(id);
      await refresh();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка');
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="flex min-h-[240px] items-center justify-center">
        <LoadingSpinner />
      </div>
    );
  }

  const summary = overview?.summary;

  return (
    <div className="mx-auto w-full max-w-6xl space-y-4">
      {error && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
          {error}
        </div>
      )}

      <div className="rounded-xl border border-gray-200 bg-white p-2 shadow-sm">
        <div className="flex flex-wrap items-center gap-2">
          {persons.map((person) => (
            <button
              key={person.id}
              type="button"
              onClick={() => setActivePersonId(person.id)}
              className={`rounded-lg px-4 py-2 text-sm font-medium transition ${
                activePersonId === person.id
                  ? 'bg-primary text-white shadow-sm'
                  : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
              }`}
            >
              {person.name}
            </button>
          ))}
          <div className="ml-auto flex flex-wrap items-center gap-2">
            <input
              type="text"
              value={newPersonName}
              onChange={(e) => setNewPersonName(e.target.value)}
              placeholder="Новая асоба"
              className="rounded-lg border border-gray-200 px-3 py-2 text-sm"
            />
            <button
              type="button"
              onClick={() => void handleAddPerson()}
              disabled={saving || !newPersonName.trim()}
              className="inline-flex items-center gap-1 rounded-lg bg-primary px-3 py-2 text-sm font-medium text-white disabled:opacity-50"
            >
              <FiPlus className="size-4" />
              Таб
            </button>
          </div>
        </div>
      </div>

      {activePersonId != null && persons.length > 1 && (
        <div className="flex justify-end">
          <button
            type="button"
            onClick={() => void handleDeletePerson(activePersonId)}
            disabled={saving}
            className="text-sm text-red-600 hover:text-red-800"
          >
            Выдаліць бягучую асобу
          </button>
        </div>
      )}

      {overviewLoading && !overview ? (
        <div className="flex min-h-[200px] items-center justify-center">
          <LoadingSpinner />
        </div>
      ) : overview && summary ? (
        <>
          <div className="grid gap-3 sm:grid-cols-2">
            <SummaryCard
              label="Павінна Кірмашу"
              value={formatMoney(summary.personOwesKirma)}
              highlight="amber"
            />
            <SummaryCard
              label="Кірмаш павінен"
              value={formatMoney(summary.kirmaOwesPerson)}
              highlight="primary"
            />
          </div>

          <div className="grid gap-3 sm:grid-cols-2">
            <SummaryCard label="Аплаты асобы" value={formatMoney(summary.totalPayment)} />
            <SummaryCard label="Выплаты Кірмашу" value={formatMoney(summary.totalKirmaPayout)} />
          </div>

          <section className="rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
            <div className="mb-4 flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-lg font-semibold text-gray-900">Гісторыя</h2>
              <button
                type="button"
                onClick={openCreateMovement}
                className="inline-flex size-9 items-center justify-center rounded-lg bg-primary text-white shadow-sm transition hover:bg-primary/90"
                aria-label="Дадаць запіс"
              >
                <FiPlus className="size-5" />
              </button>
            </div>
            <MovementsHistory
              rows={overview.movements}
              onEdit={openEditMovement}
              onDelete={(id) => void handleDeleteMovement(id)}
            />
          </section>

          <section className="rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
            <div className="mb-4 flex flex-wrap items-center justify-between gap-2">
              <div>
                <h2 className="text-lg font-semibold text-gray-900">Рэгулярныя расходы</h2>
                <p className="text-sm text-gray-500">
                  Аўтаматычна дадаюцца ў бягучы месяц (дзень 1–28)
                </p>
              </div>
              <button
                type="button"
                onClick={openCreateRecurring}
                className="inline-flex items-center gap-1 rounded-lg border border-gray-200 px-3 py-2 text-sm font-medium text-gray-800 hover:bg-gray-50"
              >
                <FiPlus className="size-4" />
                Дадаць
              </button>
            </div>
            <RecurringTable
              rows={overview.recurringExpenses}
              onEdit={openEditRecurring}
              onDelete={(id) => void handleDeleteRecurring(id)}
            />
          </section>
        </>
      ) : null}

      {movementFormOpen && (
        <FormModal
          title={editingMovement ? 'Рэдагаваць рух' : 'Новы рух'}
          onClose={() => setMovementFormOpen(false)}
          onSubmit={() => void submitMovement()}
          saving={saving}
        >
          <MovementFields
            form={movementForm}
            onChange={(patch) => setMovementForm((p) => ({ ...p, ...patch }))}
          />
        </FormModal>
      )}

      {recurringFormOpen && (
        <FormModal
          title={editingRecurring ? 'Рэдагаваць рэгулярны расход' : 'Новы рэгулярны расход'}
          onClose={() => setRecurringFormOpen(false)}
          onSubmit={() => void submitRecurring()}
          saving={saving}
        >
          <MovementFields
            form={recurringForm}
            onChange={(patch) => setRecurringForm((p) => ({ ...p, ...patch }))}
            hideDate
          />
          <label className="block text-sm font-medium text-gray-700">
            Дзень месяца (1–28)
            <input
              type="number"
              min={1}
              max={28}
              value={recurringForm.dayOfMonth}
              onChange={(e) => setRecurringForm((p) => ({ ...p, dayOfMonth: e.target.value }))}
              className="mt-1 w-full rounded-lg border border-gray-200 px-3 py-2 text-sm"
            />
          </label>
          {editingRecurring && (
            <label className="flex items-center gap-2 text-sm text-gray-700">
              <input
                type="checkbox"
                checked={recurringForm.isActive}
                onChange={(e) => setRecurringForm((p) => ({ ...p, isActive: e.target.checked }))}
              />
              Актыўны
            </label>
          )}
        </FormModal>
      )}
    </div>
  );
}

function SummaryCard({
  label,
  value,
  highlight,
}: {
  label: string;
  value: string;
  highlight?: 'primary' | 'amber';
}) {
  const boxClass =
    highlight === 'primary'
      ? 'border-primary/20 bg-primary/5'
      : highlight === 'amber'
        ? 'border-amber-200 bg-amber-50'
        : 'border-gray-100 bg-gray-50';
  const valueClass =
    highlight === 'primary'
      ? 'text-primary'
      : highlight === 'amber'
        ? 'text-amber-900'
        : 'text-gray-900';

  return (
    <div className={`rounded-lg border px-4 py-3 ${boxClass}`}>
      <p className="text-xs font-medium uppercase tracking-wide text-gray-500">{label}</p>
      <p className={`mt-1 text-xl font-semibold ${valueClass}`}>{value}</p>
    </div>
  );
}

type YearMonthGroup = {
  year: number;
  months: { key: string; label: string; rows: FinanceMovement[] }[];
};

function groupMovementsByYearMonth(rows: FinanceMovement[]): YearMonthGroup[] {
  const byYear = new Map<number, Map<number, FinanceMovement[]>>();

  for (const row of rows) {
    const [y, m] = row.movementDate.split('-').map(Number);
    if (!y || !m) continue;
    if (!byYear.has(y)) byYear.set(y, new Map());
    const byMonth = byYear.get(y)!;
    if (!byMonth.has(m)) byMonth.set(m, []);
    byMonth.get(m)!.push(row);
  }

  const monthLabel = (year: number, month: number) =>
    new Intl.DateTimeFormat('be-BY', { month: 'long' }).format(new Date(year, month - 1, 1));

  return [...byYear.entries()]
    .sort(([a], [b]) => b - a)
    .map(([year, monthsMap]) => ({
      year,
      months: [...monthsMap.entries()]
        .sort(([a], [b]) => b - a)
        .map(([month, monthRows]) => ({
          key: `${year}-${month}`,
          label: monthLabel(year, month),
          rows: monthRows.sort((a, b) => b.movementDate.localeCompare(a.movementDate) || b.id - a.id),
        })),
    }));
}

function formatMovementDay(isoDate: string): string {
  const [, , day] = isoDate.split('-');
  return day ? String(Number(day)) : isoDate;
}

/** Выплаты − аплаты за период (как на карточках долга). */
function periodNetBalance(rows: FinanceMovement[]): number {
  let payouts = 0;
  let payments = 0;

  for (const row of rows) {
    switch (row.kind) {
      case 'kirmaPayout':
      case 'incomingTransfer':
        payouts += row.amount;
        break;
      case 'payment':
      case 'outgoingTransfer':
        payments += row.amount;
        break;
      default:
        break;
    }
  }

  return payouts - payments;
}

function PeriodNetLabel({ rows }: { rows: FinanceMovement[] }) {
  const net = periodNetBalance(rows);
  const tone =
    net > 0 ? 'text-primary' : net < 0 ? 'text-amber-800' : 'text-gray-500';

  return (
    <span className={`ml-auto text-sm font-semibold tabular-nums ${tone}`}>
      {formatMoney(net)}
    </span>
  );
}

function yearCollapseKey(year: number) {
  return `y:${year}`;
}

function monthCollapseKey(monthKey: string) {
  return `m:${monthKey}`;
}

function MovementsHistory({
  rows,
  onEdit,
  onDelete,
}: {
  rows: FinanceMovement[];
  onEdit: (row: FinanceMovement) => void;
  onDelete: (id: number) => void;
}) {
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set());

  const grouped = useMemo(() => groupMovementsByYearMonth(rows), [rows]);

  const monthKeysSignature = useMemo(
    () => grouped.flatMap((g) => g.months.map((m) => m.key)).join(','),
    [grouped],
  );

  useEffect(() => {
    setCollapsed((prev) => {
      const yearKeys = [...prev].filter((k) => k.startsWith('y:'));
      const monthKeys = grouped.flatMap(({ months }) =>
        months.map(({ key }) => monthCollapseKey(key)),
      );
      return new Set([...yearKeys, ...monthKeys]);
    });
  }, [monthKeysSignature, grouped]);

  const toggle = (key: string) => {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const isCollapsed = (key: string) => collapsed.has(key);

  if (rows.length === 0) {
    return <p className="text-sm text-gray-500">Пакуль няма запісаў.</p>;
  }

  return (
    <div className="space-y-5">
      {grouped.map(({ year, months }) => {
        const yearKey = yearCollapseKey(year);
        const yearClosed = isCollapsed(yearKey);

        return (
        <div key={year} className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <button
            type="button"
            onClick={() => toggle(yearKey)}
            className="flex w-full items-center gap-3 border-b border-gray-200 bg-gray-100 px-4 py-3 text-left transition hover:bg-gray-200/60"
            aria-expanded={!yearClosed}
          >
            {yearClosed ? (
              <FiChevronRight className="size-4 shrink-0 text-gray-600" aria-hidden />
            ) : (
              <FiChevronDown className="size-4 shrink-0 text-gray-600" aria-hidden />
            )}
            <span className="rounded-md bg-gray-800 px-2.5 py-0.5 text-sm font-bold tracking-wide text-white">
              {year}
            </span>
            <PeriodNetLabel rows={months.flatMap((m) => m.rows)} />
          </button>

          {!yearClosed && (
          <div className="space-y-3 bg-gray-50/80 p-3">
            {months.map(({ key, label, rows: monthRows }) => {
              const monthKey = monthCollapseKey(key);
              const monthClosed = isCollapsed(monthKey);

              return (
              <div
                key={key}
                className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm"
              >
                <button
                  type="button"
                  onClick={() => toggle(monthKey)}
                  className="flex w-full items-center gap-2 border-b border-primary/15 bg-primary/10 px-3 py-2.5 text-left transition hover:bg-primary/15"
                  aria-expanded={!monthClosed}
                >
                  {monthClosed ? (
                    <FiChevronRight className="size-3.5 shrink-0 text-primary/70" aria-hidden />
                  ) : (
                    <FiChevronDown className="size-3.5 shrink-0 text-primary/70" aria-hidden />
                  )}
                  <span className="text-sm font-semibold capitalize text-primary">{label}</span>
                  <PeriodNetLabel rows={monthRows} />
                </button>

                {!monthClosed && (
                <div className="overflow-x-auto">
                  <table className="min-w-full text-left text-sm">
                    <thead>
                      <tr className="border-b border-gray-200 bg-white text-xs uppercase text-gray-500">
                        <th className="px-3 py-2">Дзень</th>
                        <th className="px-3 py-2">Тып</th>
                        <th className="px-3 py-2">Апісанне</th>
                        <th className="px-3 py-2 text-right">Сума</th>
                        <th className="px-3 py-2 w-24" />
                      </tr>
                    </thead>
                    <tbody>
                      {monthRows.map((row) => (
                        <tr key={row.id} className="border-b border-gray-100 bg-white last:border-b-0 hover:bg-gray-50">
                          <td className="px-3 py-2 whitespace-nowrap text-gray-600">
                            {formatMovementDay(row.movementDate)}
                          </td>
                          <td className="px-3 py-2">{movementKindLabel(row.kind)}</td>
                          <td className="px-3 py-2">
                            {row.description}
                            {row.isFromRecurring && (
                              <span className="ml-2 rounded bg-gray-100 px-1.5 py-0.5 text-[10px] text-gray-600">
                                рэгулярны
                              </span>
                            )}
                          </td>
                          <td className="px-3 py-2 text-right font-medium">{formatMoney(row.amount)}</td>
                          <td className="px-3 py-2">
                            <div className="flex justify-end gap-1">
                              <button
                                type="button"
                                onClick={() => onEdit(row)}
                                className="rounded p-1.5 text-gray-600 hover:bg-gray-100"
                                aria-label="Рэдагаваць"
                              >
                                <FiEdit2 className="size-4" />
                              </button>
                              <button
                                type="button"
                                onClick={() => onDelete(row.id)}
                                className="rounded p-1.5 text-red-600 hover:bg-red-50"
                                aria-label="Выдаліць"
                              >
                                <FiTrash2 className="size-4" />
                              </button>
                            </div>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                )}
              </div>
            );
            })}
          </div>
          )}
        </div>
        );
      })}
    </div>
  );
}

function RecurringTable({
  rows,
  onEdit,
  onDelete,
}: {
  rows: FinanceRecurringExpense[];
  onEdit: (row: FinanceRecurringExpense) => void;
  onDelete: (id: number) => void;
}) {
  if (rows.length === 0) {
    return <p className="text-sm text-gray-500">Няма рэгулярных расходаў.</p>;
  }

  return (
    <div className="overflow-x-auto">
      <table className="min-w-full text-left text-sm">
        <thead>
          <tr className="border-b border-gray-100 text-xs uppercase text-gray-500">
            <th className="px-2 py-2">Дзень</th>
            <th className="px-2 py-2">Тып</th>
            <th className="px-2 py-2">Апісанне</th>
            <th className="px-2 py-2 text-right">Сума</th>
            <th className="px-2 py-2">Статус</th>
            <th className="px-2 py-2 w-24" />
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id} className="border-b border-gray-50 hover:bg-gray-50/80">
              <td className="px-2 py-2">{row.dayOfMonth}</td>
              <td className="px-2 py-2">{movementKindLabel(row.kind)}</td>
              <td className="px-2 py-2">{row.description}</td>
              <td className="px-2 py-2 text-right font-medium">{formatMoney(row.amount)}</td>
              <td className="px-2 py-2">{row.isActive ? 'Актыўны' : 'Выключаны'}</td>
              <td className="px-2 py-2">
                <div className="flex justify-end gap-1">
                  <button
                    type="button"
                    onClick={() => onEdit(row)}
                    className="rounded p-1.5 text-gray-600 hover:bg-gray-100"
                    aria-label="Рэдагаваць"
                  >
                    <FiEdit2 className="size-4" />
                  </button>
                  <button
                    type="button"
                    onClick={() => onDelete(row.id)}
                    className="rounded p-1.5 text-red-600 hover:bg-red-50"
                    aria-label="Выдаліць"
                  >
                    <FiTrash2 className="size-4" />
                  </button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function MovementFields({
  form,
  onChange,
  hideDate,
}: {
  form: MovementFieldsState & { movementDate?: string };
  onChange: (patch: Partial<MovementFieldsState & { movementDate?: string }>) => void;
  hideDate?: boolean;
}) {
  return (
    <div className="space-y-3">
      <label className="block text-sm font-medium text-gray-700">
        Тып
        <select
          value={form.kind}
          onChange={(e) => onChange({ kind: e.target.value as FinanceMovementKind })}
          className="mt-1 w-full rounded-lg border border-gray-200 px-3 py-2 text-sm"
        >
          {MOVEMENT_KIND_OPTIONS.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          ))}
        </select>
      </label>
      <label className="block text-sm font-medium text-gray-700">
        Сума (PLN)
        <input
          type="text"
          inputMode="decimal"
          value={form.amount}
          onChange={(e) => onChange({ amount: e.target.value })}
          className="mt-1 w-full rounded-lg border border-gray-200 px-3 py-2 text-sm"
        />
      </label>
      <label className="block text-sm font-medium text-gray-700">
        Апісанне
        <input
          type="text"
          value={form.description}
          onChange={(e) => onChange({ description: e.target.value })}
          className="mt-1 w-full rounded-lg border border-gray-200 px-3 py-2 text-sm"
        />
      </label>
      {!hideDate && form.movementDate != null && (
        <label className="block text-sm font-medium text-gray-700">
          Дата
          <input
            type="date"
            value={form.movementDate}
            onChange={(e) => onChange({ movementDate: e.target.value })}
            className="mt-1 w-full rounded-lg border border-gray-200 px-3 py-2 text-sm"
          />
        </label>
      )}
    </div>
  );
}

function FormModal({
  title,
  children,
  onClose,
  onSubmit,
  saving,
}: {
  title: string;
  children: ReactNode;
  onClose: () => void;
  onSubmit: () => void;
  saving: boolean;
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-md rounded-xl bg-white p-5 shadow-xl">
        <div className="mb-4 flex items-center justify-between">
          <h3 className="text-lg font-semibold text-gray-900">{title}</h3>
          <button type="button" onClick={onClose} className="rounded p-1 text-gray-500 hover:bg-gray-100">
            <FiX className="size-5" />
          </button>
        </div>
        <div className="space-y-3">{children}</div>
        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg border border-gray-200 px-4 py-2 text-sm font-medium text-gray-700"
          >
            Скасаваць
          </button>
          <button
            type="button"
            onClick={onSubmit}
            disabled={saving}
            className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
          >
            {saving ? 'Захоўваем…' : 'Захаваць'}
          </button>
        </div>
      </div>
    </div>
  );
}
