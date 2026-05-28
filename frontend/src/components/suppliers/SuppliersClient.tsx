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
import SupplierInventoryClient from './SupplierInventoryClient';

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
  const [inventorySupplierId, setInventorySupplierId] = useState<number | null>(null);
  const [inventorySupplierName, setInventorySupplierName] = useState('');

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
        setTopbarPage({
          title: 'Інвентарызацыя',
          subtitle: inventorySupplierName ? inventorySupplierName : 'Усе пастаўшчыкі',
        });
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
            onClick: () => {
              setInventorySupplierId(null);
              setInventorySupplierName('');
              setMode(ViewMode.Inventory);
            },
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
    inventorySupplierName,
  ]);

  const openInventory = (supplier?: Supplier) => {
    setInventorySupplierId(supplier?.id ?? null);
    setInventorySupplierName(supplier?.name ?? '');
    setMode(ViewMode.Inventory);
  };

  const closeInventory = () => {
    setInventorySupplierId(null);
    setInventorySupplierName('');
    setMode(ViewMode.Default);
  };

  useEffect(() => {
    fetch(`${getApiBaseUrl()}/suppliers`, {
      credentials: apiCredentials,
    })
      .then((res) => res.json())
      .then((data: unknown) => {
        const rows = Array.isArray(data)
          ? data.map((row) => {
              const r = row as Record<string, unknown>;
              return {
                id: Number(r.id ?? r.Id ?? 0),
                name: String(r.name ?? r.Name ?? ''),
                telegram: String(
                  r.telegram ?? r.Telegram ?? r.tgContact ?? r.tGContact ?? r.TGContact ?? ''
                ),
                website: String(r.website ?? r.Website ?? ''),
                country: String(r.country ?? r.Country ?? ''),
                city: String(r.city ?? r.City ?? ''),
                isVatPayer: Boolean(r.isVatPayer ?? r.isVATPayer ?? r.IsVatPayer ?? r.IsVATPayer ?? false),
              } satisfies Supplier;
            })
          : [];
        setSuppliers(rows);
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
        <SupplierInventoryClient
          supplierId={inventorySupplierId}
          supplierName={inventorySupplierName}
          onBack={closeInventory}
        />
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
              onInventory={(s) => openInventory(s)}
            />
          </div>
        </div>
      );
    }
  }
};

export default SuppliersClient;
