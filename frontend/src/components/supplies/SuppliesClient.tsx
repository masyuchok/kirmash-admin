'use client';

import { useEffect, useState } from 'react';
import { FiPlus } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { fetchSupplies } from '@/lib/api/supplies';
import type { SupplyListItem } from '@/types/supply';
import SuppliesTable from './SuppliesTable';

export default function SuppliesClient() {
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [rows, setRows] = useState<SupplyListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setTopbarPage({
      title: 'Пастаўкі',
      subtitle: loading ? 'Загрузка…' : `Спіс паставак (${rows.length})`,
    });
    setTopbarButtons([
      {
        label: 'Новая пастаўка',
        icon: <FiPlus />,
        onClick: () => {},
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

      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <SuppliesTable supplies={rows} />
      </div>
    </div>
  );
}
