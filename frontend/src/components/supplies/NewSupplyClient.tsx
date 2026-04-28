'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { FiPlus } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { apiCredentials, getApiBaseUrl } from '@/lib/api/common';

type Props = {
  initialSupplierId?: string;
  initialSupplierName?: string;
  initialDate: string;
  supplyId?: string;
};

type SupplierOption = {
  id: number;
  name: string;
};

export default function NewSupplyClient({
  initialSupplierId = '',
  initialSupplierName = '',
  initialDate,
  supplyId,
}: Props) {
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [suppliers, setSuppliers] = useState<SupplierOption[]>([]);
  const [hint, setHint] = useState('');

  useEffect(() => {
    setTopbarPage({ title: supplyId ? `Пастаўка #${supplyId}` : 'Новая пастаўка' });
    setTopbarButtons([
      {
        label: 'Дадаць тавар',
        icon: <FiPlus />,
        onClick: () => setHint('Дадаванне тавару будзе рэалізавана на наступным этапе.'),
        variant: 'primary',
      },
    ]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [setTopbarButtons, setTopbarPage, supplyId]);

  useEffect(() => {
    let cancelled = false;
    fetch(`${getApiBaseUrl()}/suppliers`, { credentials: apiCredentials })
      .then((res) => res.json())
      .then((data: unknown) => {
        if (cancelled || !Array.isArray(data)) return;
        const rows = data
          .map((row) => {
            const r = row as Record<string, unknown>;
            const id = typeof r.id === 'number' ? r.id : Number(r.id);
            const name = typeof r.name === 'string' ? r.name : '';
            if (!Number.isFinite(id) || !name.trim()) return null;
            return { id, name };
          })
          .filter((row): row is SupplierOption => row !== null);
        setSuppliers(rows);
      })
      .catch(() => {
        if (!cancelled) setSuppliers([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const supplierName = useMemo(() => {
    if (initialSupplierName.trim()) return initialSupplierName.trim();
    const match = suppliers.find((s) => String(s.id) === initialSupplierId);
    return match?.name ?? `ID: ${initialSupplierId}`;
  }, [initialSupplierId, initialSupplierName, suppliers]);

  if (!initialDate || (!initialSupplierId && !initialSupplierName.trim())) {
    return (
      <div className="mx-auto w-full max-w-6xl rounded-xl border border-gray-200 bg-white px-6 py-8 shadow-sm">
        <p className="text-sm text-red-600">Не хапае параметраў для адкрыцця пастаўкі.</p>
        <Link href="/supplies" className="mt-3 inline-block text-sm font-medium text-primary hover:underline">
          Вярнуцца да паставак
        </Link>
      </div>
    );
  }

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      <div className="rounded-xl border border-gray-200 bg-white px-6 py-4 shadow-sm">
        <p className="text-sm text-gray-700">
          <span className="font-medium text-gray-900">Пастаўшчык:</span> {supplierName}
        </p>
        <p className="mt-1 text-sm text-gray-700">
          <span className="font-medium text-gray-900">Дата пастаўкі:</span> {initialDate}
        </p>
        {hint && <p className="mt-3 text-sm text-gray-500">{hint}</p>}
      </div>

      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="overflow-x-auto">
          <table className="min-w-full border-collapse text-left text-sm">
            <thead>
              <tr className="border-b border-gray-200 bg-gray-50/90 text-xs font-semibold uppercase tracking-wide text-gray-500 backdrop-blur-sm">
                <th className="whitespace-nowrap px-6 py-3.5">Назва</th>
                <th className="whitespace-nowrap px-4 py-3.5">Колькасць</th>
                <th className="whitespace-nowrap px-4 py-3.5">Цана пастаўшчыка</th>
                <th className="whitespace-nowrap px-6 py-3.5">Цана продажу</th>
              </tr>
            </thead>
            <tbody className="bg-white">
              <tr>
                <td colSpan={4} className="px-6 py-16 text-center">
                  <p className="text-sm font-medium text-gray-900">Тавары яшчэ не дададзеныя</p>
                  <p className="mt-1 text-sm text-gray-500">Націсніце "Дадаць тавар", каб пачаць запаўненне.</p>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
