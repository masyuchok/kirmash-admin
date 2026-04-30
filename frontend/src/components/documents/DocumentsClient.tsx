'use client';

import { useEffect, useMemo, useState } from 'react';
import { FiFileText, FiRefreshCw, FiX } from 'react-icons/fi';
import { useRouter } from 'next/navigation';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { fetchVatReports, generateVatReport, regenerateVatReport } from '@/lib/api/reports';
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
  const [regeneratingId, setRegeneratingId] = useState<number | null>(null);
  const [pendingRegenerateReport, setPendingRegenerateReport] = useState<VatReport | null>(null);

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
      const results = await Promise.allSettled([
        generateVatReport(selectedYear, selectedMonth, 'poland'),
        generateVatReport(selectedYear, selectedMonth, 'foreign'),
      ]);

      const created = results
        .filter((r): r is PromiseFulfilledResult<VatReport> => r.status === 'fulfilled')
        .map((r) => r.value);
      const rejected = results.filter((r): r is PromiseRejectedResult => r.status === 'rejected');

      if (created.length > 0) {
        setReports((prev) => {
          const byId = new Map<number, VatReport>(prev.map((item) => [item.id, item]));
          created.forEach((item) => byId.set(item.id, item));
          return Array.from(byId.values()).sort((a, b) => {
            if (a.periodYear !== b.periodYear) return b.periodYear - a.periodYear;
            if (a.periodMonth !== b.periodMonth) return b.periodMonth - a.periodMonth;
            if (a.type !== b.type) return a.type.localeCompare(b.type);
            return b.id - a.id;
          });
        });
        setGenerateModalOpen(false);
      } else if (rejected.length > 0) {
        const message = rejected
          .map((item) =>
            item.reason instanceof Error ? item.reason.message : 'Памылка генерацыі справаздачы'
          )
          .join('\n');
        setGenerateError(message);
      }
    } catch (err: unknown) {
      setGenerateError(err instanceof Error ? err.message : 'Памылка генерацыі справаздачы');
    } finally {
      setGenerating(false);
    }
  };

  const alreadyExistsPoland = reports.some(
    (r) => r.periodMonth === selectedMonth && r.periodYear === selectedYear && r.type === 'poland'
  );
  const alreadyExistsForeign = reports.some(
    (r) => r.periodMonth === selectedMonth && r.periodYear === selectedYear && r.type === 'foreign'
  );
  const alreadyExistsForPeriod = alreadyExistsPoland && alreadyExistsForeign;

  const groupedReports = useMemo(() => {
    const byPeriod = new Map<
      string,
      {
        periodMonth: number;
        periodYear: number;
        vat: number;
        vatCredit: number;
        vatToPay: number;
        reports: VatReport[];
      }
    >();
    reports.forEach((report) => {
      const key = `${report.periodYear}-${report.periodMonth}`;
      const current = byPeriod.get(key);
      if (!current) {
        byPeriod.set(key, {
          periodMonth: report.periodMonth,
          periodYear: report.periodYear,
          vat: report.vat,
          vatCredit: report.vatCredit,
          vatToPay: report.vatToPay,
          reports: [report],
        });
        return;
      }
      current.vat += report.vat;
      current.vatCredit += report.vatCredit;
      current.vatToPay += report.vatToPay;
      current.reports.push(report);
    });

    return Array.from(byPeriod.entries())
      .map(([key, value]) => ({
        key,
        ...value,
        reports: value.reports.sort((a, b) => {
          if (a.type === b.type) return b.id - a.id;
          return a.type === 'poland' ? -1 : 1;
        }),
      }))
      .sort((a, b) => {
        if (a.periodYear !== b.periodYear) return b.periodYear - a.periodYear;
        return b.periodMonth - a.periodMonth;
      });
  }, [reports]);

  const handleRegenerate = async (report: VatReport) => {
    setRegeneratingId(report.id);
    setError(null);
    try {
      const samePeriodReports = reports.filter(
        (item) => item.periodYear === report.periodYear && item.periodMonth === report.periodMonth
      );
      const targets = samePeriodReports.length > 0 ? samePeriodReports : [report];
      const results = await Promise.allSettled(targets.map((item) => regenerateVatReport(item.id)));
      const updatedItems = results
        .filter((r): r is PromiseFulfilledResult<VatReport> => r.status === 'fulfilled')
        .map((r) => r.value);
      if (updatedItems.length > 0) {
        setReports((prev) =>
          prev.map((item) => updatedItems.find((updated) => updated.id === item.id) ?? item)
        );
      }
      const rejected = results.filter((r): r is PromiseRejectedResult => r.status === 'rejected');
      if (rejected.length > 0 && updatedItems.length === 0) {
        const message = rejected
          .map((r) => (r.reason instanceof Error ? r.reason.message : 'Памылка перегенерацыі справаздачы'))
          .join('\n');
        setError(message);
      }
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка перегенерацыі справаздачы');
    } finally {
      setRegeneratingId(null);
      setPendingRegenerateReport(null);
    }
  };

  useEffect(() => {
    if (!pendingRegenerateReport) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      if (regeneratingId !== null) return;
      setPendingRegenerateReport(null);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [pendingRegenerateReport, regeneratingId]);

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
            className="group inline-flex items-center gap-2 rounded-xl border border-gray-200 bg-white px-4 py-2.5 text-sm font-medium text-gray-800 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary hover:shadow-md"
          >
            <FiFileText className="size-4 text-primary transition group-hover:text-primary" aria-hidden />
            Справаздачы
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
                  Згенераваць новую
                </button>
                <button
                  type="button"
                  onClick={() => setModalOpen(false)}
                  className="inline-flex size-8 items-center justify-center rounded-md text-gray-500 transition hover:bg-gray-100 hover:text-gray-700"
                  aria-label="Закрыць"
                  title="Закрыць"
                >
                  <FiX className="size-4" aria-hidden />
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
                      <th className="px-3 py-2.5 text-right">VAT</th>
                      <th className="px-3 py-2.5 text-right">VAT да ўліку</th>
                      <th className="px-3 py-2.5 text-right">VAT да аплаты</th>
                      <th className="px-3 py-2.5 text-right">Дзеянне</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-100">
                    {groupedReports.map((group) => {
                      const primaryReport =
                        group.reports.find((r) => r.type === 'poland') ?? group.reports[0];
                      return (
                        <tr
                          key={group.key}
                          className="cursor-pointer bg-white transition hover:bg-primary/10"
                          onClick={() => router.push(`/documents/reports/${primaryReport.id}`)}
                        >
                          <td className="px-3 py-3 font-medium text-gray-900">
                            {formatPeriod(group.periodMonth, group.periodYear)}
                          </td>
                          <td className="px-3 py-3 text-right tabular-nums text-gray-700">
                            {formatAmount(group.vat)}
                          </td>
                          <td className="px-3 py-3 text-right tabular-nums text-gray-700">
                            {formatAmount(group.vatCredit)}
                          </td>
                          <td className="px-3 py-3 text-right tabular-nums font-semibold text-gray-900">
                            {formatAmount(group.vatToPay)}
                          </td>
                          <td className="px-3 py-3 text-right">
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                setPendingRegenerateReport(primaryReport);
                              }}
                              disabled={regeneratingId === primaryReport.id}
                              className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-700 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:opacity-60"
                              aria-label="Перегенераваць справаздачу"
                              title="Перегенераваць справаздачу"
                            >
                              {regeneratingId === primaryReport.id ? (
                                <span className="size-3.5 animate-spin rounded-full border-2 border-gray-300 border-t-gray-700" />
                              ) : (
                                <FiRefreshCw className="size-4" aria-hidden />
                              )}
                            </button>
                          </td>
                        </tr>
                      );
                    })}
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
                disabled={generating || alreadyExistsForPeriod}
                className="inline-flex items-center gap-2 rounded-lg border border-primary/30 bg-primary px-3 py-2 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-60"
              >
                {generating && (
                  <span className="size-3.5 animate-spin rounded-full border-2 border-white/40 border-t-white" />
                )}
                Згенераваць
              </button>
            </div>
            {alreadyExistsForPeriod && (
              <p className="px-5 pb-4 text-xs text-amber-700">
                Справаздачы за гэты месяц ужо існуюць (Польшча і Замежжа). Выкарыстайце кнопку «Перегенераваць».
              </p>
            )}
          </div>
        </div>
      )}

      {pendingRegenerateReport && (
        <div
          className="fixed inset-0 z-[70] flex items-center justify-center bg-black/40 p-4"
          onClick={() => {
            if (regeneratingId !== null) return;
            setPendingRegenerateReport(null);
          }}
        >
          <div
            className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="text-base font-semibold text-gray-900">Пацвердзіце перегенерацыю</div>
            <p className="mt-2 text-sm text-gray-600">
              Перагенераваць справаздачу за {formatPeriod(pendingRegenerateReport.periodMonth, pendingRegenerateReport.periodYear)}?
            </p>
            <div className="mt-5 flex items-center justify-end gap-2">
              <button
                type="button"
                onClick={() => setPendingRegenerateReport(null)}
                disabled={regeneratingId !== null}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-60"
              >
                Адмена
              </button>
              <button
                type="button"
                onClick={() => handleRegenerate(pendingRegenerateReport)}
                disabled={regeneratingId !== null}
                className="inline-flex min-w-24 items-center justify-center rounded-lg border border-primary bg-primary px-3 py-1.5 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-60"
              >
                {regeneratingId !== null ? (
                  <span className="size-4 animate-spin rounded-full border-2 border-primary/20 border-t-white" />
                ) : (
                  'Перагенераваць'
                )}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
