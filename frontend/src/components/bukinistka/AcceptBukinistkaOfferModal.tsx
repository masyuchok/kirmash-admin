'use client';

import {
  fetchBukinistkaProducts,
  type BukinistkaProduct,
} from '@/lib/api/bukinistka-products';
import type { KirmaBukinistkaOffer } from '@/lib/api/bukinistka-offers';
import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiSearch, FiX } from 'react-icons/fi';

type AcceptMode = 'link' | 'create';
type CostChoice = 'kirma' | 'keep' | null;

type AcceptBukinistkaOfferModalProps = {
  open: boolean;
  offer: KirmaBukinistkaOffer | null;
  submitting: boolean;
  error: string | null;
  onClose: () => void;
  onSubmit: (input: {
    odooProductId: number;
    odooProductName: string;
    listPrice?: number | null;
    applyKirmaCostPrice?: boolean | null;
  }) => void;
};

function formatPrice(value: number): string {
  if (!Number.isFinite(value)) return '—';
  return value.toLocaleString('be-BY', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
}

function roundMoney(value: number): number {
  return Math.round(value * 100) / 100;
}

export default function AcceptBukinistkaOfferModal({
  open,
  offer,
  submitting,
  error,
  onClose,
  onSubmit,
}: AcceptBukinistkaOfferModalProps) {
  const [mounted, setMounted] = useState(false);
  const [mode, setMode] = useState<AcceptMode>('link');
  const [products, setProducts] = useState<BukinistkaProduct[]>([]);
  const [productsLoading, setProductsLoading] = useState(false);
  const [productsError, setProductsError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [listPriceInput, setListPriceInput] = useState('');
  const [costChoice, setCostChoice] = useState<CostChoice>(null);

  useEffect(() => {
    setMounted(true);
  }, []);

  useEffect(() => {
    if (!open || !offer) return;
    setMode('link');
    setSearchQuery('');
    setSelectedId(null);
    setListPriceInput('');
    setCostChoice(null);
    setProductsError(null);

    let cancelled = false;
    setProductsLoading(true);
    fetchBukinistkaProducts()
      .then((rows) => {
        if (!cancelled) setProducts(rows);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setProductsError(
            err instanceof Error
              ? err.message
              : 'Не ўдалося загрузіць прадукты Odoo.'
          );
        }
      })
      .finally(() => {
        if (!cancelled) setProductsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [open, offer]);

  const selected = useMemo(
    () => products.find((p) => p.id === selectedId) ?? null,
    [products, selectedId]
  );

  useEffect(() => {
    if (!selected) {
      setListPriceInput('');
      setCostChoice(null);
      return;
    }
    setListPriceInput(
      Number.isFinite(selected.listPrice) ? String(selected.listPrice) : ''
    );
    setCostChoice(null);
  }, [selected]);

  const filtered = useMemo(() => {
    const search = searchQuery.trim().toLowerCase();
    if (!search) return products.slice(0, 80);
    return products
      .filter((row) => {
        const haystack = [
          row.name,
          row.defaultCode ?? '',
          row.barcode ?? '',
          row.supplierName ?? '',
        ]
          .join(' ')
          .toLowerCase();
        return haystack.includes(search);
      })
      .slice(0, 80);
  }, [products, searchQuery]);

  if (!open || !mounted || !offer) return null;

  const offerCost = roundMoney(offer.grossUnitCost);
  const odooCost = selected ? roundMoney(selected.standardPrice) : 0;
  const costDiffers = selected != null && offerCost !== odooCost;

  const parsedPrice = Number.parseFloat(listPriceInput.replace(',', '.'));
  const priceValid =
    listPriceInput.trim() === '' ||
    (Number.isFinite(parsedPrice) && parsedPrice >= 0);
  const costChoiceOk = !costDiffers || costChoice != null;
  const canSubmit =
    !submitting &&
    mode === 'link' &&
    selectedId != null &&
    selectedId > 0 &&
    priceValid &&
    costChoiceOk;

  const handleSubmit = () => {
    if (!selected || !canSubmit) return;
    const current = roundMoney(selected.listPrice);
    const next = listPriceInput.trim() === '' ? null : roundMoney(parsedPrice);
    const listPrice = next === null || next === current ? undefined : next;
    onSubmit({
      odooProductId: selected.id,
      odooProductName: selected.name,
      listPrice,
      applyKirmaCostPrice: costDiffers ? costChoice === 'kirma' : undefined,
    });
  };

  return createPortal(
    <div
      className="fixed inset-0 z-[90] flex items-end justify-center overflow-y-auto bg-black/40 p-3 sm:items-center sm:p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="accept-bukinistka-title"
      onClick={() => {
        if (!submitting) onClose();
      }}
    >
      <div
        className="flex max-h-[90vh] w-full max-w-xl flex-col overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex shrink-0 items-start justify-between gap-4 border-b border-gray-100 px-5 py-4">
          <div>
            <h2
              id="accept-bukinistka-title"
              className="text-lg font-semibold text-gray-900"
            >
              Прыняць прапанову
            </h2>
            <p className="mt-1 text-sm text-gray-600">
              {offer.productName}
              {offer.productAuthor.trim()
                ? ` — ${offer.productAuthor.trim()}`
                : ''}
            </p>
            <p className="mt-0.5 text-xs text-gray-500">
              Колькасць з прапановы: {offer.quantity} · Кошт брута:{' '}
              {formatPrice(offer.grossUnitCost)}
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

        <div className="flex shrink-0 gap-2 border-b border-gray-100 px-5 py-3">
          <button
            type="button"
            onClick={() => setMode('link')}
            className={`rounded-lg px-3 py-1.5 text-sm font-medium transition ${
              mode === 'link'
                ? 'bg-amber-50 text-amber-900'
                : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
            }`}
          >
            Звязаць з існуючым
          </button>
          <button
            type="button"
            disabled
            title="Хутка"
            className="cursor-not-allowed rounded-lg px-3 py-1.5 text-sm font-medium text-gray-400"
          >
            Дадаць новы
          </button>
        </div>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto px-5 py-4">
          {mode === 'link' ? (
            <>
              <div className="relative">
                <FiSearch
                  className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-gray-400"
                  aria-hidden
                />
                <input
                  type="search"
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  placeholder="Пошук па назве, кодзе, штрихкодзе…"
                  className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-3 text-sm text-gray-900 outline-none ring-amber-500/30 focus:border-amber-500 focus:ring-2"
                />
              </div>

              {productsLoading ? (
                <p className="py-8 text-center text-sm text-gray-500">
                  Загрузка прадуктаў Odoo…
                </p>
              ) : productsError ? (
                <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
                  {productsError}
                </p>
              ) : filtered.length === 0 ? (
                <p className="py-8 text-center text-sm text-gray-500">
                  Нічога не знойдзена.
                </p>
              ) : (
                <ul className="max-h-56 divide-y divide-gray-100 overflow-y-auto rounded-lg border border-gray-200">
                  {filtered.map((row) => {
                    const active = selectedId === row.id;
                    return (
                      <li key={row.id}>
                        <button
                          type="button"
                          onClick={() => setSelectedId(row.id)}
                          className={`flex w-full flex-col gap-0.5 px-3 py-2.5 text-left transition ${
                            active ? 'bg-amber-50' : 'bg-white hover:bg-gray-50'
                          }`}
                        >
                          <span className="text-sm font-medium text-gray-900">
                            {row.name}
                          </span>
                          <span className="text-xs text-gray-500">
                            У наяўнасці: {row.quantityInStock}
                            {' · '}
                            Кошт: {formatPrice(row.standardPrice)}
                            {' · '}
                            Цана: {formatPrice(row.listPrice)}
                            {row.defaultCode
                              ? ` · Код: ${row.defaultCode}`
                              : ''}
                          </span>
                        </button>
                      </li>
                    );
                  })}
                </ul>
              )}

              {selected ? (
                <div className="space-y-3 rounded-lg border border-gray-200 bg-gray-50 px-3 py-3">
                  <p className="text-sm text-gray-700">
                    Абрана:{' '}
                    <span className="font-medium text-gray-900">
                      {selected.name}
                    </span>
                  </p>
                  <p className="text-xs text-gray-500">
                    Зараз у Odoo: {selected.quantityInStock} шт. Пасля прыняцця
                    будзе {selected.quantityInStock + offer.quantity} шт.
                  </p>

                  {costDiffers ? (
                    <div
                      className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-3"
                      role="status"
                    >
                      <p className="text-sm font-medium text-amber-950">
                        Кошт у Odoo адрозніваецца ад прапановы Кирмаша
                      </p>
                      <p className="mt-1 text-sm text-amber-900">
                        У Odoo (поле «Кошт»):{' '}
                        <span className="font-semibold tabular-nums">
                          {formatPrice(odooCost)}
                        </span>
                        . Ад Кирмаша (кошт брута):{' '}
                        <span className="font-semibold tabular-nums">
                          {formatPrice(offerCost)}
                        </span>
                        .
                      </p>
                      <fieldset className="mt-3 space-y-2">
                        <legend className="sr-only">Выбар кошту закупкі</legend>
                        <label className="flex cursor-pointer items-start gap-2 text-sm text-amber-950">
                          <input
                            type="radio"
                            name="cost-choice"
                            className="mt-0.5"
                            checked={costChoice === 'kirma'}
                            onChange={() => setCostChoice('kirma')}
                          />
                          <span>
                            Устанавіць кошт закупкі ад Кирмаша (
                            {formatPrice(offerCost)})
                          </span>
                        </label>
                        <label className="flex cursor-pointer items-start gap-2 text-sm text-amber-950">
                          <input
                            type="radio"
                            name="cost-choice"
                            className="mt-0.5"
                            checked={costChoice === 'keep'}
                            onChange={() => setCostChoice('keep')}
                          />
                          <span>
                            Пакінуць кошт у Odoo ({formatPrice(odooCost)})
                          </span>
                        </label>
                      </fieldset>
                    </div>
                  ) : null}

                  <label className="block">
                    <span className="mb-1.5 block text-sm font-medium text-gray-700">
                      Новая цана продажу
                    </span>
                    <input
                      type="number"
                      min={0}
                      step="0.01"
                      value={listPriceInput}
                      onChange={(e) => setListPriceInput(e.target.value)}
                      className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 outline-none ring-amber-500/30 focus:border-amber-500 focus:ring-2"
                    />
                    <span className="mt-1 block text-xs text-gray-500">
                      Пакіньце як ёсць, калі цану мяняць не трэба.
                    </span>
                  </label>
                </div>
              ) : null}
            </>
          ) : null}

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
            onClick={handleSubmit}
            className="inline-flex items-center gap-2 rounded-lg bg-amber-700 px-3 py-2 text-sm font-medium text-white transition hover:bg-amber-800 disabled:opacity-50"
          >
            {submitting ? (
              <span className="size-3.5 animate-spin rounded-full border-2 border-white/30 border-t-white" />
            ) : null}
            Дадаць у прыёмку
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
