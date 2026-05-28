'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiArrowLeft, FiChevronDown, FiRefreshCw } from 'react-icons/fi';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { fetchSupplierInventory } from '@/lib/api/suppliers';
import type { SupplierInventoryRow } from '@/types/supplier-inventory';

type Props = {
  supplierId?: number | null;
  supplierName?: string;
  onBack: () => void;
};

type SortColumn = 'price' | 'stock' | 'toPay';

function formatMoney(value: number): string {
  return value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function getSortValue(row: SupplierInventoryRow, column: SortColumn): number {
  switch (column) {
    case 'price':
      return row.supplierPrice;
    case 'stock':
      return row.quantityInStock;
    case 'toPay':
      return row.quantityToPay;
  }
}

export default function SupplierInventoryClient({ supplierId = null, supplierName, onBack }: Props) {
  const [rows, setRows] = useState<SupplierInventoryRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [salesSyncedAtUtc, setSalesSyncedAtUtc] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [supplierFilterOpen, setSupplierFilterOpen] = useState(false);
  const [selectedSupplierIds, setSelectedSupplierIds] = useState<Set<number>>(() => new Set());
  const [menuMounted, setMenuMounted] = useState(false);
  const [menuPosition, setMenuPosition] = useState({ top: 0, left: 0 });
  const [sort, setSort] = useState<{ column: SortColumn; direction: 'asc' | 'desc' } | null>(null);
  const supplierFilterTriggerRef = useRef<HTMLButtonElement | null>(null);
  const supplierFilterMenuRef = useRef<HTMLDivElement | null>(null);

  const showSupplierColumn = !supplierId;

  const updateSupplierFilterMenuPosition = () => {
    if (!supplierFilterTriggerRef.current) return;
    const rect = supplierFilterTriggerRef.current.getBoundingClientRect();
    const viewportPadding = 8;
    const menuWidth = 224; // w-56
    const estimatedMenuHeight = 280;

    const maxLeft = window.innerWidth - menuWidth - viewportPadding;
    const left = Math.max(viewportPadding, Math.min(rect.left, maxLeft));

    let top = rect.bottom + 8;
    if (top + estimatedMenuHeight > window.innerHeight - viewportPadding) {
      top = Math.max(viewportPadding, rect.top - estimatedMenuHeight - 8);
    }

    setMenuPosition({ top, left });
  };

  const supplierOptions = useMemo(() => {
    const map = new Map<number, string>();
    for (const row of rows) {
      if (row.supplierId > 0) {
        map.set(row.supplierId, row.supplierName || String(row.supplierId));
      }
    }
    return [...map.entries()].sort((a, b) => a[1].localeCompare(b[1], 'be'));
  }, [rows]);

  useEffect(() => {
    setSelectedSupplierIds(new Set(supplierOptions.map(([id]) => id)));
  }, [supplierOptions]);

  useEffect(() => {
    setMenuMounted(true);
  }, []);

  useEffect(() => {
    if (!supplierFilterOpen) return;
    updateSupplierFilterMenuPosition();

    const onDocClick = (event: MouseEvent) => {
      const target = event.target as Node;
      const clickedMenu = supplierFilterMenuRef.current?.contains(target);
      const clickedTrigger = supplierFilterTriggerRef.current?.contains(target);
      if (!clickedMenu && !clickedTrigger) {
        setSupplierFilterOpen(false);
      }
    };
    const onViewportChange = () => updateSupplierFilterMenuPosition();

    document.addEventListener('mousedown', onDocClick);
    window.addEventListener('resize', onViewportChange);
    window.addEventListener('scroll', onViewportChange, true);
    return () => {
      document.removeEventListener('mousedown', onDocClick);
      window.removeEventListener('resize', onViewportChange);
      window.removeEventListener('scroll', onViewportChange, true);
    };
  }, [supplierFilterOpen]);

  const isSupplierFilterCustomized =
    showSupplierColumn &&
    supplierOptions.length > 0 &&
    selectedSupplierIds.size < supplierOptions.length;

  const loadInventory = (refresh = false) => {
    setLoading(true);
    setError(null);
    return fetchSupplierInventory(supplierId ?? undefined, { refresh })
      .then((data) => {
        setRows(data.rows);
        setSalesSyncedAtUtc(data.salesSyncedAtUtc);
      })
      .catch((err: unknown) => {
        setError(err instanceof Error ? err.message : 'Памылка загрузкі інвентарызацыі');
      })
      .finally(() => {
        setLoading(false);
        setRefreshing(false);
      });
  };

  useEffect(() => {
    void loadInventory();
  }, [supplierId]);

  const handleRefreshSales = () => {
    setRefreshing(true);
    void loadInventory(true);
  };

  const visibleRows = useMemo(() => {
    let filtered = rows;
    if (showSupplierColumn && supplierOptions.length > 0) {
      filtered = filtered.filter((row) => selectedSupplierIds.has(row.supplierId));
    }
    const q = searchQuery.trim().toLowerCase();
    if (q) {
      filtered = filtered.filter((row) => {
        return (
          row.productName.toLowerCase().includes(q) ||
          row.supplierName.toLowerCase().includes(q)
        );
      });
    }
    if (!sort) return filtered;
    const direction = sort.direction === 'asc' ? 1 : -1;
    return [...filtered].sort(
      (a, b) =>
        (getSortValue(a, sort.column) - getSortValue(b, sort.column)) * direction
    );
  }, [rows, searchQuery, showSupplierColumn, supplierOptions.length, selectedSupplierIds, sort]);

  const handleSortClick = (column: SortColumn) => {
    setSort((prev) => {
      if (prev?.column === column) {
        return { column, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { column, direction: 'desc' };
    });
  };

  const toggleSupplierFilter = (supplierIdValue: number, checked: boolean) => {
    setSelectedSupplierIds((prev) => {
      const next = new Set(prev);
      if (checked) next.add(supplierIdValue);
      else next.delete(supplierIdValue);
      return next;
    });
  };

  if (loading) {
    return <LoadingSpinner label="Загрузка інвентарызацыі..." />;
  }

  if (error) {
    return (
      <div className="mx-auto w-full max-w-6xl space-y-4">
        <button
          type="button"
          onClick={onBack}
          className="inline-flex items-center gap-2 text-sm font-medium text-primary hover:text-primary-hover"
        >
          <FiArrowLeft className="size-4" aria-hidden />
          Назад
        </button>
        <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">{error}</div>
      </div>
    );
  }

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      <button
        type="button"
        onClick={onBack}
        className="inline-flex items-center gap-2 text-sm font-medium text-primary hover:text-primary-hover"
      >
        <FiArrowLeft className="size-4" aria-hidden />
        Назад да пастаўшчыкоў
      </button>

      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 px-6 py-4">
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div>
              <h2 className="text-base font-semibold text-gray-900">Інвентарызацыя</h2>
              <p className="mt-1 text-sm text-gray-500">
                {supplierName
                  ? `Пастаўшчык: ${supplierName}`
                  : 'Усе пастаўшчыкі'}
                {salesSyncedAtUtc && (
                  <span className="block text-xs text-gray-400">
                    Продажы абноўлены: {new Date(salesSyncedAtUtc).toLocaleString('be-BY')}
                  </span>
                )}
              </p>
            </div>
            <button
              type="button"
              onClick={() => void handleRefreshSales()}
              disabled={refreshing || loading}
              className="inline-flex size-9 items-center justify-center rounded-lg border border-gray-200 bg-white text-gray-700 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:opacity-60"
              aria-label="Абнавіць продажы"
              title="Абнавіць продажы"
            >
              {refreshing ? (
                <span className="size-4 animate-spin rounded-full border-2 border-gray-300 border-t-gray-700" />
              ) : (
                <FiRefreshCw className="size-4" aria-hidden />
              )}
            </button>
            <label className="w-full max-w-xs space-y-1">
              <span className="text-xs font-medium uppercase tracking-wide text-gray-500">Пошук</span>
              <input
                type="search"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.currentTarget.value)}
                placeholder="Тавар або пастаўшчык"
                className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
              />
            </label>
          </div>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-left text-sm">
            <thead>
              <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                {showSupplierColumn && (
                  <th className="px-4 py-2.5">
                    <div className="flex items-center gap-2">
                      <span>Пастаўшчык</span>
                      <button
                        type="button"
                        ref={supplierFilterTriggerRef}
                        onClick={() => {
                          setSupplierFilterOpen((prev) => {
                            const next = !prev;
                            if (next) updateSupplierFilterMenuPosition();
                            return next;
                          });
                        }}
                        className={`relative inline-flex items-center justify-center rounded-md border bg-white p-1 transition ${
                          isSupplierFilterCustomized
                            ? 'border-primary/50 text-primary'
                            : 'border-gray-200 text-gray-600 hover:border-primary/40 hover:bg-primary/10 hover:text-primary'
                        }`}
                        aria-label="Фільтр па пастаўшчыку"
                        title="Фільтр па пастаўшчыку"
                      >
                        <FiChevronDown className="size-3.5" aria-hidden />
                        {isSupplierFilterCustomized && (
                          <span className="absolute -right-0.5 -top-0.5 size-1.5 rounded-full bg-primary" />
                        )}
                      </button>
                    </div>
                  </th>
                )}
                <th className="px-4 py-2.5">Тавар</th>
                <th className="px-4 py-2.5 text-right">
                  <button
                    type="button"
                    onClick={() => handleSortClick('price')}
                    className="inline-flex w-full items-center justify-end gap-1 rounded text-xs font-semibold uppercase tracking-wide text-gray-500 transition hover:text-gray-700"
                    aria-label="Сартаваць па цане пастаўкі"
                  >
                    Цана пастаўкі
                    {sort?.column === 'price' && (
                      <span aria-hidden>{sort.direction === 'asc' ? '↑' : '↓'}</span>
                    )}
                  </button>
                </th>
                <th className="px-4 py-2.5 text-right">
                  <button
                    type="button"
                    onClick={() => handleSortClick('stock')}
                    className="inline-flex w-full items-center justify-end gap-1 rounded text-xs font-semibold uppercase tracking-wide text-gray-500 transition hover:text-gray-700"
                    aria-label="Сартаваць па колькасці ў наяўнасці"
                  >
                    У наяўнасці
                    {sort?.column === 'stock' && (
                      <span aria-hidden>{sort.direction === 'asc' ? '↑' : '↓'}</span>
                    )}
                  </button>
                </th>
                <th className="px-4 py-2.5 text-right">
                  <button
                    type="button"
                    onClick={() => handleSortClick('toPay')}
                    className="inline-flex w-full items-center justify-end gap-1 rounded text-xs font-semibold uppercase tracking-wide text-gray-500 transition hover:text-gray-700"
                    aria-label="Сартаваць па колькасці да аплаты"
                  >
                    Да аплаты
                    {sort?.column === 'toPay' && (
                      <span aria-hidden>{sort.direction === 'asc' ? '↑' : '↓'}</span>
                    )}
                  </button>
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {visibleRows.map((row) => (
                <tr key={`${row.supplierId}-${row.shopifyProductId}`} className="hover:bg-gray-50/80">
                  {showSupplierColumn && (
                    <td className="px-4 py-3 font-medium text-gray-900">{row.supplierName || '—'}</td>
                  )}
                  <td className="px-4 py-3 text-gray-800">{row.productName}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{formatMoney(row.supplierPrice)}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{row.quantityInStock}</td>
                  <td
                    className={`px-4 py-3 text-right tabular-nums font-medium ${
                      row.quantityToPay > 0
                        ? 'text-amber-700'
                        : row.quantityToPay < 0
                          ? 'text-emerald-700'
                          : 'text-gray-700'
                    }`}
                  >
                    {row.quantityToPay}
                  </td>
                </tr>
              ))}
              {visibleRows.length === 0 && (
                <tr>
                  <td
                    colSpan={showSupplierColumn ? 5 : 4}
                    className="px-4 py-8 text-center text-sm text-gray-500"
                  >
                    {rows.length === 0 ? 'Няма даных для інвентарызацыі.' : 'Нічога не знойдзена.'}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
      {menuMounted &&
        supplierFilterOpen &&
        createPortal(
          <div
            ref={supplierFilterMenuRef}
            className="fixed z-[70] w-56 rounded-lg border border-gray-200 bg-white p-2 shadow-lg"
            style={{ top: `${menuPosition.top}px`, left: `${menuPosition.left}px` }}
          >
            <div className="max-h-64 space-y-1 overflow-y-auto pr-1">
              {supplierOptions.map(([id, name]) => (
                <label
                  key={id}
                  className="flex items-center gap-2 rounded-md px-2 py-1.5 text-xs font-medium normal-case tracking-normal text-gray-700 hover:bg-gray-50"
                >
                  <input
                    type="checkbox"
                    checked={selectedSupplierIds.has(id)}
                    onChange={(e) => toggleSupplierFilter(id, e.currentTarget.checked)}
                    className="size-3.5 rounded border-gray-300 accent-primary"
                  />
                  <span className="truncate" title={name}>
                    {name}
                  </span>
                </label>
              ))}
            </div>
          </div>,
          document.body
        )}
    </div>
  );
}
