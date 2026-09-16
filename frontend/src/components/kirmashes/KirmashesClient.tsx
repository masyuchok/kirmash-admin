'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { FiPlus } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import {
  deleteKirmash,
  fetchKirmashes,
  type KirmashListItem,
} from '@/lib/api/kirmashes';

function formatDateBe(isoDate: string): string {
  const [y, m, d] = isoDate.split('-');
  if (!y || !m || !d) return isoDate;
  return `${d}.${m}.${y}`;
}

export default function KirmashesClient() {
  const router = useRouter();
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [rows, setRows] = useState<KirmashListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<number | null>(null);

  const refresh = useCallback(async () => {
    const data = await fetchKirmashes();
    setRows(data);
  }, []);

  useEffect(() => {
    setTopbarPage({
      title: 'Кірмашы',
      subtitle: loading ? 'Загрузка…' : `Спіс кірмашоў (${rows.length})`,
    });
    setTopbarButtons([
      {
        label: 'Новы кірмаш',
        icon: <FiPlus />,
        onClick: () => router.push('/kirmashes/new'),
        variant: 'primary',
      },
    ]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [loading, rows.length, router, setTopbarButtons, setTopbarPage]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    refresh()
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(
            err instanceof Error ? err.message : 'Памылка загрузкі кірмашоў'
          );
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [refresh]);

  const sorted = useMemo(
    () =>
      [...rows].sort((a, b) =>
        a.eventDate < b.eventDate ? 1 : a.eventDate > b.eventDate ? -1 : 0
      ),
    [rows]
  );

  const onDelete = async (id: number) => {
    if (!window.confirm('Выдаліць гэты кірмаш?')) return;
    setDeletingId(id);
    setError(null);
    try {
      await deleteKirmash(id);
      await refresh();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка выдалення');
    } finally {
      setDeletingId(null);
    }
  };

  if (loading) {
    return (
      <div className="flex justify-center py-16">
        <LoadingSpinner />
      </div>
    );
  }

  return (
    <div className="space-y-4 p-4 sm:p-6">
      {error && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          {error}
        </div>
      )}
      {sorted.length === 0 ? (
        <div className="rounded-xl border border-dashed border-gray-300 bg-white px-4 py-12 text-center text-sm text-gray-500">
          Пакуль няма кірмашоў. Стварыце першы.
        </div>
      ) : (
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white">
          <table className="min-w-full divide-y divide-gray-200 text-sm">
            <thead className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
              <tr>
                <th className="px-4 py-3">Назва</th>
                <th className="px-4 py-3">Дата</th>
                <th className="px-4 py-3">Тавары</th>
                <th className="px-4 py-3">Цэннікі</th>
                <th className="px-4 py-3">Статус</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {sorted.map((row) => (
                <tr key={row.id} className="hover:bg-gray-50">
                  <td className="px-4 py-3">
                    <button
                      type="button"
                      onClick={() => router.push(`/kirmashes/${row.id}`)}
                      className="font-medium text-primary hover:underline"
                    >
                      {row.title}
                    </button>
                    {row.description.trim() && (
                      <div className="mt-0.5 line-clamp-1 text-xs text-gray-500">
                        {row.description}
                      </div>
                    )}
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap text-gray-700">
                    {formatDateBe(row.eventDate)}
                  </td>
                  <td className="px-4 py-3 text-gray-700">
                    {row.linesCount} / {row.unitsCount} шт.
                  </td>
                  <td className="px-4 py-3 text-gray-700">{row.tagsCount}</td>
                  <td className="px-4 py-3">
                    <span
                      className={
                        row.status === 'ready'
                          ? 'rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700'
                          : 'rounded-full bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-600'
                      }
                    >
                      {row.status === 'ready' ? 'Гатовы' : 'Чарнавік'}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-right">
                    <button
                      type="button"
                      disabled={deletingId === row.id}
                      onClick={() => void onDelete(row.id)}
                      className="text-xs text-red-600 hover:underline disabled:opacity-50"
                    >
                      Выдаліць
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
