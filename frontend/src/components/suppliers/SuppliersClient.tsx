'use client';
import { useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import type { Supplier } from '@/types/supplier';
import { FiPlus, FiPackage } from 'react-icons/fi';
import { apiCredentials, getApiBaseUrl } from '@/lib/api/common';
import AddSupplierForm from './AddSupplierForm';
import SupplierNameSearch from './SupplierNameSearch';
import SuppliersTable from './SuppliersTable';

enum ViewMode {
  Default = 'default',
  AddSupplier = 'addSupplier',
  Inventory = 'inventory',
}

const SuppliersClient = () => {
  const router = useRouter();
  const [mode, setMode] = useState<ViewMode>(ViewMode.Default);
  const { setTopbarButtons, setTopbarPage } = useTopbar();

  const [suppliers, setSuppliers] = useState<Supplier[]>([]);
  const [loading, setLoading] = useState(true);
  const [searchQuery, setSearchQuery] = useState('');

  const filteredSuppliers = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return suppliers;
    return suppliers.filter((s) => s.name.toLowerCase().startsWith(q));
  }, [suppliers, searchQuery]);

  useEffect(() => {
    const clear = () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };

    switch (mode) {
      case ViewMode.AddSupplier:
        setTopbarPage({ title: 'Дадаць пастаўшчыка' });
        setTopbarButtons([]);
        return clear;
      case ViewMode.Inventory:
        setTopbarPage({ title: 'Інвентарызацыя' });
        setTopbarButtons([]);
        return clear;
      default:
        setTopbarPage({
          title: 'Пастаўшчыкі',
          subtitle: loading
            ? 'Загрузка…'
            : `Усяго: ${suppliers.length}${searchQuery.trim() ? ` · паказана: ${filteredSuppliers.length}` : ''}`,
        });
        setTopbarButtons([
          {
            label: 'Новы пастаўшчык',
            icon: <FiPlus />,
            onClick: () => setMode(ViewMode.AddSupplier),
            variant: 'primary',
          },
          {
            label: 'Інвентарызацыя',
            icon: <FiPackage />,
            onClick: () => setMode(ViewMode.Inventory),
            variant: 'secondary',
          },
        ]);
        return clear;
    }
  }, [
    mode,
    loading,
    suppliers.length,
    searchQuery,
    filteredSuppliers.length,
    setTopbarButtons,
    setTopbarPage,
  ]);

  useEffect(() => {
    fetch(`${getApiBaseUrl()}/suppliers`, {
      credentials: apiCredentials,
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
    return <LoadingSpinner label="Загрузка пастаўшчыкоў..." />;
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
    default: {
      return (
        <div className="mx-auto w-full max-w-6xl space-y-6">
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
