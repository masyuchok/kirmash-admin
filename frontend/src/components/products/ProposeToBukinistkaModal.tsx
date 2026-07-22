'use client';

import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiX } from 'react-icons/fi';

export type ProposeToBukinistkaDraft = {
  productLabel: string;
  quantity: number;
  grossUnitCost: number;
};

type ProposeToBukinistkaModalProps = {
  open: boolean;
  draft: ProposeToBukinistkaDraft | null;
  submitting: boolean;
  error: string | null;
  title?: string;
  submitLabel?: string;
  onClose: () => void;
  onSubmit: (quantity: number, grossUnitCost: number) => void;
};

export default function ProposeToBukinistkaModal({
  open,
  draft,
  submitting,
  error,
  title = 'Прапанаваць у Букіністыку',
  submitLabel = 'Даслаць прапанову',
  onClose,
  onSubmit,
}: ProposeToBukinistkaModalProps) {
  const [mounted, setMounted] = useState(false);
  const [quantity, setQuantity] = useState('1');
  const [gross, setGross] = useState('0');

  useEffect(() => {
    setMounted(true);
  }, []);

  useEffect(() => {
    if (!open || !draft) return;
    setQuantity(String(draft.quantity));
    setGross(
      Number.isFinite(draft.grossUnitCost) ? String(draft.grossUnitCost) : '0'
    );
  }, [open, draft]);

  if (!open || !mounted || !draft) return null;

  const parsedQty = Number.parseInt(quantity, 10);
  const parsedGross = Number.parseFloat(gross.replace(',', '.'));
  const canSubmit =
    !submitting &&
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
      onClick={onClose}
    >
      <div
        className="w-full max-w-md overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
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
              onChange={(e) => setQuantity(e.target.value)}
              className="w-full rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2"
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
            onClick={() => onSubmit(parsedQty, parsedGross)}
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
