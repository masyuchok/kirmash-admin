'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiExternalLink, FiSearch, FiX } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { fetchProductsWithSuppliers } from '@/lib/api/products';
import type { ProductWithSuppliers } from '@/types/product';

export default function ProductsClient() {
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [rows, setRows] = useState<ProductWithSuppliers[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedProductTypes, setSelectedProductTypes] = useState<string[]>([]);
  const [selectedSuppliers, setSelectedSuppliers] = useState<string[]>([]);
  const [quantitySortDirection, setQuantitySortDirection] = useState<'asc' | 'desc'>('desc');
  const [supplierMenuOpen, setSupplierMenuOpen] = useState(false);
  const [typeMenuOpen, setTypeMenuOpen] = useState(false);
  const [menuMounted, setMenuMounted] = useState(false);
  const [menuPosition, setMenuPosition] = useState({ top: 0, left: 0 });
  const [typeMenuPosition, setTypeMenuPosition] = useState({ top: 0, left: 0 });
  const supplierTriggerRef = useRef<HTMLButtonElement | null>(null);
  const typeTriggerRef = useRef<HTMLButtonElement | null>(null);
  const supplierMenuRef = useRef<HTMLDivElement | null>(null);
  const typeMenuRef = useRef<HTMLDivElement | null>(null);

  const supplierOptions = useMemo(() => {
    return Array.from(
      new Set(rows.flatMap((row) => row.suppliers).filter((name) => name.trim().length > 0))
    ).sort((a, b) => a.localeCompare(b, 'be'));
  }, [rows]);

  const productTypeOptions = useMemo(() => {
    return Array.from(
      new Set(rows.map((row) => row.productType).filter((type) => type.trim().length > 0))
    ).sort((a, b) => a.localeCompare(b, 'be'));
  }, [rows]);

  const visibleRows = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();

    const filteredBySupplier =
      selectedSuppliers.length === 0
        ? rows
        : rows.filter((row) => row.suppliers.some((name) => selectedSuppliers.includes(name)));

    const filtered =
      q.length === 0
        ? filteredBySupplier
        : filteredBySupplier.filter((row) => row.productName.toLowerCase().includes(q));

    const filteredByType =
      selectedProductTypes.length === 0
        ? filtered
        : filtered.filter((row) => selectedProductTypes.includes(row.productType));

    return [...filteredByType].sort((a, b) =>
      quantitySortDirection === 'asc'
        ? a.quantityInStock - b.quantityInStock
        : b.quantityInStock - a.quantityInStock
    );
  }, [rows, selectedSuppliers, searchQuery, selectedProductTypes, quantitySortDirection]);

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

  if (loading) {
    return (
      <div className="mx-auto w-full max-w-6xl space-y-6">
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="divide-y divide-gray-100 p-4">
            {Array.from({ length: 6 }).map((_, i) => (
              <div key={i} className="h-12 animate-pulse rounded-md bg-gray-50" />
            ))}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      {error && (
        <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">{error}</div>
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
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100 bg-white">
                {visibleRows.map((row) => (
                  <tr key={row.shopifyProductId} className="transition hover:bg-gray-50/80">
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
                      {row.quantityInStock <= 0 ? (
                        <span className="inline-flex rounded-full bg-red-50 px-2.5 py-0.5 text-xs font-medium text-red-700 ring-1 ring-inset ring-red-600/20">
                          0
                        </span>
                      ) : (
                        <span className="text-gray-700">{row.quantityInStock}</span>
                      )}
                    </td>
                    <td className="px-6 py-3.5 text-gray-700">
                      {row.suppliers.length > 0 ? row.suppliers.join(', ') : '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
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
