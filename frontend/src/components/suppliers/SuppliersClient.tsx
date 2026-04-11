'use client';
import { useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { useTopbar } from '@/components/topbar/TopbarContext';
import type { Supplier } from '@/types/supplier';
import { FiPlus, FiPackage, FiFileText } from 'react-icons/fi';
import AddSupplierForm from './AddSupplierForm';
import SupplierNameSearch from './SupplierNameSearch';
import SuppliersTable from './SuppliersTable';

enum ViewMode {
  Default = 'default',
  AddSupplier = 'addSupplier',
  Inventory = 'inventory',
  Documents = 'documents',
}

const SuppliersClient = () => {
  const router = useRouter();
  const [mode, setMode] = useState<ViewMode>(ViewMode.Default);
  const { setTopbarButtons } = useTopbar();

  const [suppliers, setSuppliers] = useState<Supplier[]>([]);
  const [loading, setLoading] = useState(true);
  const [searchQuery, setSearchQuery] = useState('');

  const filteredSuppliers = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return suppliers;
    return suppliers.filter((s) => s.name.toLowerCase().startsWith(q));
  }, [suppliers, searchQuery]);

  useEffect(() => {
    setTopbarButtons([
      {
        label: 'Новы пастаўшчык',
        icon: <FiPlus />,
        onClick: () => setMode(ViewMode.AddSupplier),
      },
      {
        label: 'Інвентарызацыя',
        icon: <FiPackage />,
        onClick: () => setMode(ViewMode.Inventory),
      },
      {
        label: 'Дакументы',
        icon: <FiFileText />,
        onClick: () => setMode(ViewMode.Documents),
      },
    ]);
    return () => setTopbarButtons([]);
  }, [setTopbarButtons]);

  useEffect(() => {
    fetch(`${process.env.NEXT_PUBLIC_API_URL}/suppliers`, {
      credentials: 'include',
    })
      .then((res) => res.json())
      .then((data) => {
        setSuppliers(data);
        setLoading(false);
      })
      .catch((err) => {
        console.error('Памылка загрузкі пастаўшчыкоў:', err);
        setLoading(false);
      });
  }, []);

  if (loading) {
    return (
      <div className="mx-auto w-full max-w-6xl space-y-6">
        <div className="space-y-2">
          <div className="h-8 w-56 animate-pulse rounded-lg bg-gray-200/80" />
          <div className="h-4 w-72 animate-pulse rounded-md bg-gray-100" />
        </div>
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-100 p-5">
            <div className="mb-2 h-3 w-32 animate-pulse rounded bg-gray-100" />
            <div className="h-10 w-full max-w-xl animate-pulse rounded-lg bg-gray-100" />
          </div>
          <div className="space-y-0 divide-y divide-gray-100 p-4">
            {Array.from({ length: 8 }).map((_, i) => (
              <div key={i} className="h-12 animate-pulse rounded-md bg-gray-50" />
            ))}
          </div>
        </div>
      </div>
    );
  }

  switch (mode) {
    case ViewMode.AddSupplier:
      return (
        <AddSupplierForm
          onSuccess={() => setMode(ViewMode.Default)}
          onCancel={() => setMode(ViewMode.Default)}
        />
      );
    case ViewMode.Inventory:
      return (
        <div className="mx-auto max-w-6xl rounded-xl border border-gray-200 bg-white p-10 text-center shadow-sm">
          <p className="text-sm text-gray-500">📦 Тут будзе інвентарызацыя</p>
        </div>
      );
    case ViewMode.Documents:
      return (
        <div className="mx-auto max-w-6xl rounded-xl border border-gray-200 bg-white p-10 text-center shadow-sm">
          <p className="text-sm text-gray-500">📁 Тут будуць дакументы</p>
        </div>
      );
    default: {
      const total = suppliers.length;
      const shown = filteredSuppliers.length;
      return (
        <div className="mx-auto w-full max-w-6xl space-y-6">
          <header className="space-y-1">
            <h1 className="text-2xl font-semibold tracking-tight text-gray-900">Пастаўшчыкі</h1>
            <p className="text-sm text-gray-500">
              Усяго: {total}
              {searchQuery.trim() ? ` · паказана: ${shown}` : ''}
            </p>
          </header>

          <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
            <div className="border-b border-gray-100 p-5 sm:p-6">
              <SupplierNameSearch
                suppliers={suppliers}
                value={searchQuery}
                onChange={setSearchQuery}
              />
            </div>
            <SuppliersTable
              suppliers={filteredSuppliers}
              hasActiveFilter={Boolean(searchQuery.trim())}
              onEdit={(s) => router.push(`/suppliers/${s.id}/edit`)}
            />
          </div>
        </div>
      );
    }
  }
};

export default SuppliersClient;
