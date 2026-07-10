'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiArrowLeft, FiChevronDown, FiClock, FiDownload, FiRefreshCw } from 'react-icons/fi';
import ProductHistoryModal from '@/components/products/ProductHistoryModal';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { usePortalMenu } from '@/hooks/usePortalMenu';
import { fetchProductHistory } from '@/lib/api/products';
import { fetchSupplierInventory, updateSupplierInventoryPricing } from '@/lib/api/suppliers';
import { exportUnpaidSupplierInventoryToExcel } from '@/lib/suppliers/inventoryExport';
import { calcGrossUnitPrice, inventoryRowKey } from '@/lib/suppliers/inventoryPricing';
import {
  flattenInventoryGroups,
  formatInventoryProductTitle,
  groupInventoryRows,
  sumInventoryGroup,
  type InventoryGroupTotals,
  type InventoryProductGroup,
} from '@/lib/suppliers/inventoryTree';
import InventoryPricingCells from '@/components/suppliers/InventoryPricingCells';
import type { ProductHistory } from '@/types/product-history';
import type { SupplierInventoryRow } from '@/types/supplier-inventory';

type Props = {
  supplierId?: number | null;
  supplierName?: string;
  onBack: () => void;
};

type SortColumn = 'received' | 'paid' | 'stock' | 'sold' | 'unpaid';

function getUnpaidQuantity(row: SupplierInventoryRow): number {
  return Math.max(0, row.quantityToPay);
}

function getOverpaidQuantity(row: SupplierInventoryRow): number {
  return Math.max(0, -row.quantityToPay);
}

function getGroupSortValue(group: InventoryProductGroup, column: SortColumn): number {
  const totals = sumInventoryGroup(group.variants);
  switch (column) {
    case 'received':
      return totals.receivedQuantity;
    case 'paid':
      return totals.paidQuantity;
    case 'stock':
      return totals.quantityInStock;
    case 'sold':
      return totals.soldQuantity;
    case 'unpaid':
      return Math.max(0, totals.quantityToPay);
  }
}

function getRowHighlightClass(row: SupplierInventoryRow): string {
  if (row.quantityToPay < 0) {
    return 'bg-violet-50/80 hover:bg-violet-50';
  }
  if (row.quantityToPay > 0) {
    return 'bg-amber-50/80 hover:bg-amber-50';
  }
  return 'hover:bg-gray-50/80';
}

function getGroupHighlightClass(totals: InventoryGroupTotals): string {
  if (totals.quantityToPay < 0) {
    return 'bg-violet-50/80 hover:bg-violet-50';
  }
  if (totals.quantityToPay > 0) {
    return 'bg-amber-50/80 hover:bg-amber-50';
  }
  return 'hover:bg-gray-50/80';
}

export default function SupplierInventoryClient({ supplierId = null, supplierName, onBack }: Props) {
  const [rows, setRows] = useState<SupplierInventoryRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [salesSyncedAtUtc, setSalesSyncedAtUtc] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [selectedSupplierIds, setSelectedSupplierIds] = useState<Set<number>>(() => new Set());
  const [sort, setSort] = useState<{ column: SortColumn; direction: 'asc' | 'desc' } | null>(null);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [historyData, setHistoryData] = useState<ProductHistory | null>(null);
  const [historySubtitle, setHistorySubtitle] = useState<string | undefined>(undefined);
  const [exportNotice, setExportNotice] = useState<string | null>(null);
  const [collapsedProducts, setCollapsedProducts] = useState<Record<string, boolean>>({});
  const supplierFilterMenu = usePortalMenu({ menuWidth: 224 });

  const showSupplierColumn = !supplierId;

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

  const isSupplierFilterCustomized =
    showSupplierColumn &&
    supplierOptions.length > 0 &&
    selectedSupplierIds.size < supplierOptions.length;

  const loadInventory = useCallback((refresh = false) => {
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
  }, [supplierId]);

  useEffect(() => {
    void loadInventory();
  }, [loadInventory]);

  const handleRefreshSales = () => {
    setRefreshing(true);
    void loadInventory(true);
  };

  const handleExportExcel = () => {
    const sourceRows = supplierId
      ? rows
      : filteredRows.filter((row) => row.quantityToPay > 0);
    const result = exportUnpaidSupplierInventoryToExcel(sourceRows, {
      supplierName: supplierName?.trim() || undefined,
    });
    if (result.exported === 0) {
      setExportNotice('Няма неаплочаных прадалагаў для экспарту.');
      return;
    }
    setExportNotice(`Экспартавана радкоў: ${result.exported}.`);
  };

  useEffect(() => {
    if (!exportNotice) return undefined;
    const timer = window.setTimeout(() => setExportNotice(null), 4000);
    return () => window.clearTimeout(timer);
  }, [exportNotice]);

  const handleSavePricing = async (
    row: SupplierInventoryRow,
    values: { netUnitPrice: number; vatRatePercent: number }
  ) => {
    try {
      const updated = await updateSupplierInventoryPricing({
        supplierId: row.supplierId,
        shopifyProductId: row.shopifyProductId,
        shopifyVariantId: row.shopifyVariantId,
        netUnitPrice: values.netUnitPrice,
        vatRatePercent: values.vatRatePercent,
      });
      setRows((prev) =>
        prev.map((item) =>
          inventoryRowKey(item) === inventoryRowKey(row)
            ? {
                ...item,
                supplierPrice: updated.supplierPrice,
                vatRatePercent: updated.vatRatePercent,
                grossUnitPrice:
                  updated.grossUnitPrice ||
                  calcGrossUnitPrice(
                    updated.supplierPrice,
                    updated.vatRatePercent,
                    updated.supplierIsVatPayer
                  ),
                supplierIsVatPayer: updated.supplierIsVatPayer,
                hasPriceOverride: updated.hasPriceOverride,
              }
            : item
        )
      );
    } catch (err: unknown) {
      setExportNotice(err instanceof Error ? err.message : 'Памылка захавання цаны');
    }
  };

  const filteredRows = useMemo(() => {
    let filtered = rows;
    if (showSupplierColumn && supplierOptions.length > 0) {
      filtered = filtered.filter((row) => selectedSupplierIds.has(row.supplierId));
    }
    const q = searchQuery.trim().toLowerCase();
    if (q) {
      filtered = filtered.filter((row) => {
        const displayName = formatInventoryProductTitle(row);
        return (
          displayName.toLowerCase().includes(q) ||
          row.productName.toLowerCase().includes(q) ||
          row.productAuthor.toLowerCase().includes(q) ||
          row.variantTitle.toLowerCase().includes(q) ||
          row.supplierName.toLowerCase().includes(q)
        );
      });
    }
    return filtered;
  }, [rows, searchQuery, showSupplierColumn, supplierOptions.length, selectedSupplierIds]);

  const displayRows = useMemo(() => {
    const groups = groupInventoryRows(filteredRows, showSupplierColumn);
    const sortedGroups = [...groups].sort((a, b) => {
      if (sort) {
        const direction = sort.direction === 'asc' ? 1 : -1;
        return (getGroupSortValue(a, sort.column) - getGroupSortValue(b, sort.column)) * direction;
      }
      const byName = formatInventoryProductTitle(a.variants[0]).localeCompare(
        formatInventoryProductTitle(b.variants[0]),
        'be'
      );
      if (byName !== 0) return byName;
      return a.supplierName.localeCompare(b.supplierName, 'be');
    });
    return flattenInventoryGroups(sortedGroups, collapsedProducts);
  }, [filteredRows, showSupplierColumn, sort, collapsedProducts]);

  const visibleRows = displayRows;

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

  const closeHistory = () => {
    setHistoryOpen(false);
    setHistoryError(null);
    setHistoryData(null);
    setHistorySubtitle(undefined);
  };

  const openHistory = async (row: SupplierInventoryRow) => {
    setHistoryOpen(true);
    setHistoryLoading(true);
    setHistoryError(null);
    setHistoryData(null);
    setHistorySubtitle(undefined);

    try {
      const history = await fetchProductHistory(row.shopifyProductId);
      setHistoryData(history);
    } catch (err: unknown) {
      setHistoryError(err instanceof Error ? err.message : 'Памылка загрузкі гісторыі');
    } finally {
      setHistoryLoading(false);
    }
  };

  const toggleCollapsed = (groupKey: string) => {
    setCollapsedProducts((prev) => ({
      ...prev,
      [groupKey]: !prev[groupKey],
    }));
  };

  const columnCount = showSupplierColumn ? 11 : 10;

  const renderSortHeader = (column: SortColumn, label: string) => (
    <button
      type="button"
      onClick={() => handleSortClick(column)}
      className="inline-flex w-full items-center justify-end gap-1 rounded text-xs font-semibold uppercase tracking-wide text-gray-500 transition hover:text-gray-700"
      aria-label={`Сартаваць па ${label}`}
    >
      {label}
      {sort?.column === column && (
        <span aria-hidden>{sort.direction === 'asc' ? '↑' : '↓'}</span>
      )}
    </button>
  );

  const tableClassName = 'w-full table-fixed border-separate border-spacing-0 text-left text-sm';

  const renderTableColGroup = () => (
    <colgroup>
      {showSupplierColumn && <col className="w-[9%]" />}
      <col className={showSupplierColumn ? 'w-[18%]' : 'w-[20%]'} />
      <col className="w-[8%]" />
      <col className="w-[7%]" />
      <col className="w-[8%]" />
      <col className="w-[7%]" />
      <col className="w-[7%]" />
      <col className="w-[7%]" />
      <col className="w-[7%]" />
      <col className="w-[7%]" />
      <col className={showSupplierColumn ? 'w-[14%]' : 'w-[16%]'} />
    </colgroup>
  );

  const renderTableHeadRow = () => (
    <tr className="bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
      {showSupplierColumn && (
        <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5">
          <div className="flex items-center gap-2">
            <span>Пастаўшчык</span>
            <button
              type="button"
              ref={supplierFilterMenu.triggerRef}
              onClick={supplierFilterMenu.toggle}
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
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5">Тавар</th>
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5 text-right">Кошт нета адзінкі</th>
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5 text-right">ПДВ %</th>
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5 text-right">Кошт брута адзінкі</th>
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5 text-right">
        {renderSortHeader('received', 'Атрымана ўсяго')}
      </th>
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5 text-right">
        {renderSortHeader('paid', 'Аплочана ўсяго')}
      </th>
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5 text-right">
        {renderSortHeader('stock', 'У наяўнасці')}
      </th>
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5 text-right">
        {renderSortHeader('sold', 'Прадана')}
      </th>
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5 text-right">
        {renderSortHeader('unpaid', 'Не аплочана')}
      </th>
      <th className="border-b border-gray-200 bg-gray-50 px-4 py-2.5 text-right">Дзеі</th>
    </tr>
  );

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

      <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="sticky top-0 z-20 bg-white shadow-[0_1px_0_0_rgb(229,231,235)]">
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
              <div className="mt-2 flex flex-wrap gap-3 text-xs text-gray-500">
                <span className="inline-flex items-center gap-1.5">
                  <span className="size-3 rounded bg-amber-100 ring-1 ring-amber-300" aria-hidden />
                  прадана, не аплочана
                </span>
                <span className="inline-flex items-center gap-1.5">
                  <span className="size-3 rounded bg-violet-100 ring-1 ring-violet-300" aria-hidden />
                  пераплата
                </span>
              </div>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={handleExportExcel}
                className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                title="Экспарт неаплочаных прадалагаў у Excel"
              >
                <FiDownload className="size-4" aria-hidden />
                Excel
              </button>
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
            </div>
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
          {exportNotice && (
            <p className="mt-3 text-sm text-gray-600">{exportNotice}</p>
          )}
          </div>

          <table className={tableClassName}>
            {renderTableColGroup()}
            <thead>{renderTableHeadRow()}</thead>
          </table>
        </div>

        <table className={tableClassName}>
          {renderTableColGroup()}
          <tbody className="divide-y divide-gray-100">
              {visibleRows.map((displayRow) => {
                if (displayRow.type === 'parent') {
                  const { group, totals } = displayRow;
                  const unpaidQty = Math.max(0, totals.quantityToPay);
                  const overpaidQty = Math.max(0, -totals.quantityToPay);
                  const isCollapsed = Boolean(collapsedProducts[group.key]);

                  return (
                    <tr
                      key={`${group.key}::parent`}
                      className={getGroupHighlightClass(totals)}
                    >
                      {showSupplierColumn && (
                        <td className="px-4 py-3 font-medium text-gray-900">{group.supplierName || '—'}</td>
                      )}
                      <td className="px-4 py-3 text-gray-800">
                        <div className="flex items-start gap-2">
                          <button
                            type="button"
                            onClick={() => toggleCollapsed(group.key)}
                            className="mt-0.5 inline-flex size-5 shrink-0 items-center justify-center rounded border border-gray-200 bg-white text-xs text-gray-600 hover:bg-gray-50"
                            aria-label={isCollapsed ? 'Разгарнуць варыянты' : 'Згарнуць варыянты'}
                            title={isCollapsed ? 'Разгарнуць варыянты' : 'Згарнуць варыянты'}
                          >
                            <span aria-hidden>{isCollapsed ? '▸' : '▾'}</span>
                          </button>
                          <div className="flex min-w-0 flex-col gap-1">
                            <span className="font-medium">
                              {formatInventoryProductTitle(group.variants[0])}
                            </span>
                            {overpaidQty > 0 && (
                              <span className="inline-flex w-fit rounded-full bg-violet-100 px-2 py-0.5 text-[11px] font-medium text-violet-800 ring-1 ring-inset ring-violet-500/25">
                                пераплата {overpaidQty}
                              </span>
                            )}
                            {unpaidQty > 0 && (
                              <span className="inline-flex w-fit rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-medium text-amber-800 ring-1 ring-inset ring-amber-500/25">
                                не аплочана {unpaidQty}
                              </span>
                            )}
                          </div>
                        </div>
                      </td>
                      <td className="px-4 py-3 text-right text-sm text-gray-400">—</td>
                      <td className="px-4 py-3 text-right text-sm text-gray-400">—</td>
                      <td className="px-4 py-3 text-right text-sm text-gray-400">—</td>
                      <td className="px-4 py-3 text-right tabular-nums font-medium text-gray-800">
                        {totals.receivedQuantity}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums font-medium text-gray-800">
                        {totals.paidQuantity}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums font-medium text-gray-800">
                        {totals.quantityInStock}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums font-medium text-gray-800">
                        {totals.soldQuantity}
                      </td>
                      <td
                        className={`px-4 py-3 text-right tabular-nums font-medium ${
                          unpaidQty > 0 ? 'text-amber-800' : 'text-gray-800'
                        }`}
                      >
                        {unpaidQty}
                      </td>
                      <td className="px-4 py-3" />
                    </tr>
                  );
                }

                const row = displayRow.row;
                const unpaidQty = getUnpaidQuantity(row);
                const overpaidQty = getOverpaidQuantity(row);
                const variantLabel = row.variantTitle.trim() || '—';

                return (
                  <tr
                    key={`${row.supplierId}-${row.shopifyProductId}-${row.shopifyVariantId}`}
                    className={`${getRowHighlightClass(row)} ${displayRow.isVariantChild ? 'bg-gray-50/40' : ''}`}
                  >
                    {showSupplierColumn && (
                      <td className={`py-3 font-medium text-gray-900 ${displayRow.isVariantChild ? 'pl-10' : 'px-4'}`}>
                        {displayRow.isVariantChild ? '' : row.supplierName || '—'}
                      </td>
                    )}
                    <td className={`py-3 text-gray-800 ${displayRow.isVariantChild ? 'pl-10 pr-4' : 'px-4'}`}>
                      <div className={`flex flex-col gap-1 ${displayRow.isVariantChild ? 'ml-5' : ''}`}>
                        {displayRow.isVariantChild ? (
                          <div className="flex items-start gap-2">
                            <span className="mt-0.5 inline-flex items-center gap-1 text-gray-400" aria-hidden>
                              <span className="h-5 w-px bg-gray-300" />
                              <span className="w-5 border-t border-gray-300" />
                            </span>
                            <div className="min-w-0 space-y-1">
                              <span className="text-gray-700">{variantLabel}</span>
                              {overpaidQty > 0 && (
                                <span className="inline-flex w-fit rounded-full bg-violet-100 px-2 py-0.5 text-[11px] font-medium text-violet-800 ring-1 ring-inset ring-violet-500/25">
                                  пераплата {overpaidQty}
                                </span>
                              )}
                              {unpaidQty > 0 && (
                                <span className="inline-flex w-fit rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-medium text-amber-800 ring-1 ring-inset ring-amber-500/25">
                                  не аплочана {unpaidQty}
                                </span>
                              )}
                            </div>
                          </div>
                        ) : (
                          <>
                            <span>{formatInventoryProductTitle(row)}</span>
                            {overpaidQty > 0 && (
                              <span className="inline-flex w-fit rounded-full bg-violet-100 px-2 py-0.5 text-[11px] font-medium text-violet-800 ring-1 ring-inset ring-violet-500/25">
                                пераплата {overpaidQty}
                              </span>
                            )}
                            {unpaidQty > 0 && (
                              <span className="inline-flex w-fit rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-medium text-amber-800 ring-1 ring-inset ring-amber-500/25">
                                не аплочана {unpaidQty}
                              </span>
                            )}
                          </>
                        )}
                      </div>
                    </td>
                    <InventoryPricingCells row={row} onSave={handleSavePricing} />
                    <td className="px-4 py-3 text-right tabular-nums text-gray-700">{row.receivedQuantity}</td>
                    <td className="px-4 py-3 text-right tabular-nums text-gray-700">{row.paidQuantity}</td>
                    <td className="px-4 py-3 text-right tabular-nums text-gray-700">{row.quantityInStock}</td>
                    <td className="px-4 py-3 text-right tabular-nums text-gray-700">{row.soldQuantity}</td>
                    <td
                      className={`px-4 py-3 text-right tabular-nums font-medium ${
                        unpaidQty > 0 ? 'text-amber-800' : 'text-gray-700'
                      }`}
                    >
                      {unpaidQty}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <button
                        type="button"
                        onClick={() => {
                          void openHistory(row);
                        }}
                        className="inline-flex items-center gap-1.5 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-600 transition hover:border-primary/30 hover:bg-primary/5 hover:text-primary"
                        aria-label={`Гісторыя: ${displayRow.isVariantChild ? variantLabel : formatInventoryProductTitle(row)}`}
                        title="Гісторыя прадукту"
                      >
                        <FiClock className="size-3.5" aria-hidden />
                        Гісторыя
                      </button>
                    </td>
                  </tr>
                );
              })}
              {visibleRows.length === 0 && (
                <tr>
                  <td
                    colSpan={columnCount}
                    className="px-4 py-8 text-center text-sm text-gray-500"
                  >
                    {rows.length === 0 ? 'Няма даных для інвентарызацыі.' : 'Нічога не знойдзена.'}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
      </div>
      {supplierFilterMenu.mounted &&
        supplierFilterMenu.open &&
        createPortal(
          <div
            ref={supplierFilterMenu.menuRef}
            className="fixed z-[70] w-56 rounded-lg border border-gray-200 bg-white p-2 shadow-lg"
            style={{
              top: `${supplierFilterMenu.position.top}px`,
              left: `${supplierFilterMenu.position.left}px`,
            }}
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
      <ProductHistoryModal
        open={historyOpen}
        loading={historyLoading}
        error={historyError}
        history={historyData}
        subtitle={historySubtitle}
        onClose={closeHistory}
      />
    </div>
  );
}
