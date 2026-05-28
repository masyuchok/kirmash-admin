'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { FiPlus } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { deleteSupply, fetchSupplies } from '@/lib/api/supplies';
import { fetchSupplierOptions } from '@/lib/api/suppliers';
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
  const [deleteModalOpen, setDeleteModalOpen] = useState(false);
  const [deleteModalStep, setDeleteModalStep] = useState<1 | 2>(1);
  const [supplyPendingDelete, setSupplyPendingDelete] = useState<SupplyListItem | null>(null);
  const [deleteSubmitting, setDeleteSubmitting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const refreshSupplies = useCallback(async () => {
    const data = await fetchSupplies();
    setRows(data);
  }, []);

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
    refreshSupplies()
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
  }, [refreshSupplies]);

  useEffect(() => {
    if (!createOpen) return;
    let cancelled = false;
    setSupplierLoading(true);
    setSupplierError(null);
    fetchSupplierOptions()
      .then((options) => {
        if (cancelled) return;
        setSupplierOptions(options.map(({ id, name }) => ({ id, name })));
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
      supplierId: String(supply.supplierId),
      supplierName: supply.supplierName,
      date: supply.date,
    });
    router.push(`/supplies/${supply.id}?${query.toString()}`);
  };

  const closeDeleteModal = () => {
    setDeleteModalOpen(false);
    setDeleteModalStep(1);
    setSupplyPendingDelete(null);
    setDeleteError(null);
    setDeleteSubmitting(false);
  };

  const requestDeleteSupply = (supply: SupplyListItem) => {
    setSupplyPendingDelete(supply);
    setDeleteModalStep(1);
    setDeleteError(null);
    setDeleteModalOpen(true);
  };

  const confirmDeleteSupply = async () => {
    if (!supplyPendingDelete) return;
    const idNum = Number(supplyPendingDelete.id);
    if (!Number.isFinite(idNum) || idNum <= 0) {
      setDeleteError('Некарэктны ідэнтыфікатар пастаўкі.');
      return;
    }
    setDeleteSubmitting(true);
    setDeleteError(null);
    try {
      await deleteSupply(idNum);
      setRows((prev) => prev.filter((r) => r.id !== supplyPendingDelete.id));
      closeDeleteModal();
    } catch (err: unknown) {
      setDeleteError(err instanceof Error ? err.message : 'Памылка выдалення пастаўкі');
    } finally {
      setDeleteSubmitting(false);
    }
  };

  if (loading) {
    return <LoadingSpinner label="Загрузка паставак..." />;
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
          onRequestDelete={requestDeleteSupply}
          onToggleDateSort={() =>
            setDateSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
          }
        />
      </div>

      {deleteModalOpen && supplyPendingDelete && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
          role="dialog"
          aria-modal="true"
          aria-labelledby="delete-list-supply-title"
        >
          <div className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-6 shadow-xl">
            <h2 id="delete-list-supply-title" className="text-lg font-semibold text-gray-900">
              {deleteModalStep === 1 ? 'Выдаліць пастаўку?' : 'Апошняе пацвярджэнне'}
            </h2>
            <p className="mt-3 text-sm text-gray-700">
              {deleteModalStep === 1 ? (
                <>
                  Пастаўшчык: <span className="font-medium">{supplyPendingDelete.supplierName}</span>
                  <br />
                  Дата:{' '}
                  <span className="font-medium">
                    {supplyPendingDelete.date || '—'}
                  </span>
                  <span className="mt-2 block text-gray-600">
                    Пасля выдалення вярнуць пастаўку будзе нельга.
                  </span>
                </>
              ) : (
                'Вы сапраўды хочаце незваротна выдаліць гэтую пастаўку з табліцы?'
              )}
            </p>
            {deleteError && <p className="mt-3 text-sm text-red-600">{deleteError}</p>}
            <div className="mt-6 flex flex-wrap justify-end gap-3">
              <button
                type="button"
                onClick={closeDeleteModal}
                disabled={deleteSubmitting}
                className={btnSecondary}
              >
                Адмена
              </button>
              {deleteModalStep === 1 ? (
                <button
                  type="button"
                  onClick={() => setDeleteModalStep(2)}
                  disabled={deleteSubmitting}
                  className="rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-700 shadow-sm transition hover:bg-red-100 disabled:opacity-60"
                >
                  Працягнуць
                </button>
              ) : (
                <button
                  type="button"
                  onClick={() => {
                    void confirmDeleteSupply();
                  }}
                  disabled={deleteSubmitting}
                  className="rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-700 shadow-sm transition hover:bg-red-100 disabled:opacity-60"
                >
                  {deleteSubmitting ? 'Выдаляю...' : 'Выдаліць назаўжды'}
                </button>
              )}
            </div>
          </div>
        </div>
      )}

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
              {supplierLoading && (
                <div className="flex items-center gap-2 text-sm text-gray-500">
                  <span className="size-4 animate-spin rounded-full border-2 border-primary/25 border-t-primary" />
                  Загрузка пастаўшчыкоў...
                </div>
              )}
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
