'use client';

import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { useRouter } from 'next/navigation';
import { FiExternalLink, FiSearch, FiX } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { fetchProductsWithSuppliers } from '@/lib/api/products';
import type { ProductWithSuppliers } from '@/types/product';

type Props = {
  supplyId?: string;
  supplierId?: string;
  supplierName?: string;
  date?: string;
  selectedProductIds?: string[];
};

export default function SupplyProductPickerClient({
  supplyId = '',
  supplierId = '',
  supplierName = '',
  date = '',
  selectedProductIds = [],
}: Props) {
  const pageSize = 50;
  const router = useRouter();
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [rows, setRows] = useState<ProductWithSuppliers[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedTypes, setSelectedTypes] = useState<string[]>([]);
  const [selectedIds, setSelectedIds] = useState<string[]>(selectedProductIds);
  const [page, setPage] = useState(1);
  const [typeMenuOpen, setTypeMenuOpen] = useState(false);
  const [menuMounted, setMenuMounted] = useState(false);
  const [typeMenuPosition, setTypeMenuPosition] = useState({ top: 0, left: 0 });
  const [typeTriggerEl, setTypeTriggerEl] = useState<HTMLButtonElement | null>(null);
  const [typeMenuEl, setTypeMenuEl] = useState<HTMLDivElement | null>(null);

  useEffect(() => {
    setTopbarPage({ title: 'Выбар тавару для пастаўкі' });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [setTopbarButtons, setTopbarPage]);

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
          setError(err instanceof Error ? err.message : 'Памылка загрузкі прадуктаў');
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const typeOptions = useMemo(
    () =>
      Array.from(new Set(rows.map((r) => r.productType).filter((t) => t.trim().length > 0))).sort((a, b) =>
        a.localeCompare(b, 'be')
      ),
    [rows]
  );

  const visibleRows = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    const byType =
      selectedTypes.length === 0 ? rows : rows.filter((r) => selectedTypes.includes(r.productType));
    return q ? byType.filter((r) => r.productName.toLowerCase().includes(q)) : byType;
  }, [rows, selectedTypes, searchQuery]);

  const totalPages = Math.max(1, Math.ceil(visibleRows.length / pageSize));
  const pagedRows = useMemo(() => {
    const safePage = Math.min(page, totalPages);
    const start = (safePage - 1) * pageSize;
    return visibleRows.slice(start, start + pageSize);
  }, [visibleRows, page, totalPages]);

  useEffect(() => {
    setPage(1);
  }, [searchQuery, selectedTypes]);

  const toggleType = (type: string) => {
    setSelectedTypes((prev) => (prev.includes(type) ? prev.filter((x) => x !== type) : [...prev, type]));
  };

  const updateTypeMenuPosition = () => {
    if (!typeTriggerEl) return;
    const rect = typeTriggerEl.getBoundingClientRect();
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
    if (!typeMenuOpen) return;
    updateTypeMenuPosition();

    const onDocClick = (event: MouseEvent) => {
      const target = event.target as Node;
      const clickedMenu = typeMenuEl?.contains(target);
      const clickedTrigger = typeTriggerEl?.contains(target);
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
  }, [typeMenuOpen, typeTriggerEl, typeMenuEl]);

  const toggleProduct = (id: string) => {
    setSelectedIds((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]));
  };

  const returnToSupply = () => {
    const query = new URLSearchParams();
    if (date) query.set('date', date);
    if (supplierId) query.set('supplierId', supplierId);
    if (supplierName) query.set('supplierName', supplierName);
    if (selectedIds.length > 0) query.set('selectedProductIds', selectedIds.join(','));
    const target = supplyId ? `/supplies/${supplyId}` : '/supplies/new';
    router.push(`${target}?${query.toString()}`);
  };

  const openShopifyCreate = () => {
    const from = rows.find((r) => r.productAdminUrl)?.productAdminUrl;
    if (!from) return;
    const url = new URL(from);
    const storePath = url.pathname.split('/products/')[0];
    const createUrl = `${url.origin}${storePath}/products/new`;
    window.open(createUrl, '_blank', 'noopener,noreferrer');
  };

  return (
    <div className="mx-auto w-full max-w-6xl space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <button
          type="button"
          onClick={openShopifyCreate}
          className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white shadow-sm transition hover:bg-primary-hover"
        >
          Дадаць новы тавар
        </button>
        <button
          type="button"
          onClick={returnToSupply}
          className="rounded-lg border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition hover:bg-gray-50"
        >
          Дадаць выбраныя ({selectedIds.length})
        </button>
      </div>

      {error && <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">{error}</div>}

      <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 bg-gray-50/50 px-6 py-4">
          <div className="flex flex-wrap items-center gap-3">
            <div className="flex w-full max-w-xl items-center gap-2 rounded-xl border border-gray-200 bg-white px-3 py-2.5 shadow-sm focus-within:border-primary focus-within:ring-2 focus-within:ring-primary/20">
              <FiSearch className="size-4 shrink-0 text-gray-400" />
              <input
                type="text"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.currentTarget.value)}
                placeholder="Пошук па назве..."
                className="w-full border-0 bg-transparent text-sm text-gray-900 placeholder:text-gray-400 focus:outline-none"
              />
              {searchQuery.trim() && (
                <button type="button" onClick={() => setSearchQuery('')} className="text-gray-400 hover:text-gray-600">
                  <FiX className="size-4" />
                </button>
              )}
            </div>
            <div className="inline-flex items-center gap-1 rounded-xl border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 shadow-sm">
              <span className="font-medium">Тып</span>
              <button
                type="button"
                ref={setTypeTriggerEl}
                className="inline-flex items-center rounded p-0.5 text-gray-500 transition hover:bg-gray-100 hover:text-gray-700"
                aria-label="Фільтр па тыпе прадукту"
                onClick={() => setTypeMenuOpen((prev) => !prev)}
              >
                <span aria-hidden>{typeMenuOpen ? '▴' : '▾'}</span>
              </button>
            </div>
          </div>
        </div>

        {loading ? (
          <div className="p-4">
            {Array.from({ length: 6 }).map((_, i) => (
              <div key={i} className="mb-2 h-12 animate-pulse rounded-md bg-gray-50" />
            ))}
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50/90 text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="px-4 py-3.5"></th>
                  <th className="px-6 py-3.5">Назва</th>
                  <th className="px-6 py-3.5 text-right">У наяўнасці</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100 bg-white">
                {pagedRows.map((row) => (
                  <tr key={row.shopifyProductId} className="transition hover:bg-gray-50/80">
                    <td className="px-4 py-3.5">
                      <input
                        type="checkbox"
                        className="size-4 rounded border-gray-300 accent-primary focus:ring-primary"
                        checked={selectedIds.includes(row.shopifyProductId)}
                        onChange={() => toggleProduct(row.shopifyProductId)}
                      />
                    </td>
                    <td className="px-6 py-3.5 font-medium text-gray-900">
                      <div className="flex items-center gap-3">
                        {row.mainImageUrl ? (
                          <a href={row.mainImageUrl} target="_blank" rel="noopener noreferrer">
                            <img src={row.mainImageUrl} alt={row.productName} className="size-8 rounded-md border border-gray-200 object-cover" />
                          </a>
                        ) : (
                          <div className="size-8 rounded-md border border-gray-200 bg-gray-100" />
                        )}
                        <a href={row.productAdminUrl} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 hover:underline">
                          {row.productName}
                          <FiExternalLink className="size-3.5 text-gray-500" />
                        </a>
                      </div>
                    </td>
                    <td className="px-6 py-3.5 text-right tabular-nums text-gray-700">{row.quantityInStock}</td>
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
        typeMenuOpen &&
        createPortal(
          <div
            ref={setTypeMenuEl}
            className="fixed z-[70] w-64 rounded-lg border border-gray-200 bg-white p-3 shadow-lg"
            style={{ top: `${typeMenuPosition.top}px`, left: `${typeMenuPosition.left}px` }}
          >
            <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-500">
              Фільтр па тыпе
            </p>
            <div className="max-h-56 space-y-2 overflow-auto pr-1">
              {typeOptions.length === 0 ? (
                <p className="text-xs text-gray-500">Няма тыпаў</p>
              ) : (
                typeOptions.map((type) => (
                  <label
                    key={type}
                    className="flex items-center gap-2 text-sm font-normal normal-case text-gray-700"
                  >
                    <input
                      type="checkbox"
                      className="size-4 rounded border-gray-300 accent-primary focus:ring-primary"
                      checked={selectedTypes.includes(type)}
                      onChange={() => toggleType(type)}
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
