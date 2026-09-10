'use client';

import {
  invoiceBukinistkaPosSales,
  type BukinistkaPosSale,
} from '@/lib/api/bukinistka-sales';
import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiX } from 'react-icons/fi';

type IssueBukinistkaInvoiceModalProps = {
  open: boolean;
  sales: BukinistkaPosSale[];
  onClose: () => void;
  onIssued: () => void;
};

function formatPrice(value: number): string {
  if (!Number.isFinite(value)) return '—';
  return value.toLocaleString('be-BY', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
}

function todayLocalDateInput(): string {
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

/** Noon UTC for the chosen calendar day (stable month for VAT period). */
function dateInputToInvoiceUtc(dateInput: string): string {
  const [y, m, d] = dateInput.split('-').map((p) => Number(p));
  if (!y || !m || !d) return new Date().toISOString();
  return new Date(Date.UTC(y, m - 1, d, 12, 0, 0)).toISOString();
}

function canInvoiceSale(row: BukinistkaPosSale): boolean {
  return (
    row.offerId != null &&
    row.offerId > 0 &&
    row.grossUnitCost != null &&
    Number.isFinite(row.grossUnitCost) &&
    row.grossUnitCost > 0
  );
}

export default function IssueBukinistkaInvoiceModal({
  open,
  sales,
  onClose,
  onIssued,
}: IssueBukinistkaInvoiceModalProps) {
  const [mounted, setMounted] = useState(false);
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
  const [invoiceDate, setInvoiceDate] = useState(todayLocalDateInput);
  const [invoiceNumber, setInvoiceNumber] = useState('');
  const [vatRate, setVatRate] = useState<5 | 23>(5);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setMounted(true);
  }, []);

  useEffect(() => {
    if (!open) return;
    setSelectedIds(new Set());
    setInvoiceDate(todayLocalDateInput());
    setInvoiceNumber('');
    setVatRate(5);
    setError(null);
    setSubmitting(false);
  }, [open]);

  const invoiceable = useMemo(() => sales.filter(canInvoiceSale), [sales]);

  const selectedRows = useMemo(
    () => invoiceable.filter((row) => selectedIds.has(row.id)),
    [invoiceable, selectedIds]
  );

  const totalGross = useMemo(
    () =>
      selectedRows.reduce(
        (sum, row) => sum + (row.grossUnitCost ?? 0) * row.quantity,
        0
      ),
    [selectedRows]
  );

  const allSelected =
    invoiceable.length > 0 && selectedRows.length === invoiceable.length;

  if (!open || !mounted) return null;

  const canSubmit =
    !submitting &&
    selectedRows.length > 0 &&
    Boolean(invoiceDate.trim()) &&
    totalGross > 0;

  const toggleId = (id: number) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const toggleAll = () => {
    if (allSelected) {
      setSelectedIds(new Set());
      return;
    }
    setSelectedIds(new Set(invoiceable.map((row) => row.id)));
  };

  const handleSubmit = async () => {
    if (!canSubmit) return;
    setSubmitting(true);
    setError(null);
    try {
      await invoiceBukinistkaPosSales({
        saleIds: selectedRows.map((row) => row.id),
        invoiceDateUtc: dateInputToInvoiceUtc(invoiceDate),
        invoiceNumber: invoiceNumber.trim() || undefined,
        vatRatePercent: vatRate,
      });
      onIssued();
      onClose();
    } catch (err: unknown) {
      setError(
        err instanceof Error ? err.message : 'Не ўдалося выставіць фактуру.'
      );
    } finally {
      setSubmitting(false);
    }
  };

  return createPortal(
    <div
      className="fixed inset-0 z-[90] flex items-end justify-center overflow-y-auto bg-black/40 p-3 sm:items-center sm:p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="buk-invoice-title"
    >
      <div className="flex max-h-[90vh] w-full max-w-2xl flex-col overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl">
        <div className="flex shrink-0 items-start justify-between gap-4 border-b border-gray-100 px-5 py-4">
          <div>
            <h2
              id="buk-invoice-title"
              className="text-lg font-semibold text-gray-900"
            >
              Выставіць фактуру
            </h2>
            <p className="mt-1 text-sm text-gray-600">
              Абярыце продажы. Цана брута — з прапановы. Фактура трапіць у
              польскую VAT-справаздачу за месяц даты фактуры.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            disabled={submitting}
            className="inline-flex size-8 items-center justify-center rounded-lg text-gray-500 transition hover:bg-gray-100 hover:text-gray-800 disabled:opacity-50"
            aria-label="Закрыць"
          >
            <FiX className="size-5" aria-hidden />
          </button>
        </div>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto px-5 py-4">
          <div className="grid gap-3 sm:grid-cols-3">
            <label className="block sm:col-span-1">
              <span className="mb-1.5 block text-sm font-medium text-gray-700">
                Дата фактуры
              </span>
              <input
                type="date"
                value={invoiceDate}
                onChange={(e) => setInvoiceDate(e.target.value)}
                disabled={submitting}
                className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2 disabled:opacity-50"
              />
            </label>
            <label className="block sm:col-span-1">
              <span className="mb-1.5 block text-sm font-medium text-gray-700">
                Нумар (неабавязкова)
              </span>
              <input
                type="text"
                value={invoiceNumber}
                onChange={(e) => setInvoiceNumber(e.target.value)}
                disabled={submitting}
                placeholder="Аўтаматычна"
                className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2 disabled:opacity-50"
              />
            </label>
            <label className="block sm:col-span-1">
              <span className="mb-1.5 block text-sm font-medium text-gray-700">
                VAT
              </span>
              <select
                value={vatRate}
                onChange={(e) =>
                  setVatRate(Number(e.target.value) === 23 ? 23 : 5)
                }
                disabled={submitting}
                className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2 disabled:opacity-50"
              >
                <option value={5}>5%</option>
                <option value={23}>23%</option>
              </select>
            </label>
          </div>

          {invoiceable.length === 0 ? (
            <p className="rounded-lg border border-dashed border-gray-200 bg-gray-50 px-4 py-8 text-center text-sm text-gray-500">
              Няма продажаў з цаной брута з прапановы.
            </p>
          ) : (
            <div className="overflow-hidden rounded-lg border border-gray-200">
              <table className="w-full table-fixed divide-y divide-gray-100 text-left text-sm">
                <thead className="bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <tr>
                    <th className="w-10 px-3 py-2">
                      <input
                        type="checkbox"
                        checked={allSelected}
                        onChange={toggleAll}
                        disabled={submitting}
                        aria-label="Выбраць усе"
                      />
                    </th>
                    <th className="px-3 py-2">Прадукт</th>
                    <th className="w-16 px-3 py-2 text-right">Кол.</th>
                    <th className="w-24 px-3 py-2 text-right">Брута</th>
                    <th className="w-24 px-3 py-2 text-right">Сума</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100 bg-white">
                  {invoiceable.map((row) => {
                    const checked = selectedIds.has(row.id);
                    const line = (row.grossUnitCost ?? 0) * row.quantity;
                    return (
                      <tr key={row.id}>
                        <td className="px-3 py-2">
                          <input
                            type="checkbox"
                            checked={checked}
                            onChange={() => toggleId(row.id)}
                            disabled={submitting}
                            aria-label={`Выбраць ${row.productName}`}
                          />
                        </td>
                        <td className="px-3 py-2 font-medium text-gray-900">
                          <span className="break-words [overflow-wrap:anywhere]">
                            {row.productName}
                          </span>
                        </td>
                        <td className="px-3 py-2 text-right tabular-nums text-gray-700">
                          {row.quantity}
                        </td>
                        <td className="px-3 py-2 text-right tabular-nums text-gray-700">
                          {formatPrice(row.grossUnitCost ?? 0)}
                        </td>
                        <td className="px-3 py-2 text-right tabular-nums text-gray-900">
                          {formatPrice(line)}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}

          <p className="text-sm text-gray-700">
            Абрана: {selectedRows.length} · Сума брута:{' '}
            <span className="font-semibold tabular-nums text-gray-900">
              {formatPrice(totalGross)}
            </span>
          </p>

          {error ? (
            <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
              {error}
            </p>
          ) : null}
        </div>

        <div className="flex shrink-0 items-center justify-end gap-2 border-t border-gray-100 px-5 py-4">
          <button
            type="button"
            onClick={onClose}
            disabled={submitting}
            className="rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-50"
          >
            Скасаваць
          </button>
          <button
            type="button"
            disabled={!canSubmit}
            onClick={() => {
              void handleSubmit();
            }}
            className="inline-flex items-center gap-2 rounded-lg bg-primary px-3 py-2 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-50"
          >
            {submitting ? (
              <span className="size-3.5 animate-spin rounded-full border-2 border-white/30 border-t-white" />
            ) : null}
            Выставіць фактуру
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
