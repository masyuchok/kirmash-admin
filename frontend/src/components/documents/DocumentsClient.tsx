'use client';

import { useEffect, useState } from 'react';
import { FiFileText } from 'react-icons/fi';
import { useRouter } from 'next/navigation';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { fetchVatReports, generateVatReport } from '@/lib/api/reports';
import type { VatReport } from '@/types/report';

function formatAmount(value: number): string {
  return value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function formatPeriod(month: number, year: number): string {
  const months = [
    'Студзень',
    'Люты',
    'Сакавік',
    'Красавік',
    'Май',
    'Чэрвень',
    'Ліпень',
    'Жнівень',
    'Верасень',
    'Кастрычнік',
    'Лістапад',
    'Снежань',
  ];
  const monthLabel = month >= 1 && month <= 12 ? months[month - 1] : `Месяц ${month}`;
  return `${monthLabel} ${year}`;
}

export default function DocumentsClient() {
  const router = useRouter();
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [modalOpen, setModalOpen] = useState(false);
  const [generateModalOpen, setGenerateModalOpen] = useState(false);
  const [reports, setReports] = useState<VatReport[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const now = new Date();
  const [selectedMonth, setSelectedMonth] = useState(now.getMonth() + 1);
  const [selectedYear, setSelectedYear] = useState(now.getFullYear());
  const [generating, setGenerating] = useState(false);
  const [generateError, setGenerateError] = useState<string | null>(null);

  useEffect(() => {
    setTopbarPage({ title: 'Дакументы' });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [setTopbarButtons, setTopbarPage]);

  useEffect(() => {
    if (!modalOpen) return;
    let cancelled = false;
    setLoading(true);
    setError(null);

    fetchVatReports()
      .then((data) => {
        if (!cancelled) setReports(data);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Памылка загрузкі справаздач');
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [modalOpen]);

  const handleGenerate = async () => {
    setGenerating(true);
    setGenerateError(null);
    try {
      const created = await generateVatReport(selectedYear, selectedMonth);
      setReports((prev) => [created, ...prev]);
      setGenerateModalOpen(false);
    } catch (err: unknown) {
      setGenerateError(err instanceof Error ? err.message : 'Памылка генерацыі справаздачы');
    } finally {
      setGenerating(false);
    }
  };

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 px-6 py-5">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">Дакументы</h2>
        </div>
        <div className="p-6">
          <button
            type="button"
            onClick={() => setModalOpen(true)}
            className="inline-flex items-center gap-2 rounded-xl border border-gray-200 bg-white px-4 py-2.5 text-sm font-medium text-gray-800 transition hover:bg-gray-50"
          >
            <FiFileText className="size-4 text-primary" aria-hidden />
            Справаздача
          </button>
        </div>
      </div>

      {modalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-3xl rounded-2xl border border-gray-200 bg-white shadow-xl">
            <div className="flex items-center justify-between border-b border-gray-100 px-5 py-4">
              <h3 className="text-base font-semibold text-gray-900">Згенераваныя справаздачы</h3>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setGenerateModalOpen(true)}
                  className="rounded-md border border-primary/30 bg-primary/5 px-3 py-1.5 text-sm font-medium text-primary transition hover:bg-primary/10"
                >
                  Згенераваць новы
                </button>
                <button
                  type="button"
                  onClick={() => setModalOpen(false)}
                  className="rounded-md px-2 py-1 text-sm text-gray-500 transition hover:bg-gray-100 hover:text-gray-700"
                >
                  Закрыць
                </button>
              </div>
            </div>
            <div className="overflow-x-auto px-5 py-4">
              {loading ? (
                <div className="py-8">
                  <LoadingSpinner label="Загрузка справаздач..." />
                </div>
              ) : error ? (
                <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
                  {error}
                </div>
              ) : reports.length === 0 ? (
                <p className="py-6 text-sm text-gray-500">Справаздач пакуль няма.</p>
              ) : (
                <table className="min-w-full border-collapse text-left text-sm">
                  <thead>
                    <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                      <th className="px-3 py-2.5">Месяц-год</th>
                      <th className="px-3 py-2.5 text-right">Ват</th>
                      <th className="px-3 py-2.5 text-right">Ват в зачет</th>
                      <th className="px-3 py-2.5 text-right">Ваты к оплате</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-100">
                    {reports.map((item) => (
                      <tr
                        key={item.id}
                        className="cursor-pointer bg-white transition hover:bg-gray-50"
                        onClick={() => router.push(`/documents/reports/${item.id}`)}
                      >
                        <td className="px-3 py-3 font-medium text-gray-900">
                          {formatPeriod(item.periodMonth, item.periodYear)}
                        </td>
                        <td className="px-3 py-3 text-right tabular-nums text-gray-700">
                          {formatAmount(item.vat)}
                        </td>
                        <td className="px-3 py-3 text-right tabular-nums text-gray-700">
                          {formatAmount(item.vatCredit)}
                        </td>
                        <td className="px-3 py-3 text-right tabular-nums font-semibold text-gray-900">
                          {formatAmount(item.vatToPay)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          </div>
        </div>
      )}

      {modalOpen && generateModalOpen && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 p-4">
          <div className="w-full max-w-md rounded-2xl border border-gray-200 bg-white shadow-xl">
            <div className="border-b border-gray-100 px-5 py-4">
              <h3 className="text-base font-semibold text-gray-900">Генерацыя справаздачы</h3>
            </div>
            <div className="space-y-4 px-5 py-4">
              {generateError && (
                <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
                  {generateError}
                </div>
              )}
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Месяц</span>
                <select
                  value={selectedMonth}
                  onChange={(e) => setSelectedMonth(Number(e.currentTarget.value))}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                >
                  {Array.from({ length: 12 }, (_, i) => i + 1).map((month) => (
                    <option key={month} value={month}>
                      {formatPeriod(month, 2000).replace(' 2000', '')}
                    </option>
                  ))}
                </select>
              </label>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Год</span>
                <input
                  type="number"
                  min={2000}
                  max={3000}
                  value={selectedYear}
                  onChange={(e) => setSelectedYear(Number(e.currentTarget.value))}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
            </div>
            <div className="flex items-center justify-end gap-2 border-t border-gray-100 px-5 py-4">
              <button
                type="button"
                onClick={() => setGenerateModalOpen(false)}
                className="rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 transition hover:bg-gray-50"
              >
                Адмена
              </button>
              <button
                type="button"
                onClick={handleGenerate}
                disabled={generating}
                className="inline-flex items-center gap-2 rounded-lg border border-primary/30 bg-primary px-3 py-2 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-60"
              >
                {generating && (
                  <span className="size-3.5 animate-spin rounded-full border-2 border-white/40 border-t-white" />
                )}
                Згенераваць
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
