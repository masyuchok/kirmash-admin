'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiExternalLink, FiSearch, FiX } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { fetchProductsWithSuppliers, syncUnsyncedProductRow } from '@/lib/api/products';
import type { ProductWithSuppliers } from '@/types/product';

type ProductTableRow = ProductWithSuppliers & {
  supplierId: number | null;
  supplierName: string;
  rowKey: string;
  rowSource: 'shopify' | 'supply';
};

export default function ProductsClient() {
  const pageSize = 50;
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [rows, setRows] = useState<ProductWithSuppliers[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedProductTypes, setSelectedProductTypes] = useState<string[]>([]);
  const [selectedSuppliers, setSelectedSuppliers] = useState<string[]>([]);
  const [syncFilter, setSyncFilter] = useState<'all' | 'supply' | 'shopify'>('all');
  const [quantitySortDirection, setQuantitySortDirection] = useState<'asc' | 'desc'>('desc');
  const [page, setPage] = useState(1);
  const [supplierMenuOpen, setSupplierMenuOpen] = useState(false);
  const [typeMenuOpen, setTypeMenuOpen] = useState(false);
  const [menuMounted, setMenuMounted] = useState(false);
  const [menuPosition, setMenuPosition] = useState({ top: 0, left: 0 });
  const [typeMenuPosition, setTypeMenuPosition] = useState({ top: 0, left: 0 });
  const [syncingRowKey, setSyncingRowKey] = useState<string | null>(null);
  const [recentSyncedQty, setRecentSyncedQty] = useState<Record<string, number>>({});
  const supplierTriggerRef = useRef<HTMLButtonElement | null>(null);
  const typeTriggerRef = useRef<HTMLButtonElement | null>(null);
  const supplierMenuRef = useRef<HTMLDivElement | null>(null);
  const typeMenuRef = useRef<HTMLDivElement | null>(null);

  const displayRows = useMemo<ProductTableRow[]>(() => {
    return rows.flatMap((row) => {
      const list: ProductTableRow[] = [];
      list.push({
        ...row,
        supplierId: null,
        supplierName: row.lastSyncedSupplierName.trim() || '—',
        quantityInStock: row.shopifyQuantityInStock,
        rowSource: 'shopify',
        rowKey: `${row.shopifyProductId}::shopify`,
      });

      for (const unsynced of row.unsyncedSuppliers) {
        list.push({
          ...row,
          supplierId: unsynced.supplierId,
          rowSource: 'supply',
          rowKey: `${row.shopifyProductId}::supply::${unsynced.supplierId}`,
          supplierName: unsynced.supplierName || '—',
          quantityInStock: unsynced.quantity,
        });
      }

      return list.map((item) => ({
        ...item,
        ...row,
        quantityInStock: item.quantityInStock,
        supplierId: item.supplierId,
        supplierName: item.supplierName,
        rowSource: item.rowSource,
        rowKey: item.rowKey,
      }));
    });
  }, [rows]);

  const supplierOptions = useMemo(() => {
    const names = displayRows
      .map((row) => row.supplierName)
      .filter((name) => name.trim().length > 0 && name !== '—');
    return Array.from(new Set(names)).sort((a, b) => a.localeCompare(b, 'be'));
  }, [displayRows]);

  const productTypeOptions = useMemo(() => {
    return Array.from(
      new Set(rows.map((row) => row.productType).filter((type) => type.trim().length > 0))
    ).sort((a, b) => a.localeCompare(b, 'be'));
  }, [rows]);

  const visibleRows = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();

    const filteredBySupplier =
      selectedSuppliers.length === 0
        ? displayRows
        : displayRows.filter((row) => selectedSuppliers.includes(row.supplierName));

    const filtered =
      q.length === 0
        ? filteredBySupplier
        : filteredBySupplier.filter((row) => row.productName.toLowerCase().includes(q));

    const filteredByType =
      selectedProductTypes.length === 0
        ? filtered
        : filtered.filter((row) => selectedProductTypes.includes(row.productType));

    const filteredBySyncFlag =
      syncFilter === 'all'
        ? filteredByType
        : filteredByType.filter((row) =>
            syncFilter === 'supply' ? row.rowSource === 'supply' : row.rowSource === 'shopify'
          );

    return [...filteredBySyncFlag].sort((a, b) =>
      quantitySortDirection === 'asc'
        ? a.quantityInStock - b.quantityInStock
        : b.quantityInStock - a.quantityInStock
    );
  }, [displayRows, selectedSuppliers, searchQuery, selectedProductTypes, syncFilter, quantitySortDirection]);

  const totalPages = Math.max(1, Math.ceil(visibleRows.length / pageSize));
  const pagedRows = useMemo(() => {
    const safePage = Math.min(page, totalPages);
    const start = (safePage - 1) * pageSize;
    return visibleRows.slice(start, start + pageSize);
  }, [visibleRows, page, totalPages]);

  useEffect(() => {
    setPage(1);
  }, [searchQuery, selectedSuppliers, selectedProductTypes, syncFilter, quantitySortDirection]);

  const toggleSupplierFilter = (supplier: string) => {
    setSelectedSuppliers((prev) =>
      prev.includes(supplier)
        ? prev.filter((item) => item !== supplier)
        : [...prev, supplier]
    );
  };

  const toggleTypeFilter = (type: string) => {
    setSelectedProductTypes((prev) =>
      prev.includes(type)
        ? prev.filter((item) => item !== type)
        : [...prev, type]
    );
  };

  const updateMenuPosition = () => {
    if (!supplierTriggerRef.current) return;
    const rect = supplierTriggerRef.current.getBoundingClientRect();
    const viewportPadding = 8;
    const menuWidth = 256; // w-64
    const estimatedMenuHeight = 280;

    const maxLeft = window.innerWidth - menuWidth - viewportPadding;
    const left = Math.max(viewportPadding, Math.min(rect.left, maxLeft));

    let top = rect.bottom + 8;
    if (top + estimatedMenuHeight > window.innerHeight - viewportPadding) {
      top = Math.max(viewportPadding, rect.top - estimatedMenuHeight - 8);
    }

    setMenuPosition({ top, left });
  };

  const updateTypeMenuPosition = () => {
    if (!typeTriggerRef.current) return;
    const rect = typeTriggerRef.current.getBoundingClientRect();
    const viewportPadding = 8;
    const menuWidth = 256; // w-64
    const estimatedMenuHeight = 280;

    const maxLeft = window.innerWidth - menuWidth - viewportPadding;
    const left = Math.max(viewportPadding, Math.min(rect.left, maxLeft));

    let top = rect.bottom + 8;
    if (top + estimatedMenuHeight > window.innerHeight - viewportPadding) {
      top = Math.max(viewportPadding, rect.top - estimatedMenuHeight - 8);
    }

    setTypeMenuPosition({ top, left });
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

  useEffect(() => {
    if (!typeMenuOpen) return;
    updateTypeMenuPosition();

    const onDocClick = (event: MouseEvent) => {
      const target = event.target as Node;
      const clickedMenu = typeMenuRef.current?.contains(target);
      const clickedTrigger = typeTriggerRef.current?.contains(target);
      if (!clickedMenu && !clickedTrigger) {
        setTypeMenuOpen(false);
      }
    };
    const onViewportChange = () => updateTypeMenuPosition();

    document.addEventListener('mousedown', onDocClick);
    window.addEventListener('resize', onViewportChange);
    window.addEventListener('scroll', onViewportChange, true);
    return () => {
      document.removeEventListener('mousedown', onDocClick);
      window.removeEventListener('resize', onViewportChange);
      window.removeEventListener('scroll', onViewportChange, true);
    };
  }, [typeMenuOpen]);

  useEffect(() => {
    setTopbarPage({
      title: 'Прадукты',
      subtitle: loading
        ? 'Загрузка…'
        : `Усяго прадуктаў: ${rows.length}${
            selectedSuppliers.length > 0 || searchQuery.trim()
              || selectedProductTypes.length > 0
              || syncFilter !== 'all'
              ? ` · паказана: ${visibleRows.length}`
              : ''
          }`,
    });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [
    loading,
    rows.length,
    selectedSuppliers.length,
    selectedProductTypes.length,
    syncFilter,
    searchQuery,
    visibleRows.length,
    setTopbarButtons,
    setTopbarPage,
  ]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    fetchProductsWithSuppliers()
      .then((data) => {
        if (!cancelled) setRows(data);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Памылка загрузкі');
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const reloadProducts = async (expectedQtyByProductId?: Record<string, number>) => {
    const data = await fetchProductsWithSuppliers(true);
    if (!expectedQtyByProductId || Object.keys(expectedQtyByProductId).length === 0) {
      setRows(data);
      return;
    }

    setRows(
      data.map((item) => {
        const expected = expectedQtyByProductId[item.shopifyProductId];
        if (typeof expected !== 'number') return item;
        // Shopify totalInventory can lag briefly after write; keep freshest known value in UI.
        if (item.shopifyQuantityInStock >= expected) return item;
        return {
          ...item,
          shopifyQuantityInStock: expected,
          quantityInStock: item.unsyncedSuppliers.length > 0 ? item.quantityInStock : expected,
        };
      })
    );
  };

  const handleSyncRow = async (row: ProductTableRow) => {
    if (row.rowSource !== 'supply' || !row.supplierId) return;
    setSyncingRowKey(row.rowKey);
    setError(null);
    setSuccess(null);
    try {
      const result = await syncUnsyncedProductRow(row.shopifyProductId, row.supplierId);
      const nextRecent = { ...recentSyncedQty, [row.shopifyProductId]: result.newAvailable };
      setRecentSyncedQty(nextRecent);
      setRows((prev) =>
        prev.map((item) => {
          if (item.shopifyProductId !== row.shopifyProductId) return item;

          const remainingUnsynced = item.unsyncedSuppliers.filter((s) => s.supplierId !== row.supplierId);
          const remainingUnsyncedQty = remainingUnsynced.reduce((sum, s) => sum + s.quantity, 0);

          return {
            ...item,
            unsyncedSuppliers: remainingUnsynced,
            hasSupplyQuantityOverride: remainingUnsynced.length > 0,
            quantityInStock: remainingUnsynced.length > 0 ? remainingUnsyncedQty : result.newAvailable,
            shopifyQuantityInStock: result.newAvailable,
            lastSyncedSupplierName: row.supplierName || item.lastSyncedSupplierName,
          };
        })
      );
      setSuccess(
        `Сінхранізавана: +${result.syncedQuantity}, было ${result.previousAvailable}, стала ${result.newAvailable}.`
      );
      await reloadProducts(nextRecent);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка сінхранізацыі');
    } finally {
      setSyncingRowKey(null);
    }
  };

  if (loading) {
    return <LoadingSpinner label="Загрузка прадуктаў..." />;
  }

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      {error && (
        <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">{error}</div>
      )}
      {success && (
        <div className="rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {success}
        </div>
      )}

      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 bg-gray-50/50 px-6 py-4">
          <label className="mb-1.5 block text-xs font-semibold uppercase tracking-wide text-gray-500">
            Пошук па назве
          </label>
          <div className="flex flex-wrap items-center gap-3">
            <div className="flex w-full max-w-xl items-center gap-2 rounded-xl border border-gray-200 bg-white px-3 py-2.5 shadow-sm focus-within:border-primary focus-within:ring-2 focus-within:ring-primary/20">
              <FiSearch className="size-4 shrink-0 text-gray-400" aria-hidden />
              <input
                type="text"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.currentTarget.value)}
                placeholder="Увядзіце назву прадукту..."
                className="w-full border-0 bg-transparent text-sm text-gray-900 placeholder:text-gray-400 focus:outline-none"
              />
              {searchQuery.trim() && (
                <button
                  type="button"
                  onClick={() => setSearchQuery('')}
                  className="inline-flex size-6 items-center justify-center rounded-md text-gray-400 transition hover:bg-gray-100 hover:text-gray-600"
                  aria-label="Ачысціць пошук"
                >
                  <FiX className="size-4" />
                </button>
              )}
            </div>
            <div className="inline-flex items-center gap-1 rounded-xl border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 shadow-sm">
              <span className="font-medium">Тып</span>
              <button
                type="button"
                ref={typeTriggerRef}
                className="inline-flex items-center rounded p-0.5 text-gray-500 transition hover:bg-gray-100 hover:text-gray-700"
                aria-label="Фільтр па тыпе прадукту"
                onClick={() => setTypeMenuOpen((prev) => !prev)}
              >
                <span aria-hidden>{typeMenuOpen ? '▴' : '▾'}</span>
              </button>
            </div>
            <label className="inline-flex items-center gap-2 rounded-xl border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 shadow-sm">
              <span className="font-medium">Крыніца колькасці</span>
              <select
                value={syncFilter}
                onChange={(e) => setSyncFilter(e.currentTarget.value as 'all' | 'supply' | 'shopify')}
                className="rounded-md border border-gray-200 bg-white px-2 py-1 text-sm text-gray-700 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
              >
                <option value="all">Усе</option>
                <option value="shopify">Толькі Shopify</option>
                <option value="supply">Толькі не сінхранізаваныя</option>
              </select>
            </label>
            <div
              className="inline-flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800"
              title="Падсветка радка азначае: колькасць узята з пастаўкі, бо сінхранізацыя з Shopify адключаная."
            >
              <span className="inline-block size-2 rounded-full bg-amber-500" />
              Радкі з пастаўкі без Shopify sync
            </div>
          </div>
        </div>
        {visibleRows.length === 0 ? (
          <div className="px-6 py-16 text-center">
            <p className="text-sm font-medium text-gray-900">
              {rows.length === 0 ? 'Прадуктаў пакуль няма' : 'Нічога не знойдзена'}
            </p>
            <p className="mt-1 text-sm text-gray-500">
              {rows.length === 0
                ? 'Калі ў Shopify з’явяцца прадукты, яны будуць паказаны тут.'
                : 'Змяніце пошук, тып або фільтр пастаўшчыкоў, каб убачыць іншыя прадукты.'}
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50/90 text-xs font-semibold uppercase tracking-wide text-gray-500 backdrop-blur-sm">
                  <th className="whitespace-nowrap px-6 py-3.5">Назва</th>
                  <th className="whitespace-nowrap px-6 py-3.5 text-right">
                    <button
                      type="button"
                      className="inline-flex items-center gap-1 rounded text-xs font-semibold uppercase tracking-wide text-gray-500 transition hover:text-gray-700"
                      onClick={() =>
                        setQuantitySortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
                      }
                      aria-label="Сартаваць па колькасці ў наяўнасці"
                    >
                      У наяўнасці
                      <span aria-hidden>{quantitySortDirection === 'asc' ? '↑' : '↓'}</span>
                    </button>
                  </th>
                  <th className="whitespace-nowrap px-6 py-3.5 text-right">У Shopify</th>
                  <th className="whitespace-nowrap px-6 py-3.5">
                    <div className="inline-flex items-center gap-1">
                      <span>Пастаўшчыкі</span>
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
                  </th>
                  <th className="whitespace-nowrap px-6 py-3.5 text-right">Дзеянне</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100 bg-white">
                {pagedRows.map((row) => (
                  <tr
                    key={row.rowKey}
                    className={`transition hover:bg-gray-50/80 ${
                      row.rowSource === 'supply' && row.hasSupplyQuantityOverride
                        ? 'bg-amber-50/70'
                        : ''
                    }`}
                  >
                    <td className="px-6 py-3.5 font-medium text-gray-900">
                      <div className="flex items-center gap-3">
                        {row.mainImageUrl ? (
                          <a
                            href={row.mainImageUrl}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="inline-flex rounded-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40"
                            aria-label={`Адкрыць арыгінал выявы: ${row.productName}`}
                          >
                            <img
                              src={row.mainImageUrl}
                              alt={row.productName}
                              className="size-8 rounded-md border border-gray-200 object-cover"
                              loading="lazy"
                            />
                          </a>
                        ) : (
                          <div className="size-8 rounded-md border border-gray-200 bg-gray-100" />
                        )}
                        {row.productAdminUrl ? (
                          <a
                            href={row.productAdminUrl}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="inline-flex items-center gap-1 hover:underline"
                          >
                            {row.productName}
                            <FiExternalLink className="size-3.5 text-gray-500" aria-hidden />
                          </a>
                        ) : (
                          <span>{row.productName}</span>
                        )}
                      </div>
                    </td>
                    <td className="whitespace-nowrap px-6 py-3.5 text-right tabular-nums">
                      <div className="inline-flex items-center gap-2">
                        {row.rowSource === 'supply' && row.hasSupplyQuantityOverride && (
                          <span className="inline-flex rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-medium text-amber-800 ring-1 ring-inset ring-amber-500/30">
                            з пастаўкі
                          </span>
                        )}
                        {row.quantityInStock <= 0 ? (
                          <span className="inline-flex rounded-full bg-red-50 px-2.5 py-0.5 text-xs font-medium text-red-700 ring-1 ring-inset ring-red-600/20">
                            0
                          </span>
                        ) : (
                          <span className="text-gray-700">{row.quantityInStock}</span>
                        )}
                      </div>
                    </td>
                    <td className="whitespace-nowrap px-6 py-3.5 text-right tabular-nums text-gray-700">
                      {row.shopifyQuantityInStock}
                    </td>
                    <td className="px-6 py-3.5 text-gray-700">
                      {row.supplierName}
                    </td>
                    <td className="whitespace-nowrap px-6 py-3.5 text-right">
                      {row.rowSource === 'supply' && row.supplierId ? (
                        <button
                          type="button"
                          onClick={() => handleSyncRow(row)}
                          disabled={syncingRowKey === row.rowKey}
                          className="inline-flex items-center gap-2 rounded-lg border border-primary/30 bg-primary/5 px-3 py-1.5 text-xs font-medium text-primary transition hover:bg-primary/10 disabled:opacity-50"
                        >
                          {syncingRowKey === row.rowKey && (
                            <span className="size-3.5 animate-spin rounded-full border-2 border-primary/30 border-t-primary" />
                          )}
                          Сінхранізаваць з Shopify
                        </button>
                      ) : (
                        <span className="text-xs text-gray-400">—</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {visibleRows.length > 0 && (
          <div className="flex items-center justify-between border-t border-gray-100 px-6 py-3">
            <p className="text-sm text-gray-500">
              Старонка {Math.min(page, totalPages)} з {totalPages}
            </p>
            <div className="flex items-center gap-2">
              <button
                type="button"
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm text-gray-700 transition hover:bg-gray-50 disabled:opacity-50"
                onClick={() => setPage((p) => Math.max(1, p - 1))}
                disabled={page <= 1}
              >
                Назад
              </button>
              <button
                type="button"
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm text-gray-700 transition hover:bg-gray-50 disabled:opacity-50"
                onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                disabled={page >= totalPages}
              >
                Далей
              </button>
            </div>
          </div>
        )}
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
              {supplierOptions.length === 0 ? (
                <p className="text-xs text-gray-500">Няма пастаўшчыкоў</p>
              ) : (
                supplierOptions.map((supplier) => (
                  <label
                    key={supplier}
                    className="flex items-center gap-2 text-sm font-normal normal-case text-gray-700"
                  >
                    <input
                      type="checkbox"
                      className="size-4 rounded border-gray-300 accent-primary focus:ring-primary"
                      checked={selectedSuppliers.includes(supplier)}
                      onChange={() => toggleSupplierFilter(supplier)}
                    />
                    <span className="truncate" title={supplier}>
                      {supplier}
                    </span>
                  </label>
                ))
              )}
            </div>
          </div>,
          document.body
        )}
      {menuMounted &&
        typeMenuOpen &&
        createPortal(
          <div
            ref={typeMenuRef}
            className="fixed z-[70] w-64 rounded-lg border border-gray-200 bg-white p-3 shadow-lg"
            style={{ top: `${typeMenuPosition.top}px`, left: `${typeMenuPosition.left}px` }}
          >
            <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-500">
              Фільтр па тыпе
            </p>
            <div className="max-h-56 space-y-2 overflow-auto pr-1">
              {productTypeOptions.length === 0 ? (
                <p className="text-xs text-gray-500">Няма тыпаў</p>
              ) : (
                productTypeOptions.map((type) => (
                  <label
                    key={type}
                    className="flex items-center gap-2 text-sm font-normal normal-case text-gray-700"
                  >
                    <input
                      type="checkbox"
                      className="size-4 rounded border-gray-300 accent-primary focus:ring-primary"
                      checked={selectedProductTypes.includes(type)}
                      onChange={() => toggleTypeFilter(type)}
                    />
                    <span className="truncate" title={type}>
                      {type}
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
