import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiChevronDown, FiChevronRight, FiTrash2 } from 'react-icons/fi';
import type { SupplyListItem } from '@/types/supply';

type Props = {
  supplies: SupplyListItem[];
  filterActive?: boolean;
  sortDirection: 'asc' | 'desc';
  onToggleDateSort: () => void;
  supplierFilters: string[];
  selectedSuppliers: string[];
  onToggleSupplierFilter: (name: string) => void;
  onOpenSupply: (supply: SupplyListItem) => void;
  onRequestDelete?: (supply: SupplyListItem) => void;
};

type YearMonthGroup = {
  year: number;
  months: { key: string; label: string; rows: SupplyListItem[] }[];
};

const MONTH_NAMES = [
  'Студзень',
  'Люты',
  'Сакавік',
  'Красавік',
  'Май',
  'Чэрвень',
  'Ліпень',
  'Жнівень',
  'Верасень',
  'Кастрычнік',
  'Лістапад',
  'Снежань',
] as const;

function parseYearMonth(date: string): { year: number; month: number } | null {
  const match = /^(\d{4})-(\d{2})/.exec(date);
  if (match) {
    const year = Number(match[1]);
    const month = Number(match[2]);
    if (year >= 1 && month >= 1 && month <= 12) return { year, month };
  }
  const parsed = new Date(date);
  if (Number.isNaN(parsed.getTime())) return null;
  return { year: parsed.getFullYear(), month: parsed.getMonth() + 1 };
}

function groupSuppliesByYearMonth(supplies: SupplyListItem[]): YearMonthGroup[] {
  const byYear = new Map<number, Map<number, SupplyListItem[]>>();

  for (const row of supplies) {
    const parts = parseYearMonth(row.date);
    if (!parts) continue;
    if (!byYear.has(parts.year)) byYear.set(parts.year, new Map());
    const byMonth = byYear.get(parts.year)!;
    if (!byMonth.has(parts.month)) byMonth.set(parts.month, []);
    byMonth.get(parts.month)!.push(row);
  }

  return [...byYear.entries()]
    .sort(([a], [b]) => b - a)
    .map(([year, monthsMap]) => ({
      year,
      months: [...monthsMap.entries()]
        .sort(([a], [b]) => b - a)
        .map(([month, monthRows]) => ({
          key: `${year}-${month}`,
          label: MONTH_NAMES[month - 1] ?? `Месяц ${month}`,
          rows: monthRows,
        })),
    }));
}

function sortRows(rows: SupplyListItem[], direction: 'asc' | 'desc'): SupplyListItem[] {
  return [...rows].sort((a, b) => {
    const aTime = new Date(a.date).getTime();
    const bTime = new Date(b.date).getTime();
    const aValid = Number.isFinite(aTime);
    const bValid = Number.isFinite(bTime);
    if (!aValid && !bValid) return a.supplierName.localeCompare(b.supplierName, 'be');
    if (!aValid) return 1;
    if (!bValid) return -1;
    const byDate = direction === 'asc' ? aTime - bTime : bTime - aTime;
    if (byDate !== 0) return byDate;
    return a.supplierName.localeCompare(b.supplierName, 'be');
  });
}

function formatSupplyDay(date: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(date);
  if (match) return String(Number(match[3]));
  const parsed = new Date(date);
  if (Number.isNaN(parsed.getTime())) return date || '—';
  return String(parsed.getDate());
}

function yearCollapseKey(year: number): string {
  return `y:${year}`;
}

function monthCollapseKey(monthKey: string): string {
  return `m:${monthKey}`;
}

function monthTotalQuantity(rows: SupplyListItem[]): number {
  return rows.reduce((sum, row) => sum + row.totalQuantity, 0);
}

export default function SuppliesTable({
  supplies,
  filterActive = false,
  sortDirection,
  onToggleDateSort,
  supplierFilters,
  selectedSuppliers,
  onToggleSupplierFilter,
  onOpenSupply,
  onRequestDelete,
}: Props) {
  const [supplierMenuOpen, setSupplierMenuOpen] = useState(false);
  const [menuMounted, setMenuMounted] = useState(false);
  const [menuPosition, setMenuPosition] = useState({ top: 0, left: 0 });
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set());
  const collapseInitialized = useRef(false);
  const supplierTriggerRef = useRef<HTMLButtonElement | null>(null);
  const supplierMenuRef = useRef<HTMLDivElement | null>(null);

  const currentCalendarYear = new Date().getFullYear();

  const grouped = useMemo(() => groupSuppliesByYearMonth(supplies), [supplies]);

  const monthKeysSignature = useMemo(
    () => grouped.flatMap((g) => g.months.map((m) => m.key)).join(','),
    [grouped]
  );

  useEffect(() => {
    if (grouped.length === 0 || collapseInitialized.current) return;
    collapseInitialized.current = true;
    const years = grouped.map((g) => g.year);
    const yearKeys = years
      .filter((year) => year !== currentCalendarYear)
      .map((year) => yearCollapseKey(year));
    const monthKeys = grouped.flatMap(({ months }) =>
      months.map(({ key }) => monthCollapseKey(key))
    );
    setCollapsed(new Set([...yearKeys, ...monthKeys]));
  }, [grouped, currentCalendarYear]);

  useEffect(() => {
    setCollapsed((prev) => {
      const yearKeys = [...prev].filter((k) => k.startsWith('y:'));
      const monthKeys = grouped.flatMap(({ months }) =>
        months.map(({ key }) => monthCollapseKey(key))
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

  const updateMenuPosition = () => {
    if (!supplierTriggerRef.current) return;
    const rect = supplierTriggerRef.current.getBoundingClientRect();
    setMenuPosition({
      top: rect.bottom + 8,
      left: rect.left,
    });
  };

  useEffect(() => {
    setMenuMounted(true);
  }, []);

  useEffect(() => {
    if (!supplierMenuOpen) return;
    updateMenuPosition();

    const onDocClick = (event: MouseEvent) => {
      const target = event.target as Node;
      const clickedMenu = supplierMenuRef.current?.contains(target);
      const clickedTrigger = supplierTriggerRef.current?.contains(target);
      if (!clickedMenu && !clickedTrigger) {
        setSupplierMenuOpen(false);
      }
    };
    const onViewportChange = () => updateMenuPosition();

    document.addEventListener('mousedown', onDocClick);
    window.addEventListener('resize', onViewportChange);
    window.addEventListener('scroll', onViewportChange, true);
    return () => {
      document.removeEventListener('mousedown', onDocClick);
      window.removeEventListener('resize', onViewportChange);
      window.removeEventListener('scroll', onViewportChange, true);
    };
  }, [supplierMenuOpen]);

  if (supplies.length === 0) {
    return (
      <div className="px-6 py-16 text-center">
        <p className="text-sm font-medium text-gray-900">
          {filterActive ? 'Няма паставак па выбраным фільтры' : 'Паставак пакуль няма'}
        </p>
        <p className="mt-1 text-sm text-gray-500">
          {filterActive
            ? 'Змяніце фільтр пастаўшчыкаў.'
            : 'Дадайце пастаўку ў сістэме, каб яна з’явілася ў спісе.'}
        </p>
      </div>
    );
  }

  return (
    <div className="p-4">
      <div className="mb-4 flex flex-wrap items-center gap-3 rounded-lg border border-gray-200 bg-gray-50 px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">
        <div className="inline-flex items-center gap-1">
          <span>Пастаўшчык</span>
          <button
            type="button"
            ref={supplierTriggerRef}
            className="inline-flex items-center rounded p-0.5 text-gray-500 transition hover:bg-gray-100 hover:text-gray-700"
            aria-label="Фільтр пастаўшчыкоў"
            onClick={() => setSupplierMenuOpen((prev) => !prev)}
          >
            <span aria-hidden>{supplierMenuOpen ? '▴' : '▾'}</span>
          </button>
        </div>
        <button
          type="button"
          onClick={onToggleDateSort}
          className="inline-flex items-center gap-1 rounded text-xs font-semibold uppercase tracking-wide text-gray-500 transition hover:text-gray-700"
          aria-label="Сартаваць па даце"
        >
          Дата ў месяцы
          <span aria-hidden>{sortDirection === 'asc' ? '↑' : '↓'}</span>
        </button>
      </div>

      <div className="space-y-5">
        {grouped.map(({ year, months }) => {
          const yearKey = yearCollapseKey(year);
          const yearClosed = isCollapsed(yearKey);

          return (
            <div
              key={year}
              className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm"
            >
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
                <span className="ml-auto text-xs text-gray-500">
                  {months.reduce((sum, m) => sum + m.rows.length, 0)} паставак
                </span>
              </button>

              {!yearClosed && (
                <div className="space-y-3 bg-gray-50/80 p-3">
                  {months.map(({ key, label, rows: monthRows }) => {
                    const monthKey = monthCollapseKey(key);
                    const monthClosed = isCollapsed(monthKey);
                    const sortedRows = sortRows(monthRows, sortDirection);
                    const totalQty = monthTotalQuantity(sortedRows);

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
                          <span className="ml-auto text-xs font-medium tabular-nums text-primary/80">
                            {sortedRows.length} · {totalQty} шт.
                          </span>
                        </button>

                        {!monthClosed && (
                          <div className="overflow-x-auto">
                            <table className="min-w-full border-collapse text-left text-sm">
                              <thead>
                                <tr className="border-b border-gray-200 bg-white text-xs font-semibold uppercase tracking-wide text-gray-500">
                                  <th className="px-4 py-2.5">Пастаўшчык</th>
                                  <th className="px-4 py-2.5">Дзень</th>
                                  <th className="px-4 py-2.5 text-right tabular-nums">Тавары</th>
                                  {onRequestDelete && <th className="w-14 px-2 py-2.5" />}
                                </tr>
                              </thead>
                              <tbody className="divide-y divide-gray-100">
                                {sortedRows.map((row) => (
                                  <tr
                                    key={row.id}
                                    className="cursor-pointer transition-colors hover:bg-primary/10"
                                    onClick={() => onOpenSupply(row)}
                                  >
                                    <td className="whitespace-nowrap px-4 py-3 font-medium text-gray-900">
                                      {row.supplierName}
                                    </td>
                                    <td className="whitespace-nowrap px-4 py-3 tabular-nums text-gray-600">
                                      {formatSupplyDay(row.date)}
                                    </td>
                                    <td className="px-4 py-3 text-right tabular-nums text-gray-700">
                                      {row.totalQuantity}
                                    </td>
                                    {onRequestDelete && (
                                      <td className="px-2 py-3 text-right">
                                        <button
                                          type="button"
                                          onClick={(e) => {
                                            e.stopPropagation();
                                            onRequestDelete(row);
                                          }}
                                          className="inline-flex size-9 items-center justify-center rounded-lg text-gray-500 transition hover:bg-red-50 hover:text-red-700"
                                          aria-label={`Выдаліць пастаўку ад ${row.supplierName}`}
                                          title="Выдаліць пастаўку"
                                        >
                                          <FiTrash2 className="size-4" />
                                        </button>
                                      </td>
                                    )}
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

      {menuMounted &&
        supplierMenuOpen &&
        createPortal(
          <div
            ref={supplierMenuRef}
            className="fixed z-[70] w-64 rounded-lg border border-gray-200 bg-white p-3 shadow-lg"
            style={{ top: `${menuPosition.top}px`, left: `${menuPosition.left}px` }}
          >
            <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-500">
              Фільтр пастаўшчыкоў
            </p>
            <div className="max-h-56 space-y-2 overflow-auto pr-1">
              {supplierFilters.length === 0 ? (
                <p className="text-xs text-gray-500">Няма пастаўшчыкоў</p>
              ) : (
                supplierFilters.map((name) => (
                  <label
                    key={name}
                    className="flex items-center gap-2 text-sm font-normal normal-case text-gray-700"
                  >
                    <input
                      type="checkbox"
                      className="size-4 rounded border-gray-300 accent-primary focus:ring-primary"
                      checked={selectedSuppliers.includes(name)}
                      onChange={() => onToggleSupplierFilter(name)}
                    />
                    <span className="truncate" title={name}>
                      {name}
                    </span>
                  </label>
                ))
              )}
            </div>
          </div>,
          document.body
        )}
    </div>
  );
}
