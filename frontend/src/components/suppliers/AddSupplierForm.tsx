'use client';
import { useState } from 'react';
import { createSupplier } from '@/lib/api/suppliers';
import SupplierFormFields from './SupplierFormFields';
import {
  defaultEmptySupplierForm,
  type SupplierFormValues,
} from '@/lib/suppliers/supplierFormTypes';

type Props = {
  onSuccess?: (createdId: number) => void;
  onCancel?: () => void;
};

const btnPrimary =
  'rounded-lg bg-blue-600 px-5 py-2.5 text-sm font-medium text-white shadow-sm transition hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50';
const btnSecondary =
  'rounded-lg border border-gray-200 bg-white px-5 py-2.5 text-sm font-medium text-gray-700 shadow-sm transition hover:bg-gray-50';

const AddSupplierForm = ({ onSuccess, onCancel }: Props) => {
  const [form, setForm] = useState<SupplierFormValues>(() => defaultEmptySupplierForm());
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>('');

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      const data = await createSupplier(form);
      const createdId = typeof data?.id === 'number' ? data.id : -1;
      onSuccess?.(createdId);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Невядомая памылка');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="mx-auto w-full max-w-3xl overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
      <div className="border-b border-gray-100 px-6 py-5">
        <h2 className="text-xl font-semibold tracking-tight text-gray-900">Дадаць пастаўшчыка</h2>
      </div>
      <form onSubmit={submit} className="space-y-4 px-6 py-6">
        <SupplierFormFields values={form} onChange={setForm} showDuplicateNameHint />
        {error && <p className="text-sm text-red-600">{error}</p>}
        <div className="flex flex-wrap gap-3 pt-2">
          <button type="submit" disabled={loading} className={btnPrimary}>
            {loading ? 'Захоўваю...' : '💾 Дадаць'}
          </button>
          <button type="button" onClick={onCancel} className={btnSecondary}>
            Адмена
          </button>
        </div>
      </form>
    </div>
  );
};

export default AddSupplierForm;
