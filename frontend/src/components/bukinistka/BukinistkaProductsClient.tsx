'use client';

import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { usePortalMenu } from '@/hooks/usePortalMenu';
import {
  fetchBukinistkaProducts,
  type BukinistkaProduct,
} from '@/lib/api/bukinistka-products';
import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiChevronDown, FiChevronUp, FiSearch, FiX } from 'react-icons/fi';

type SortKey = 'standardPrice' | 'listPrice' | 'quantityInStock';
type SortDir = 'asc' | 'desc';

const EMPTY_SUPPLIER = '__none__';

function formatQty(value: number): string {
  if (!Number.isFinite(value)) return '0';
  return Number.isInteger(value)
    ? String(value)
    : value.toLocaleString('be-BY', {
        minimumFractionDigits: 0,
        maximumFractionDigits: 2,
      });
}

function formatPrice(value: number): string {
  if (!Number.isFinite(value)) return '—';
  return value.toLocaleString('be-BY', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
}

function supplierKey(name: string | null | undefined): string {
  const trimmed = name?.trim() ?? '';
  return trimmed || EMPTY_SUPPLIER;
}

function supplierLabel(key: string): string {
  return key === EMPTY_SUPPLIER ? '—' : key;
}

function SortableHeader({
  label,
  column,
  sortKey,
  sortDir,
  onSort,
}: {
  label: string;
  column: SortKey;
  sortKey: SortKey;
  sortDir: SortDir;
  onSort: (column: SortKey) => void;
}) {
  const active = sortKey === column;
  return (
    <th className="px-4 py-3 text-right">
      <button
        type="button"
        onClick={() => onSort(column)}
        className={`inline-flex items-center gap-1 uppercase tracking-wide transition ${
          active ? 'text-amber-800' : 'text-gray-500 hover:text-gray-800'
        }`}
        aria-label={`Сартаваць па «${label}»`}
      >
        <span>{label}</span>
        {active ? (
          sortDir === 'asc' ? (
            <FiChevronUp className="size-3.5 shrink-0" aria-hidden />
          ) : (
            <FiChevronDown className="size-3.5 shrink-0" aria-hidden />
          )
        ) : (
          <FiChevronDown className="size-3.5 shrink-0 opacity-30" aria-hidden />
        )}
      </button>
    </th>
  );
}

export default function BukinistkaProductsClient() {
  const [rows, setRows] = useState<BukinistkaProduct[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [sortKey, setSortKey] = useState<SortKey>('quantityInStock');
  const [sortDir, setSortDir] = useState<SortDir>('desc');
  const [selectedSuppliers, setSelectedSuppliers] = useState<string[]>([]);
  const supplierMenu = usePortalMenu({
    menuWidth: 280,
    estimatedMenuHeight: 320,
  });

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchBukinistkaProducts()
      .then((products) => {
        if (!cancelled) setRows(products);
      })
      .catch((err: Error) => {
        if (!cancelled) setError(err.message);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const supplierOptions = useMemo(() => {
    const set = new Set<string>();
    for (const row of rows) {
      set.add(supplierKey(row.supplierName));
    }
    return [...set].sort((a, b) => {
      if (a === EMPTY_SUPPLIER) return 1;
      if (b === EMPTY_SUPPLIER) return -1;
      return a.localeCompare(b, 'be', { sensitivity: 'base' });
    });
  }, [rows]);

  const visibleRows = useMemo(() => {
    const search = searchQuery.trim().toLowerCase();
    let list = rows;

    if (selectedSuppliers.length > 0) {
      list = list.filter((row) =>
        selectedSuppliers.includes(supplierKey(row.supplierName))
      );
    }

    if (search) {
      list = list.filter((row) => {
        const haystack = [
          row.name,
          row.defaultCode ?? '',
          row.barcode ?? '',
          row.supplierName ?? '',
        ]
          .join(' ')
          .toLowerCase();
        return haystack.includes(search);
      });
    }

    return [...list].sort((a, b) => {
      const left = a[sortKey];
      const right = b[sortKey];
      const diff = sortDir === 'desc' ? right - left : left - right;
      if (diff !== 0) return diff;
      return a.name.localeCompare(b.name, 'be', { sensitivity: 'base' });
    });
  }, [rows, searchQuery, sortKey, sortDir, selectedSuppliers]);

  const handleSort = (column: SortKey) => {
    if (sortKey === column) {
      setSortDir((prev) => (prev === 'desc' ? 'asc' : 'desc'));
      return;
    }
    setSortKey(column);
    setSortDir('desc');
  };

  const toggleSupplierFilter = (supplier: string) => {
    setSelectedSuppliers((prev) =>
      prev.includes(supplier)
        ? prev.filter((item) => item !== supplier)
        : [...prev, supplier]
    );
  };

  const openInOdoo = (row: BukinistkaProduct) => {
    if (!row.odooUrl) return;
    window.open(row.odooUrl, '_blank', 'noopener,noreferrer');
  };

  const supplierFilterActive = selectedSuppliers.length > 0;

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col gap-4">
      <div>
        <h1 className="text-2xl font-semibold text-gray-900">Прадукты</h1>
        <p className="mt-1 text-sm text-gray-600">
          Каталог Odoo Bukinistka з колькасцю ў наяўнасці. Клік па радку
          адкрывае картку ў Odoo.
        </p>
      </div>

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
        <label className="relative block w-full max-w-md">
          <span className="sr-only">
            Пошук па назве, штрыхкодзе і пастаўшчыку
          </span>
          <FiSearch
            className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-gray-400"
            aria-hidden
          />
          <input
            type="text"
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            placeholder="Пошук па назве, штрыхкодзе, пастаўшчыку..."
            className="h-10 w-full rounded-xl border border-gray-200 bg-white py-2 pl-10 pr-10 text-sm text-gray-900 shadow-sm outline-none transition placeholder:text-gray-400 focus:border-amber-400 focus:ring-2 focus:ring-amber-100"
          />
          {searchQuery && (
            <button
              type="button"
              onClick={() => setSearchQuery('')}
              className="absolute right-2 top-1/2 -translate-y-1/2 rounded p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-700"
              aria-label="Ачысціць пошук"
            >
              <FiX className="size-4" aria-hidden />
            </button>
          )}
        </label>
      </div>

      {error && (
        <p className="rounded-xl border border-red-100 bg-red-50 px-4 py-3 text-sm text-red-800">
          {error}
        </p>
      )}

      {loading ? (
        <div className="flex justify-center rounded-2xl border border-gray-200 bg-white py-16 shadow-sm">
          <LoadingSpinner label="Загрузка прадуктаў..." />
        </div>
      ) : (
        <div className="overflow-hidden rounded-2xl border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-100 px-4 py-3 text-sm text-gray-500">
            Знойдзена:{' '}
            <span className="font-medium text-gray-800">
              {visibleRows.length}
            </span>
            {searchQuery.trim() || supplierFilterActive
              ? ` (з ${rows.length})`
              : null}
            {supplierFilterActive ? (
              <button
                type="button"
                onClick={() => setSelectedSuppliers([])}
                className="ml-3 text-amber-800 underline-offset-2 hover:underline"
              >
                Скінуць фільтр пастаўшчыка
              </button>
            ) : null}
          </div>
          <div className="overflow-x-auto">
            <table className="min-w-full text-left text-sm">
              <thead className="bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                <tr>
                  <th className="px-4 py-3">Назва</th>
                  <th className="px-4 py-3">Штрыхкод</th>
                  <th className="px-4 py-3">
                    <button
                      type="button"
                      ref={supplierMenu.triggerRef}
                      onClick={supplierMenu.toggle}
                      className={`inline-flex items-center gap-1 uppercase tracking-wide transition ${
                        supplierFilterActive || supplierMenu.open
                          ? 'text-amber-800'
                          : 'text-gray-500 hover:text-gray-800'
                      }`}
                      aria-expanded={supplierMenu.open}
                      aria-haspopup="listbox"
                      aria-label="Фільтр па пастаўшчыку"
                    >
                      <span>Пастаўшчык</span>
                      <span aria-hidden>{supplierMenu.open ? '▴' : '▾'}</span>
                    </button>
                  </th>
                  <SortableHeader
                    label="Кошт"
                    column="standardPrice"
                    sortKey={sortKey}
                    sortDir={sortDir}
                    onSort={handleSort}
                  />
                  <SortableHeader
                    label="Цана"
                    column="listPrice"
                    sortKey={sortKey}
                    sortDir={sortDir}
                    onSort={handleSort}
                  />
                  <SortableHeader
                    label="У наяўнасці"
                    column="quantityInStock"
                    sortKey={sortKey}
                    sortDir={sortDir}
                    onSort={handleSort}
                  />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {visibleRows.length === 0 ? (
                  <tr>
                    <td
                      colSpan={6}
                      className="px-4 py-12 text-center text-gray-500"
                    >
                      {rows.length === 0
                        ? 'Прадукты не знойдзены ў Odoo.'
                        : 'Нічога не знойдзена па фільтрах.'}
                    </td>
                  </tr>
                ) : (
                  visibleRows.map((row) => (
                    <tr
                      key={row.id}
                      className="cursor-pointer hover:bg-amber-50/40"
                      onClick={() => openInOdoo(row)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault();
                          openInOdoo(row);
                        }
                      }}
                      tabIndex={0}
                      role="link"
                      aria-label={`Адкрыць «${row.name}» у Odoo`}
                    >
                      <td className="px-4 py-3 font-medium text-gray-900">
                        {row.name}
                      </td>
                      <td className="px-4 py-3 text-gray-600">
                        {row.barcode || '—'}
                      </td>
                      <td className="px-4 py-3 text-gray-600">
                        {row.supplierName || '—'}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums text-gray-900">
                        {formatPrice(row.standardPrice)}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums text-gray-900">
                        {formatPrice(row.listPrice)}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums text-gray-900">
                        {formatQty(row.quantityInStock)}
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
                  className="text-xs font-medium text-amber-800 hover:underline"
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
                      className="size-4 rounded border-gray-300 accent-amber-700 focus:ring-amber-500"
                      checked={selectedSuppliers.includes(supplier)}
                      onChange={() => toggleSupplierFilter(supplier)}
                    />
                    <span className="truncate" title={supplierLabel(supplier)}>
                      {supplierLabel(supplier)}
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
