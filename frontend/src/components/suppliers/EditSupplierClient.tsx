'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { FiArrowLeft } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { fetchSupplierById, updateSupplier } from '@/lib/api/suppliers';
import SupplierFormFields from './SupplierFormFields';
import {
  serializeSupplierForm,
  type SupplierFormValues,
} from '@/lib/suppliers/supplierFormTypes';

const btnPrimary =
  'rounded-lg bg-blue-600 px-5 py-2.5 text-sm font-medium text-white shadow-sm transition hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50 disabled:pointer-events-none';
const btnSecondary =
  'rounded-lg border border-gray-200 bg-white px-5 py-2.5 text-sm font-medium text-gray-700 shadow-sm transition hover:bg-gray-50';

type Props = {
  supplierId: number;
};

export default function EditSupplierClient({ supplierId }: Props) {
  const router = useRouter();
  const { setTopbarButtons } = useTopbar();
  const [form, setForm] = useState<SupplierFormValues | null>(null);
  const [initialSnapshot, setInitialSnapshot] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);

  const isDirty = useMemo(() => {
    if (!form || initialSnapshot === null) return false;
    return serializeSupplierForm(form) !== initialSnapshot;
  }, [form, initialSnapshot]);

  useEffect(() => {
    setTopbarButtons([
      {
        label: 'Да спісу',
        icon: <FiArrowLeft />,
        onClick: () => router.push('/suppliers'),
      },
    ]);
    return () => setTopbarButtons([]);
  }, [router, setTopbarButtons]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setLoadError(null);
    fetchSupplierById(supplierId)
      .then((values) => {
        if (cancelled) return;
        setForm(values);
        setInitialSnapshot(serializeSupplierForm(values));
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setLoadError(err instanceof Error ? err.message : 'Памылка загрузкі');
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [supplierId]);

  const handleSaveClick = () => {
    setSaveError(null);
    if (!isDirty || !form) return;
    setConfirmOpen(true);
  };

  const handleConfirmSave = async () => {
    if (!form) return;
    setSaving(true);
    setSaveError(null);
    try {
      await updateSupplier(supplierId, form);
      setConfirmOpen(false);
      router.push('/suppliers');
    } catch (err: unknown) {
      setSaveError(err instanceof Error ? err.message : 'Памылка захавання');
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="mx-auto w-full max-w-3xl space-y-4 rounded-xl border border-gray-200 bg-white p-10 text-center shadow-sm">
        <p className="text-sm text-gray-500">Загрузка...</p>
      </div>
    );
  }

  if (loadError || !form) {
    return (
      <div className="mx-auto w-full max-w-3xl space-y-4 rounded-xl border border-gray-200 bg-white p-10 text-center shadow-sm">
        <p className="text-sm text-red-600">{loadError ?? 'Даныя недаступныя'}</p>
        <Link href="/suppliers" className="text-sm font-medium text-blue-600 hover:underline">
          Вярнуцца да спісу
        </Link>
      </div>
    );
  }

  return (
    <>
      <div className="mx-auto w-full max-w-3xl overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 px-6 py-5">
          <h1 className="text-xl font-semibold tracking-tight text-gray-900">Рэдагаваць пастаўшчыка</h1>
          <p className="mt-1 text-sm text-gray-500">ID: {supplierId}</p>
        </div>
        <div className="space-y-4 px-6 py-6">
          <SupplierFormFields values={form} onChange={setForm} />
          {saveError && <p className="text-sm text-red-600">{saveError}</p>}
          <div className="flex flex-wrap gap-3 pt-2">
            <button
              type="button"
              disabled={!isDirty || saving}
              className={btnPrimary}
              onClick={handleSaveClick}
            >
              Захаваць змены
            </button>
            <Link href="/suppliers" className={`inline-flex items-center justify-center ${btnSecondary}`}>
              Адмена
            </Link>
          </div>
        </div>
      </div>

      {confirmOpen && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
          role="dialog"
          aria-modal="true"
          aria-labelledby="save-confirm-title"
        >
          <div className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-6 shadow-xl">
            <h2 id="save-confirm-title" className="text-lg font-semibold text-gray-900">
              Пацверджанне
            </h2>
            <p className="mt-2 text-sm text-gray-600">
              Ці ўпэўнены, што хочаце захаваць змены?
            </p>
            <div className="mt-6 flex flex-wrap justify-end gap-3">
              <button
                type="button"
                className={btnSecondary}
                disabled={saving}
                onClick={() => setConfirmOpen(false)}
              >
                Адмена
              </button>
              <button
                type="button"
                className={btnPrimary}
                disabled={saving}
                onClick={handleConfirmSave}
              >
                {saving ? 'Захоўваю...' : 'Захаваць'}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
