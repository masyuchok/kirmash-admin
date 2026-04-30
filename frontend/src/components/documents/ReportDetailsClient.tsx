'use client';

import { Fragment, useEffect, useMemo, useState } from 'react';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { fetchVatReportDetails } from '@/lib/api/reports';
import type { VatReportDetails } from '@/types/report-details';

function formatAmount(value: number): string {
  return value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function formatDate(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleDateString('ru-RU');
}

export default function ReportDetailsClient({ reportId }: { reportId: number }) {
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [data, setData] = useState<VatReportDetails | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedOrderId, setExpandedOrderId] = useState<string | null>(null);
  const [showAuditDetails, setShowAuditDetails] = useState(false);

  useEffect(() => {
    setTopbarPage({ title: `Справаздача #${reportId}` });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [reportId, setTopbarButtons, setTopbarPage]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchVatReportDetails(reportId)
      .then((res) => {
        if (!cancelled) setData(res);
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err instanceof Error ? err.message : 'Памылка загрузкі справаздачы');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [reportId]);

  const expandedRow = useMemo(
    () => data?.rows.find((row) => row.shopifyOrderId === expandedOrderId) ?? null,
    [data, expandedOrderId]
  );

  if (loading) return <LoadingSpinner label="Загрузка справаздачы..." />;
  if (error) {
    return (
      <div className="mx-auto w-full max-w-6xl rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
        {error}
      </div>
    );
  }
  if (!data) return null;

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 px-6 py-4 text-sm text-gray-600">
          Усяго VAT: <span className="font-semibold text-gray-900">{formatAmount(data.vat)}</span>
        </div>
        <div className="overflow-x-auto">
          <table className="min-w-full border-collapse text-left text-sm">
            <thead>
              <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                <th className="px-4 py-2.5">Тып</th>
                <th className="px-4 py-2.5">Назва</th>
                <th className="px-4 py-2.5 text-right">VAT</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {data.rows.map((row) => (
                <tr
                  key={`${row.type}-${row.shopifyOrderId}`}
                  className={`transition ${row.type === 'poland' ? 'cursor-pointer hover:bg-gray-50' : ''}`}
                  onClick={() => {
                    if (row.type !== 'poland') return;
                    setExpandedOrderId((prev) => (prev === row.shopifyOrderId ? null : row.shopifyOrderId));
                  }}
                >
                  <td className="px-4 py-3">{row.type === 'poland' ? 'Польша' : 'Не Польша'}</td>
                  <td className="px-4 py-3">{row.type === 'poland' ? 'Польша' : row.name}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.vat)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {expandedRow && (
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="flex items-center justify-between border-b border-gray-100 px-6 py-4">
            <h3 className="text-sm font-semibold text-gray-900">Дэталі па Польшчы</h3>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => setShowAuditDetails((prev) => !prev)}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 transition hover:bg-gray-50"
              >
                {showAuditDetails ? 'Схаваць дэталізацыю' : 'Дэталізацыя'}
              </button>
              <button
                type="button"
                onClick={() => window.print()}
                className="rounded-lg border border-primary/30 bg-primary/5 px-3 py-1.5 text-sm font-medium text-primary transition hover:bg-primary/10"
              >
                Экспорт в PDF
              </button>
            </div>
          </div>
          <div className="overflow-x-auto">
            <table className="min-w-full border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="px-4 py-2.5">Номер заказа</th>
                  <th className="px-4 py-2.5">Дата</th>
                  <th className="px-4 py-2.5 text-right">Ставка VAT</th>
                  <th className="px-4 py-2.5 text-right">Сумма брутто</th>
                  <th className="px-4 py-2.5 text-right">VAT</th>
                  <th className="px-4 py-2.5 text-right">Сумма нетто</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {expandedRow.polandRows.map((row, idx) => (
                  <Fragment key={`${row.orderNumber}-${idx}`}>
                    <tr>
                      <td className="px-4 py-3">{row.orderNumber}</td>
                      <td className="px-4 py-3">{formatDate(row.orderDateUtc)}</td>
                      <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.vatRatePercent)}%</td>
                      <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.grossAmount)}</td>
                      <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.vatAmount)}</td>
                      <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.netAmount)}</td>
                    </tr>
                    {showAuditDetails && row.items.length > 0 && (
                      <tr className="bg-gray-50/50">
                        <td className="px-4 py-2 text-xs text-gray-500" colSpan={6}>
                          {row.items.map((item, itemIdx) => (
                            <div key={`${item.productTitle}-${itemIdx}`} className="py-0.5">
                              {item.productTitle} · qty {item.quantity} · type: {item.productType || '—'} · VAT{' '}
                              {formatAmount(item.assignedVatRatePercent)}% · reason: {item.assignmentReason}
                            </div>
                          ))}
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}
