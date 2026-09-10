'use client';

import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { usePortalMenu } from '@/hooks/usePortalMenu';
import type { BukinistkaPortalSale } from '@/lib/api/bukinistka-sales';
import { useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiSearch } from 'react-icons/fi';

const EMPTY_SUPPLIER = '__empty__';

function formatPrice(value: number): string {
  if (!Number.isFinite(value)) return '—';
  return value.toLocaleString('be-BY', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
}

function formatDate(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('be-BY', {
    timeZone: 'Europe/Warsaw',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function supplierKey(name: string | null | undefined): string {
  const trimmed = name?.trim() ?? '';
  return trimmed || EMPTY_SUPPLIER;
}

function supplierLabel(key: string): string {
  return key === EMPTY_SUPPLIER ? '—' : key;
}

type BukinistkaSalesListProps = {
  title: string;
  description: string;
  orderColumnLabel: string;
  sales: BukinistkaPortalSale[];
  loading: boolean;
  error: string | null;
  syncing: boolean;
  onSync: () => void;
};

export default function BukinistkaSalesList({
  title,
  description,
  orderColumnLabel,
  sales,
  loading,
  error,
  syncing,
  onSync,
}: BukinistkaSalesListProps) {
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedSuppliers, setSelectedSuppliers] = useState<string[]>([]);
  const supplierMenu = usePortalMenu({
    menuWidth: 260,
    estimatedMenuHeight: 320,
  });

  const supplierOptions = useMemo(() => {
    const keys = new Set<string>();
    for (const row of sales) {
      keys.add(supplierKey(row.supplierName));
    }
    return Array.from(keys).sort((a, b) =>
      supplierLabel(a).localeCompare(supplierLabel(b), 'be')
    );
  }, [sales]);

  const filteredSales = useMemo(() => {
    const search = searchQuery.trim().toLowerCase();
    return sales.filter((row) => {
      if (
        selectedSuppliers.length > 0 &&
        !selectedSuppliers.includes(supplierKey(row.supplierName))
      ) {
        return false;
      }
      if (!search) return true;
      return row.productName.toLowerCase().includes(search);
    });
  }, [sales, searchQuery, selectedSuppliers]);

  const totals = useMemo(() => {
    let units = 0;
    let gross = 0;
    for (const row of filteredSales) {
      units += row.quantity;
      if (row.grossUnitCost != null && Number.isFinite(row.grossUnitCost)) {
        gross += row.grossUnitCost * row.quantity;
      }
    }
    return { units, gross, rows: filteredSales.length };
  }, [filteredSales]);

  const supplierFilterActive = selectedSuppliers.length > 0;

  const toggleSupplierFilter = (supplier: string) => {
    setSelectedSuppliers((prev) =>
      prev.includes(supplier)
        ? prev.filter((s) => s !== supplier)
        : [...prev, supplier]
    );
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-base font-semibold text-gray-900">{title}</h2>
          <p className="mt-1 text-sm text-gray-500">{description}</p>
        </div>
        <button
          type="button"
          disabled={syncing || loading}
          onClick={onSync}
          className="inline-flex items-center gap-2 rounded-lg bg-primary px-3 py-2 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-50"
        >
          {syncing ? (
            <span className="size-3.5 animate-spin rounded-full border-2 border-white/30 border-t-white" />
          ) : null}
          Абнавіць
        </button>
      </div>

      {error ? (
        <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
          {error}
        </div>
      ) : null}

      {loading ? (
        <LoadingSpinner label="Загрузка продажаў…" />
      ) : sales.length === 0 ? (
        <p className="rounded-lg border border-dashed border-gray-200 bg-gray-50 px-4 py-10 text-center text-sm text-gray-500">
          Пакуль няма сінхранізаваных продажаў.
        </p>
      ) : (
        <div className="space-y-3">
          <div className="flex flex-wrap items-center gap-3">
            <div className="relative min-w-[220px] flex-1">
              <FiSearch
                className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-gray-400"
                aria-hidden
              />
              <input
                type="search"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder="Пошук па назве…"
                className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-3 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2"
              />
            </div>
            <p className="text-sm text-gray-700">
              Радкоў: {totals.rows} · Тавараў: {totals.units} · Брута:{' '}
              <span className="font-semibold tabular-nums text-gray-900">
                {formatPrice(totals.gross)}
              </span>
            </p>
          </div>

          <div className="overflow-hidden rounded-xl border border-gray-200">
            <table className="w-full table-fixed divide-y divide-gray-100 text-left text-sm">
              <thead className="bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                <tr>
                  <th className="w-[28%] px-4 py-3">Прадукт</th>
                  <th className="w-[10%] px-4 py-3 text-right">Колькасць</th>
                  <th className="w-[12%] px-4 py-3 text-right">Брута</th>
                  <th className="w-[16%] px-4 py-3">
                    <button
                      type="button"
                      ref={supplierMenu.triggerRef}
                      onClick={supplierMenu.toggle}
                      className={`inline-flex items-center gap-1 uppercase tracking-wide transition ${
                        supplierFilterActive || supplierMenu.open
                          ? 'text-primary'
                          : 'text-gray-500 hover:text-gray-800'
                      }`}
                      aria-expanded={supplierMenu.open}
                      aria-haspopup="listbox"
                    >
                      <span>Пастаўшчык</span>
                      <span aria-hidden>{supplierMenu.open ? '▴' : '▾'}</span>
                    </button>
                  </th>
                  <th className="w-[16%] px-4 py-3">{orderColumnLabel}</th>
                  <th className="w-[18%] px-4 py-3">Дата продажу</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100 bg-white">
                {filteredSales.length === 0 ? (
                  <tr>
                    <td
                      colSpan={6}
                      className="px-4 py-8 text-center text-sm text-gray-500"
                    >
                      Нічога не знойдзена.
                    </td>
                  </tr>
                ) : (
                  filteredSales.map((row) => (
                    <tr key={row.id} className="align-top">
                      <td className="px-4 py-3 font-medium text-gray-900">
                        <span className="break-words [overflow-wrap:anywhere]">
                          {row.productName}
                        </span>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums text-gray-700">
                        {row.quantity}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums text-gray-700">
                        {row.grossUnitCost != null
                          ? formatPrice(row.grossUnitCost)
                          : '—'}
                      </td>
                      <td className="px-4 py-3 text-gray-600">
                        <span className="break-words [overflow-wrap:anywhere]">
                          {row.supplierName?.trim() || '—'}
                        </span>
                      </td>
                      <td className="px-4 py-3 text-gray-600">
                        {row.orderLabel}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-gray-500">
                        {formatDate(row.soldAtUtc)}
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {supplierMenu.mounted &&
        supplierMenu.open &&
        createPortal(
          <div
            ref={supplierMenu.menuRef}
            className="fixed z-[70] rounded-lg border border-gray-200 bg-white p-3 shadow-lg"
            style={{
              top: `${supplierMenu.position.top}px`,
              left: `${supplierMenu.position.left}px`,
              width: `${supplierMenu.menuWidth}px`,
            }}
            role="listbox"
            aria-label="Фільтр пастаўшчыкоў"
          >
            <div className="mb-2 flex items-center justify-between gap-2">
              <p className="text-xs font-semibold uppercase tracking-wide text-gray-500">
                Пастаўшчыкі
              </p>
              {supplierFilterActive ? (
                <button
                  type="button"
                  onClick={() => setSelectedSuppliers([])}
                  className="text-xs font-medium text-primary hover:underline"
                >
                  Скінуць
                </button>
              ) : null}
            </div>
            <div className="max-h-64 space-y-2 overflow-auto pr-1">
              {supplierOptions.length === 0 ? (
                <p className="text-xs text-gray-500">Няма пастаўшчыкоў</p>
              ) : (
                supplierOptions.map((supplier) => (
                  <label
                    key={supplier}
                    className="flex cursor-pointer items-center gap-2 text-sm font-normal normal-case text-gray-700"
                  >
                    <input
                      type="checkbox"
                      checked={selectedSuppliers.includes(supplier)}
                      onChange={() => toggleSupplierFilter(supplier)}
                      className="rounded border-gray-300 text-primary focus:ring-primary"
                    />
                    <span>{supplierLabel(supplier)}</span>
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
