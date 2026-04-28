'use client';

import { useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { FiPlus } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { fetchSupplies } from '@/lib/api/supplies';
import { apiCredentials, getApiBaseUrl } from '@/lib/api/common';
import type { SupplyListItem } from '@/types/supply';
import SuppliesTable from './SuppliesTable';

type SupplierOption = {
  id: number;
  name: string;
};

const inputClass =
  'w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25';
const btnPrimary =
  'rounded-lg bg-primary px-5 py-2.5 text-sm font-medium text-white shadow-sm transition hover:bg-primary-hover focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary disabled:pointer-events-none disabled:opacity-50';
const btnSecondary =
  'rounded-lg border border-gray-200 bg-white px-5 py-2.5 text-sm font-medium text-gray-700 shadow-sm transition hover:bg-gray-50';

export default function SuppliesClient() {
  const router = useRouter();
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [rows, setRows] = useState<SupplyListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const [supplierOptions, setSupplierOptions] = useState<SupplierOption[]>([]);
  const [supplierId, setSupplierId] = useState('');
  const [supplyDate, setSupplyDate] = useState('');
  const [supplierLoading, setSupplierLoading] = useState(false);
  const [supplierError, setSupplierError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [dateSortDirection, setDateSortDirection] = useState<'asc' | 'desc'>('desc');
  const [selectedSuppliers, setSelectedSuppliers] = useState<string[]>([]);

  useEffect(() => {
    setTopbarPage({
      title: 'Пастаўкі',
      subtitle: loading ? 'Загрузка…' : `Спіс паставак (${rows.length})`,
    });
    setTopbarButtons([
      {
        label: 'Новая пастаўка',
        icon: <FiPlus />,
        onClick: () => {
          setCreateOpen(true);
          setFormError(null);
        },
        variant: 'primary',
      },
    ]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [loading, rows.length, setTopbarButtons, setTopbarPage]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchSupplies()
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

  useEffect(() => {
    if (!createOpen) return;
    let cancelled = false;
    setSupplierLoading(true);
    setSupplierError(null);
    fetch(`${getApiBaseUrl()}/suppliers`, { credentials: apiCredentials })
      .then((res) => res.json())
      .then((data: unknown) => {
        if (cancelled) return;
        if (!Array.isArray(data)) {
          setSupplierOptions([]);
          return;
        }
        const options = data
          .map((row) => {
            const r = row as Record<string, unknown>;
            const id = typeof r.id === 'number' ? r.id : Number(r.id);
            const name = typeof r.name === 'string' ? r.name : '';
            if (!Number.isFinite(id) || !name.trim()) return null;
            return { id, name };
          })
          .filter((row): row is SupplierOption => row !== null);
        setSupplierOptions(options);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setSupplierError(err instanceof Error ? err.message : 'Памылка загрузкі пастаўшчыкоў');
        }
      })
      .finally(() => {
        if (!cancelled) setSupplierLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [createOpen]);

  const closeModal = () => {
    setCreateOpen(false);
    setFormError(null);
  };

  const goToCreate = () => {
    if (!supplierId || !supplyDate) {
      setFormError('Выберыце пастаўшчыка і дату пастаўкі.');
      return;
    }
    const query = new URLSearchParams({ supplierId, date: supplyDate });
    setCreateOpen(false);
    router.push(`/supplies/new?${query.toString()}`);
  };

  const supplierFilters = useMemo(() => {
    return Array.from(new Set(rows.map((row) => row.supplierName))).sort((a, b) =>
      a.localeCompare(b, 'be')
    );
  }, [rows]);

  const visibleRows = useMemo(() => {
    const bySupplier =
      selectedSuppliers.length === 0
        ? rows
        : rows.filter((row) => selectedSuppliers.includes(row.supplierName));

    const rowsCopy = [...bySupplier];
    rowsCopy.sort((a, b) => {
      const aTime = new Date(a.date).getTime();
      const bTime = new Date(b.date).getTime();
      const aValid = Number.isFinite(aTime);
      const bValid = Number.isFinite(bTime);

      if (!aValid && !bValid) return 0;
      if (!aValid) return 1;
      if (!bValid) return -1;

      return dateSortDirection === 'asc' ? aTime - bTime : bTime - aTime;
    });
    return rowsCopy;
  }, [rows, selectedSuppliers, dateSortDirection]);

  const toggleSupplierFilter = (name: string) => {
    setSelectedSuppliers((prev) =>
      prev.includes(name) ? prev.filter((item) => item !== name) : [...prev, name]
    );
  };

  const openSupply = (supply: SupplyListItem) => {
    const query = new URLSearchParams({
      supplierName: supply.supplierName,
      date: supply.date,
    });
    router.push(`/supplies/${supply.id}?${query.toString()}`);
  };

  if (loading) {
    return (
      <div className="mx-auto w-full max-w-6xl space-y-6">
        <div className="space-y-2">
          <div className="h-8 w-48 animate-pulse rounded-lg bg-gray-200/80" />
          <div className="h-4 w-64 animate-pulse rounded-md bg-gray-100" />
        </div>
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="divide-y divide-gray-100 p-4">
            {Array.from({ length: 5 }).map((_, i) => (
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

      <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
        <SuppliesTable
          supplies={visibleRows}
          sortDirection={dateSortDirection}
          supplierFilters={supplierFilters}
          selectedSuppliers={selectedSuppliers}
          onToggleSupplierFilter={toggleSupplierFilter}
          onOpenSupply={openSupply}
          onToggleDateSort={() =>
            setDateSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
          }
        />
      </div>

      {createOpen && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
          role="dialog"
          aria-modal="true"
          aria-labelledby="new-supply-title"
        >
          <div className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-6 shadow-xl">
            <h2 id="new-supply-title" className="text-lg font-semibold text-gray-900">
              Новая пастаўка
            </h2>
            <div className="mt-4 space-y-4">
              <div>
                <label htmlFor="supplierId" className="mb-1 block text-sm font-medium text-gray-700">
                  Пастаўшчык*
                </label>
                <select
                  id="supplierId"
                  value={supplierId}
                  onChange={(e) => setSupplierId(e.currentTarget.value)}
                  className={inputClass}
                  disabled={supplierLoading}
                >
                  <option value="">Выберыце пастаўшчыка</option>
                  {supplierOptions.map((row) => (
                    <option key={row.id} value={String(row.id)}>
                      {row.name}
                    </option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="supplyDate" className="mb-1 block text-sm font-medium text-gray-700">
                  Дата пастаўкі*
                </label>
                <input
                  id="supplyDate"
                  type="date"
                  value={supplyDate}
                  onChange={(e) => setSupplyDate(e.currentTarget.value)}
                  className={inputClass}
                />
              </div>
              {supplierLoading && <p className="text-sm text-gray-500">Загрузка пастаўшчыкоў...</p>}
              {supplierError && <p className="text-sm text-red-600">{supplierError}</p>}
              {!supplierLoading && !supplierError && supplierOptions.length === 0 && (
                <p className="text-sm text-gray-500">Спіс пастаўшчыкоў пакуль пусты.</p>
              )}
              {formError && <p className="text-sm text-red-600">{formError}</p>}
            </div>
            <div className="mt-6 flex flex-wrap justify-end gap-3">
              <button type="button" onClick={closeModal} className={btnSecondary}>
                Cancel
              </button>
              <button
                type="button"
                onClick={goToCreate}
                className={btnPrimary}
                disabled={supplierLoading || supplierOptions.length === 0}
              >
                OK
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
