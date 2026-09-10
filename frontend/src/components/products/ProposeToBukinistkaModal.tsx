'use client';

import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiX } from 'react-icons/fi';

export type ProposeToBukinistkaDraft = {
  productLabel: string;
  quantity: number;
  grossUnitCost: number;
  syncOnSale?: boolean;
};

type ProposeToBukinistkaModalProps = {
  open: boolean;
  draft: ProposeToBukinistkaDraft | null;
  submitting: boolean;
  error: string | null;
  title?: string;
  submitLabel?: string;
  /** When false, hide SyncOnSale checkbox (e.g. edit pending offer). Default true. */
  showSyncOnSale?: boolean;
  /** When true, quantity is shown but not editable (accepted offer cost update). */
  quantityLocked?: boolean;
  /** Blocks submit (e.g. eligibility failed) while still showing the form. */
  submitDisabled?: boolean;
  onClose: () => void;
  onSubmit: (
    quantity: number,
    grossUnitCost: number,
    syncOnSale: boolean
  ) => void;
};

export default function ProposeToBukinistkaModal({
  open,
  draft,
  submitting,
  error,
  title = 'Прапанаваць у Букіністыку',
  submitLabel = 'Даслаць прапанову',
  showSyncOnSale = true,
  quantityLocked = false,
  submitDisabled = false,
  onClose,
  onSubmit,
}: ProposeToBukinistkaModalProps) {
  const [mounted, setMounted] = useState(false);
  const [quantity, setQuantity] = useState('1');
  const [gross, setGross] = useState('0');
  const [syncOnSale, setSyncOnSale] = useState(true);

  useEffect(() => {
    setMounted(true);
  }, []);

  useEffect(() => {
    if (!open || !draft) return;
    setQuantity(String(draft.quantity));
    setGross(
      Number.isFinite(draft.grossUnitCost) ? String(draft.grossUnitCost) : '0'
    );
    setSyncOnSale(draft.syncOnSale ?? true);
  }, [open, draft]);

  if (!open || !mounted || !draft) return null;

  const parsedQty = Number.parseInt(quantity, 10);
  const parsedGross = Number.parseFloat(gross.replace(',', '.'));
  const canSubmit =
    !submitting &&
    !submitDisabled &&
    Number.isFinite(parsedQty) &&
    parsedQty > 0 &&
    Number.isFinite(parsedGross) &&
    parsedGross >= 0;

  return createPortal(
    <div
      className="fixed inset-0 z-[90] flex items-end justify-center overflow-y-auto bg-black/40 p-3 sm:items-center sm:p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="propose-bukinistka-title"
    >
      <div className="w-full max-w-md overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl">
        <div className="flex items-start justify-between gap-4 border-b border-gray-100 px-5 py-4">
          <div>
            <h2
              id="propose-bukinistka-title"
              className="text-lg font-semibold text-gray-900"
            >
              {title}
            </h2>
            <p className="mt-1 text-sm text-gray-600">{draft.productLabel}</p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="inline-flex size-8 items-center justify-center rounded-lg text-gray-500 transition hover:bg-gray-100 hover:text-gray-800"
            aria-label="Закрыць"
          >
            <FiX className="size-5" aria-hidden />
          </button>
        </div>

        <div className="space-y-4 px-5 py-4">
          <label className="block">
            <span className="mb-1.5 block text-sm font-medium text-gray-700">
              Колькасць
            </span>
            <input
              type="number"
              min={1}
              step={1}
              value={quantity}
              readOnly={quantityLocked}
              disabled={quantityLocked}
              onChange={(e) => setQuantity(e.target.value)}
              className="w-full rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2 disabled:bg-gray-50 disabled:text-gray-500"
            />
          </label>
          <label className="block">
            <span className="mb-1.5 block text-sm font-medium text-gray-700">
              Кошт брута
            </span>
            <input
              type="number"
              min={0}
              step="0.01"
              value={gross}
              onChange={(e) => setGross(e.target.value)}
              className="w-full rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2"
            />
          </label>
          {showSyncOnSale ? (
            <label className="flex cursor-pointer items-start gap-3 rounded-lg border border-gray-200 bg-gray-50 px-3 py-3">
              <input
                type="checkbox"
                checked={syncOnSale}
                onChange={(e) => setSyncOnSale(e.target.checked)}
                className="mt-0.5 size-4 rounded border-gray-300 text-primary focus:ring-primary"
              />
              <span>
                <span className="block text-sm font-medium text-gray-900">
                  Сінхранізаваць пры продажы
                </span>
                <span className="mt-0.5 block text-xs text-gray-500">
                  Пасля прыёмкі продаж у Shopify створыць Wydanie ў Odoo на
                  адрас Kirma.sh.
                </span>
              </span>
            </label>
          ) : null}
          {error ? (
            <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
              {error}
            </p>
          ) : null}
        </div>

        <div className="flex items-center justify-end gap-2 border-t border-gray-100 px-5 py-4">
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
            onClick={() => onSubmit(parsedQty, parsedGross, syncOnSale)}
            className="inline-flex items-center gap-2 rounded-lg bg-primary px-3 py-2 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-50"
          >
            {submitting ? (
              <span className="size-3.5 animate-spin rounded-full border-2 border-white/30 border-t-white" />
            ) : null}
            {submitLabel}
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
