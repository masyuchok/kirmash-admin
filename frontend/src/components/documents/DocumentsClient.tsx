'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { FiChevronDown, FiChevronRight, FiLock, FiRefreshCw, FiUnlock } from 'react-icons/fi';
import { useRouter } from 'next/navigation';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { useTopbar } from '@/components/topbar/TopbarContext';
import {
  fetchVatReportPeriods,
  generateVatReport,
  regenerateVatReport,
  setVatReportLocked,
} from '@/lib/api/reports';
import type { VatReport, VatReportPeriod } from '@/types/report';

type DocumentsTabId = 'reports' | 'other';

const documentsTabs: { id: DocumentsTabId; label: string }[] = [
  { id: 'reports', label: 'Справаздачы' },
  { id: 'other', label: 'Іншыя дакументы' },
];

function formatAmount(value: number): string {
  return value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

const MONTH_NAMES = [
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
] as const;

function formatMonthName(month: number): string {
  return month >= 1 && month <= 12 ? MONTH_NAMES[month - 1] : `Месяц ${month}`;
}

function formatPeriod(month: number, year: number): string {
  return `${formatMonthName(month)} ${year}`;
}

function yearCollapseKey(year: number): string {
  return `y:${year}`;
}

export default function DocumentsClient() {
  const router = useRouter();
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [generateModalOpen, setGenerateModalOpen] = useState(false);
  const [periods, setPeriods] = useState<VatReportPeriod[]>([]);
  const [collapsedYears, setCollapsedYears] = useState<Set<string>>(() => new Set());
  const yearsCollapseInitialized = useRef(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const now = new Date();
  const [selectedMonth, setSelectedMonth] = useState(now.getMonth() + 1);
  const [selectedYear, setSelectedYear] = useState(now.getFullYear());
  const [generating, setGenerating] = useState(false);
  const [generateError, setGenerateError] = useState<string | null>(null);
  const [regeneratingId, setRegeneratingId] = useState<number | null>(null);
  const [pendingRegeneratePeriod, setPendingRegeneratePeriod] = useState<VatReportPeriod | null>(null);
  const [lockingReportId, setLockingReportId] = useState<number | null>(null);
  const [activeTab, setActiveTab] = useState<DocumentsTabId>('reports');

  useEffect(() => {
    setTopbarPage({ title: 'Дакументы' });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [setTopbarButtons, setTopbarPage]);

  useEffect(() => {
    if (activeTab !== 'reports') return;
    let cancelled = false;
    setLoading(true);
    setError(null);

    fetchVatReportPeriods()
      .then((data) => {
        if (!cancelled) setPeriods(data);
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
  }, [activeTab]);

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
        const refreshed = await fetchVatReportPeriods();
        setPeriods(refreshed);
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

  const currentCalendarYear = now.getFullYear();

  const alreadyExistsForPeriod = periods.some(
    (p) => p.periodMonth === selectedMonth && p.periodYear === selectedYear
  );

  const groupedByYear = useMemo(() => {
    const byYear = new Map<number, VatReportPeriod[]>();
    periods.forEach((period) => {
      const list = byYear.get(period.periodYear) ?? [];
      list.push(period);
      byYear.set(period.periodYear, list);
    });
    return Array.from(byYear.entries())
      .sort((a, b) => b[0] - a[0])
      .map(([year, yearPeriods]) => ({
        year,
        periods: yearPeriods.sort((a, b) => b.periodMonth - a.periodMonth),
      }));
  }, [periods]);

  useEffect(() => {
    if (periods.length === 0 || yearsCollapseInitialized.current) return;
    yearsCollapseInitialized.current = true;
    const years = [...new Set(periods.map((p) => p.periodYear))];
    setCollapsedYears(
      new Set(years.filter((year) => year !== currentCalendarYear).map((year) => yearCollapseKey(year)))
    );
  }, [periods, currentCalendarYear]);

  const toggleYear = (year: number) => {
    const key = yearCollapseKey(year);
    setCollapsedYears((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const isYearCollapsed = (year: number) => collapsedYears.has(yearCollapseKey(year));

  const refreshPeriods = async () => {
    const refreshed = await fetchVatReportPeriods();
    setPeriods(refreshed);
  };

  const handleToggleLock = async (period: VatReportPeriod) => {
    const nextLocked = !period.isLocked;
    const lockTarget =
      period.reports.find((r) => r.type === 'poland') ?? period.reports[0];
    if (!lockTarget) return;
    setLockingReportId(lockTarget.id);
    setError(null);
    try {
      await setVatReportLocked(lockTarget.id, nextLocked);
      await refreshPeriods();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка змены блакавання');
    } finally {
      setLockingReportId(null);
    }
  };

  const handleRegenerate = async (period: VatReportPeriod) => {
    const primaryReport = period.reports.find((r) => r.type === 'poland') ?? period.reports[0];
    if (!primaryReport) return;
    setRegeneratingId(primaryReport.id);
    setError(null);
    try {
      const targets = period.reports.length > 0 ? period.reports : [primaryReport];
      const results = await Promise.allSettled(targets.map((item) => regenerateVatReport(item.id)));
      const updatedItems = results
        .filter((r): r is PromiseFulfilledResult<VatReport> => r.status === 'fulfilled')
        .map((r) => r.value);
      if (updatedItems.length > 0) {
        await refreshPeriods();
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
      setPendingRegeneratePeriod(null);
    }
  };

  useEffect(() => {
    if (!pendingRegeneratePeriod) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      if (regeneratingId !== null) return;
      setPendingRegeneratePeriod(null);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [pendingRegeneratePeriod, regeneratingId]);

  return (
    <div className="mx-auto w-full max-w-6xl space-y-4">
      <div className="rounded-xl border border-gray-200 bg-white p-2 shadow-sm">
        <div className="flex flex-wrap gap-2">
          {documentsTabs.map((tab) => {
            const isActive = activeTab === tab.id;
            return (
              <button
                key={tab.id}
                type="button"
                onClick={() => setActiveTab(tab.id)}
                className={`rounded-lg px-4 py-2 text-sm font-medium transition ${
                  isActive
                    ? 'bg-primary text-white shadow-sm'
                    : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
                }`}
              >
                {tab.label}
              </button>
            );
          })}
        </div>
      </div>

      <div className="rounded-xl border border-gray-200 bg-white p-6 shadow-sm">
        {activeTab === 'reports' && (
          <div className="space-y-4">
            <div className="flex items-center justify-between">
              <h3 className="text-base font-semibold text-gray-900">Згенераваныя справаздачы</h3>
              <button
                type="button"
                onClick={() => setGenerateModalOpen(true)}
                className="rounded-md border border-primary/30 bg-primary/5 px-3 py-1.5 text-sm font-medium text-primary transition hover:bg-primary/10"
              >
                Згенераваць новую
              </button>
            </div>
            <div className="overflow-x-auto">
              {loading ? (
                <div className="py-8">
                  <LoadingSpinner label="Загрузка справаздач..." />
                </div>
              ) : error ? (
                <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
                  {error}
                </div>
              ) : periods.length === 0 ? (
                <p className="py-6 text-sm text-gray-500">Справаздач пакуль няма.</p>
              ) : (
                <div className="space-y-4">
                  {groupedByYear.map(({ year, periods: yearPeriods }) => {
                    const yearClosed = isYearCollapsed(year);

                    return (
                      <div
                        key={year}
                        className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm"
                      >
                        <button
                          type="button"
                          onClick={() => toggleYear(year)}
                          className="flex w-full items-center gap-3 border-b border-gray-200 bg-gray-100 px-4 py-3 text-left transition hover:bg-gray-200/60"
                          aria-expanded={!yearClosed}
                        >
                          {yearClosed ? (
                            <FiChevronRight className="size-4 shrink-0 text-gray-600" aria-hidden />
                          ) : (
                            <FiChevronDown className="size-4 shrink-0 text-gray-600" aria-hidden />
                          )}
                          <span className="rounded-md bg-gray-800 px-2.5 py-0.5 text-sm font-bold tracking-wide text-white">
                            {year}
                          </span>
                          <span className="ml-auto text-xs text-gray-500">
                            {yearPeriods.length}{' '}
                            {yearPeriods.length === 1 ? 'месяц' : 'месяцаў'}
                          </span>
                        </button>

                        {!yearClosed && (
                          <div className="overflow-x-auto">
                            <table className="min-w-full border-collapse text-left text-sm">
                              <thead>
                                <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                                  <th className="px-3 py-2.5">Месяц</th>
                                  <th className="px-3 py-2.5 text-right">VAT да аплаты</th>
                                  <th className="px-3 py-2.5 text-right">Прыбытак</th>
                                  <th className="px-3 py-2.5 text-right">Дзеянне</th>
                                </tr>
                              </thead>
                              <tbody className="divide-y divide-gray-100">
                                {yearPeriods.map((period) => {
                                  const primaryReport =
                                    period.reports.find((r) => r.type === 'poland') ??
                                    period.reports[0];
                                  const actionReportId = primaryReport?.id ?? period.primaryReportId;
                                  const isLocked = period.isLocked;

                                  return (
                                    <tr
                                      key={`${period.periodYear}-${period.periodMonth}`}
                                      className={`cursor-pointer bg-white transition hover:bg-primary/10 ${isLocked ? 'bg-gray-50/80' : ''}`}
                                      onClick={() =>
                                        router.push(`/documents/reports/${period.primaryReportId}`)
                                      }
                                    >
                                      <td className="px-3 py-3 font-medium text-gray-900">
                                        <span className="inline-flex items-center gap-2">
                                          {isLocked && (
                                            <FiLock
                                              className="size-3.5 shrink-0 text-amber-600"
                                              aria-hidden
                                              title="Заблакавана"
                                            />
                                          )}
                                          {formatMonthName(period.periodMonth)}
                                        </span>
                                      </td>
                                      <td className="px-3 py-3 text-right tabular-nums font-semibold text-gray-900">
                                        {formatAmount(period.totalVat)}
                                      </td>
                                      <td className="px-3 py-3 text-right tabular-nums text-gray-700">
                                        {formatAmount(period.profit)}
                                      </td>
                                      <td className="px-3 py-3 text-right">
                                        <div className="inline-flex items-center justify-end gap-1.5">
                                          <button
                                            type="button"
                                            onClick={(e) => {
                                              e.stopPropagation();
                                              void handleToggleLock(period);
                                            }}
                                            disabled={lockingReportId === actionReportId}
                                            className={`inline-flex size-8 items-center justify-center rounded-full border bg-white shadow-sm transition disabled:opacity-60 ${
                                              isLocked
                                                ? 'border-amber-200 text-amber-700 hover:bg-amber-50'
                                                : 'border-gray-200 text-gray-700 hover:border-primary/40 hover:bg-primary/10 hover:text-primary'
                                            }`}
                                            aria-label={
                                              isLocked ? 'Разблакаваць перыяд' : 'Заблакаваць перыяд'
                                            }
                                            title={
                                              isLocked ? 'Разблакаваць перыяд' : 'Заблакаваць перыяд'
                                            }
                                          >
                                            {lockingReportId === actionReportId ? (
                                              <span className="size-3.5 animate-spin rounded-full border-2 border-gray-300 border-t-gray-700" />
                                            ) : isLocked ? (
                                              <FiUnlock className="size-4" aria-hidden />
                                            ) : (
                                              <FiLock className="size-4" aria-hidden />
                                            )}
                                          </button>
                                          <button
                                            type="button"
                                            onClick={(e) => {
                                              e.stopPropagation();
                                              if (isLocked) return;
                                              setPendingRegeneratePeriod(period);
                                            }}
                                            disabled={isLocked || regeneratingId === actionReportId}
                                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-700 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:cursor-not-allowed disabled:opacity-40"
                                            aria-label="Перегенераваць справаздачу"
                                            title={
                                              isLocked
                                                ? 'Справаздача заблакавана'
                                                : 'Перегенераваць справаздачу'
                                            }
                                          >
                                            {regeneratingId === actionReportId ? (
                                              <span className="size-3.5 animate-spin rounded-full border-2 border-gray-300 border-t-gray-700" />
                                            ) : (
                                              <FiRefreshCw className="size-4" aria-hidden />
                                            )}
                                          </button>
                                        </div>
                                      </td>
                                    </tr>
                                  );
                                })}
                              </tbody>
                            </table>
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          </div>
        )}
        {activeTab === 'other' && (
          <div className="rounded-xl border border-dashed border-gray-300 bg-gray-50 px-4 py-6 text-sm text-gray-600">
            Раздзел у распрацоўцы.
          </div>
        )}
      </div>

      {generateModalOpen && (
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
                      {formatMonthName(month)}
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

      {pendingRegeneratePeriod && (
        <div
          className="fixed inset-0 z-[70] flex items-center justify-center bg-black/40 p-4"
          onClick={() => {
            if (regeneratingId !== null) return;
            setPendingRegeneratePeriod(null);
          }}
        >
          <div
            className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="text-base font-semibold text-gray-900">Пацвердзіце перегенерацыю</div>
            <p className="mt-2 text-sm text-gray-600">
              Перагенераваць справаздачу за{' '}
              {formatPeriod(
                pendingRegeneratePeriod.periodMonth,
                pendingRegeneratePeriod.periodYear
              )}
              ?
            </p>
            <div className="mt-5 flex items-center justify-end gap-2">
              <button
                type="button"
                onClick={() => setPendingRegeneratePeriod(null)}
                disabled={regeneratingId !== null}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-60"
              >
                Адмена
              </button>
              <button
                type="button"
                onClick={() => handleRegenerate(pendingRegeneratePeriod)}
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
