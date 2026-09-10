'use client';

import LoadingSpinner from '@/components/ui/LoadingSpinner';
import {
  fetchBukinistkaInventory,
  type BukinistkaInventoryRow,
} from '@/lib/api/bukinistka-inventory';
import { useEffect, useMemo, useState } from 'react';
import {
  FiChevronDown,
  FiChevronUp,
  FiExternalLink,
  FiSearch,
} from 'react-icons/fi';

type SortKey =
  | 'productName'
  | 'acceptedQty'
  | 'quantityInStock'
  | 'soldQty'
  | 'paidQty'
  | 'quantityToPay';
type SortDir = 'asc' | 'desc';

function formatQty(value: number): string {
  if (!Number.isFinite(value)) return '0';
  return Number.isInteger(value)
    ? String(value)
    : value.toLocaleString('be-BY', {
        minimumFractionDigits: 0,
        maximumFractionDigits: 2,
      });
}

function SortableHeader({
  label,
  column,
  sortKey,
  sortDir,
  align = 'right',
  onSort,
}: {
  label: string;
  column: SortKey;
  sortKey: SortKey;
  sortDir: SortDir;
  align?: 'left' | 'right';
  onSort: (column: SortKey) => void;
}) {
  const active = sortKey === column;
  return (
    <th
      className={`px-4 py-3 ${align === 'right' ? 'text-right' : 'text-left'}`}
    >
      <button
        type="button"
        onClick={() => onSort(column)}
        className={`inline-flex items-center gap-1 uppercase tracking-wide transition ${
          align === 'right' ? 'flex-row-reverse' : ''
        } ${active ? 'text-amber-800' : 'text-gray-500 hover:text-gray-800'}`}
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

export default function BukinistkaInventoryClient() {
  const [rows, setRows] = useState<BukinistkaInventoryRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [sortKey, setSortKey] = useState<SortKey>('productName');
  const [sortDir, setSortDir] = useState<SortDir>('asc');

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchBukinistkaInventory()
      .then((data) => {
        if (!cancelled) setRows(data);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(
            err instanceof Error
              ? err.message
              : 'Не ўдалося загрузіць інвентарызацыю.'
          );
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const handleSort = (column: SortKey) => {
    if (sortKey === column) {
      setSortDir((prev) => (prev === 'asc' ? 'desc' : 'asc'));
      return;
    }
    setSortKey(column);
    setSortDir(column === 'productName' ? 'asc' : 'desc');
  };

  const filtered = useMemo(() => {
    const search = searchQuery.trim().toLowerCase();
    const list = search
      ? rows.filter((row) => row.productName.toLowerCase().includes(search))
      : [...rows];

    const dir = sortDir === 'asc' ? 1 : -1;
    list.sort((a, b) => {
      if (sortKey === 'productName') {
        return (
          a.productName.localeCompare(b.productName, 'be', {
            sensitivity: 'base',
          }) * dir
        );
      }
      return (a[sortKey] - b[sortKey]) * dir;
    });
    return list;
  }, [rows, searchQuery, sortKey, sortDir]);

  if (loading) {
    return (
      <div className="flex justify-center py-16">
        <LoadingSpinner />
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {error ? (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
          {error}
        </p>
      ) : null}

      <div className="relative max-w-md">
        <FiSearch
          className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-gray-400"
          aria-hidden
        />
        <input
          type="search"
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          placeholder="Пошук па назве…"
          className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-3 text-sm text-gray-900 outline-none ring-amber-500/30 focus:border-amber-500 focus:ring-2"
        />
      </div>

      {filtered.length === 0 ? (
        <p className="rounded-xl border border-dashed border-gray-200 bg-white px-4 py-10 text-center text-sm text-gray-500">
          Пакуль няма прынятых тавараў ад Кірмаша.
        </p>
      ) : (
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-100 text-sm">
              <thead className="bg-gray-50/90 text-xs font-semibold uppercase tracking-wide text-gray-500">
                <tr>
                  <th className="px-4 py-3 text-left">Фота</th>
                  <SortableHeader
                    label="Назва"
                    column="productName"
                    sortKey={sortKey}
                    sortDir={sortDir}
                    align="left"
                    onSort={handleSort}
                  />
                  <SortableHeader
                    label="Прынята ўсяго"
                    column="acceptedQty"
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
                  <SortableHeader
                    label="Прададзена"
                    column="soldQty"
                    sortKey={sortKey}
                    sortDir={sortDir}
                    onSort={handleSort}
                  />
                  <SortableHeader
                    label="Аплачана"
                    column="paidQty"
                    sortKey={sortKey}
                    sortDir={sortDir}
                    onSort={handleSort}
                  />
                  <SortableHeader
                    label="Да аплаты"
                    column="quantityToPay"
                    sortKey={sortKey}
                    sortDir={sortDir}
                    onSort={handleSort}
                  />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {filtered.map((row) => (
                  <tr key={row.odooProductId} className="hover:bg-gray-50/80">
                    <td className="px-4 py-3">
                      {row.mainImageUrl ? (
                        // eslint-disable-next-line @next/next/no-img-element
                        <img
                          src={row.mainImageUrl}
                          alt=""
                          className="size-12 rounded-md object-cover"
                        />
                      ) : (
                        <div className="flex size-12 items-center justify-center rounded-md bg-gray-100 text-xs text-gray-400">
                          —
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      {row.odooUrl ? (
                        <a
                          href={row.odooUrl}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex max-w-md items-start gap-1.5 font-medium text-amber-900 hover:underline"
                        >
                          <span className="min-w-0">{row.productName}</span>
                          <FiExternalLink
                            className="mt-0.5 size-3.5 shrink-0 opacity-70"
                            aria-hidden
                          />
                        </a>
                      ) : (
                        <span className="font-medium text-gray-900">
                          {row.productName}
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right tabular-nums text-gray-800">
                      {formatQty(row.acceptedQty)}
                    </td>
                    <td className="px-4 py-3 text-right tabular-nums text-gray-800">
                      {formatQty(row.quantityInStock)}
                    </td>
                    <td className="px-4 py-3 text-right tabular-nums text-gray-800">
                      {formatQty(row.soldQty)}
                    </td>
                    <td className="px-4 py-3 text-right tabular-nums text-gray-800">
                      {formatQty(row.paidQty)}
                    </td>
                    <td className="px-4 py-3 text-right tabular-nums font-medium text-gray-900">
                      {formatQty(row.quantityToPay)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}
