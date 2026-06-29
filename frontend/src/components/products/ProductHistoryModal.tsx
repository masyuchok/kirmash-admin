'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiX } from 'react-icons/fi';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import type {
  ProductHistory,
  ProductHistoryPaymentEvent,
  ProductHistorySaleEvent,
  ProductHistorySupplyEvent,
} from '@/types/product-history';

type ProductHistoryModalProps = {
  open: boolean;
  loading: boolean;
  error: string | null;
  history: ProductHistory | null;
  subtitle?: string;
  onClose: () => void;
};

function formatDate(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleDateString('be-BY', { timeZone: 'Europe/Warsaw' });
}

function formatVariantSuffix(event: { variantTitle: string }): string {
  const title = event.variantTitle.trim();
  return title ? ` · ${title}` : '';
}

function groupSuppliesBySupplier(
  supplies: ProductHistorySupplyEvent[]
): { supplierName: string; events: ProductHistorySupplyEvent[] }[] {
  const map = new Map<string, ProductHistorySupplyEvent[]>();
  for (const event of supplies) {
    const key = event.supplierName.trim() || '—';
    const list = map.get(key) ?? [];
    list.push(event);
    map.set(key, list);
  }
  return Array.from(map.entries())
    .sort(([a], [b]) => a.localeCompare(b, 'be'))
    .map(([supplierName, events]) => ({
      supplierName,
      events: events.sort((x, y) => y.date.localeCompare(x.date) || y.supplyId - x.supplyId),
    }));
}

function SaleSourceLabel({ sale }: { sale: ProductHistorySaleEvent }) {
  if (sale.source === 'cash') {
    return <span className="text-gray-600">Гатоўка</span>;
  }
  if (sale.orderNumber.trim()) {
    return <span className="text-gray-600">Заказ {sale.orderNumber}</span>;
  }
  return <span className="text-gray-600">Заказ</span>;
}

export default function ProductHistoryModal({
  open,
  loading,
  error,
  history,
  subtitle,
  onClose,
}: ProductHistoryModalProps) {
  const [mounted, setMounted] = useState(false);

  useEffect(() => {
    setMounted(true);
  }, []);

  if (!open || !mounted) return null;

  const supplyGroups = history ? groupSuppliesBySupplier(history.supplies) : [];

  return createPortal(
    <div
      className="fixed inset-0 z-[90] flex items-end justify-center overflow-y-auto bg-black/40 p-3 sm:items-center sm:p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="product-history-title"
      onClick={onClose}
    >
      <div
        className="flex max-h-[min(90vh,720px)] w-full max-w-2xl flex-col overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-4 border-b border-gray-100 px-5 py-4">
          <div>
            <h2 id="product-history-title" className="text-lg font-semibold text-gray-900">
              Гісторыя прадукту
            </h2>
            {history && (
              <p className="mt-1 text-sm text-gray-700">
                {history.productName}
                {subtitle ? <span className="text-gray-500"> · {subtitle}</span> : null}
              </p>
            )}
          </div>
          <button
            type="button"
            onClick={onClose}
            className="inline-flex size-8 shrink-0 items-center justify-center rounded-lg text-gray-500 transition hover:bg-gray-100 hover:text-gray-700"
            aria-label="Закрыць"
          >
            <FiX className="size-5" />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-4">
          {loading && <LoadingSpinner label="Загрузка гісторыі..." />}
          {!loading && error && (
            <p className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">{error}</p>
          )}
          {!loading && !error && history && (
            <div className="space-y-6">
              <section>
                <h3 className="text-xs font-semibold uppercase tracking-wide text-gray-500">Пастаўкі</h3>
                {supplyGroups.length === 0 ? (
                  <p className="mt-2 text-sm text-gray-500">Паставак пакуль няма.</p>
                ) : (
                  <div className="mt-3 space-y-4">
                    {supplyGroups.map((group) => (
                      <div key={group.supplierName}>
                        <p className="text-sm font-medium text-gray-900">{group.supplierName}</p>
                        <ul className="mt-1.5 divide-y divide-gray-100 rounded-lg border border-gray-100">
                          {group.events.map((event) => (
                            <li
                              key={`${event.supplyId}-${event.shopifyVariantId}-${event.date}-${event.quantity}`}
                              className="flex flex-wrap items-baseline justify-between gap-2 px-3 py-2 text-sm"
                            >
                              <span className="text-gray-700">
                                {formatDate(event.date)}
                                {formatVariantSuffix(event)}
                              </span>
                              <span className="tabular-nums font-medium text-gray-900">+{event.quantity}</span>
                            </li>
                          ))}
                        </ul>
                      </div>
                    ))}
                  </div>
                )}
              </section>

              <section>
                <h3 className="text-xs font-semibold uppercase tracking-wide text-gray-500">Продажы</h3>
                {history.sales.length === 0 ? (
                  <p className="mt-2 text-sm text-gray-500">Продажаў пакуль няма.</p>
                ) : (
                  <ul className="mt-3 divide-y divide-gray-100 rounded-lg border border-gray-100">
                    {history.sales.map((sale, index) => (
                      <li
                        key={`${sale.dateUtc}-${sale.source}-${sale.orderNumber}-${sale.quantity}-${index}`}
                        className="flex flex-wrap items-baseline justify-between gap-2 px-3 py-2 text-sm"
                      >
                        <div className="space-y-0.5">
                          <span className="text-gray-700">
                            {formatDate(sale.dateUtc)}
                            {formatVariantSuffix(sale)}
                          </span>
                          <div className="text-xs">
                            <SaleSourceLabel sale={sale} />
                            {sale.reportId != null && (
                              <>
                                {' · '}
                                <Link
                                  href={`/documents/reports/${sale.reportId}`}
                                  className="text-primary hover:underline"
                                >
                                  справаздача
                                </Link>
                              </>
                            )}
                          </div>
                        </div>
                        <span className="tabular-nums font-medium text-gray-900">−{sale.quantity}</span>
                      </li>
                    ))}
                  </ul>
                )}
              </section>

              <section>
                <h3 className="text-xs font-semibold uppercase tracking-wide text-gray-500">
                  Аплата пастаўшчыку
                </h3>
                {history.payments.length === 0 ? (
                  <p className="mt-2 text-sm text-gray-500">Аплат пакуль няма.</p>
                ) : (
                  <ul className="mt-3 divide-y divide-gray-100 rounded-lg border border-gray-100">
                    {history.payments.map((payment, index) => (
                      <li
                        key={`${payment.expenseId}-${payment.dateUtc}-${payment.quantity}-${index}`}
                        className="flex flex-wrap items-baseline justify-between gap-2 px-3 py-2 text-sm"
                      >
                        <div className="space-y-0.5">
                          <span className="text-gray-700">
                            {formatDate(payment.dateUtc)}
                            {formatVariantSuffix(payment)}
                          </span>
                          <div className="text-xs text-gray-600">
                            {payment.supplierName.trim() || '—'}
                            {payment.invoiceNumber.trim() ? ` · ${payment.invoiceNumber}` : ''}
                            {' · '}
                            <Link
                              href={`/documents/reports/${payment.reportId}`}
                              className="text-primary hover:underline"
                            >
                              справаздача
                            </Link>
                          </div>
                        </div>
                        <span className="tabular-nums font-medium text-gray-900">{payment.quantity} адз.</span>
                      </li>
                    ))}
                  </ul>
                )}
              </section>
            </div>
          )}
        </div>
      </div>
    </div>,
    document.body
  );
}
