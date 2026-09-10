'use client';

import {
  createKirmaShopifyProduct,
  fetchKirmaCreateShopifyProductPreview,
  KirmaCreateShopifyProductError,
  type KirmaBukinistkaOffer,
  type KirmaCreateShopifyProductPreview,
} from '@/lib/api/bukinistka-offers';
import { fetchProductsWithSuppliers } from '@/lib/api/products';
import {
  calcNetUnitPriceFromGross,
  recalcMarginBySaleGross,
} from '@/lib/suppliers/inventoryPricing';
import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiSearch, FiX } from 'react-icons/fi';

type AcceptMode = 'link' | 'create';
type BarcodeConflictChoice = 'clear' | 'manual' | null;

type ProductOption = {
  shopifyProductId: string;
  label: string;
  salePrice: number;
  quantityInStock: number;
};

type Props = {
  open: boolean;
  offer: KirmaBukinistkaOffer | null;
  submitting: boolean;
  error: string | null;
  onClose: () => void;
  onSubmit: (input: {
    shopifyProductId: string;
    shopifyVariantId?: string;
    salePrice?: number | null;
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

function digitsOnly(value: string): string {
  return value.replace(/\D/g, '');
}

function isBarcodeConflictError(err: unknown): boolean {
  if (
    err instanceof KirmaCreateShopifyProductError &&
    err.code === 'barcode_conflict'
  ) {
    return true;
  }
  const message =
    err instanceof Error ? err.message : typeof err === 'string' ? err : '';
  const m = message.toLowerCase();
  return (
    m.includes('barcode_conflict') ||
    m.includes('barcode') ||
    m.includes('already') ||
    m.includes('taken') ||
    m.includes('unique')
  );
}

function MarginHint({
  grossCost,
  saleGross,
  vatRate,
}: {
  grossCost: number;
  saleGross: number | null;
  vatRate: 5 | 23;
}) {
  if (saleGross == null || saleGross <= 0 || grossCost <= 0) return null;
  const netCost = calcNetUnitPriceFromGross(grossCost, vatRate);
  const { marginPercent, saleNet } = recalcMarginBySaleGross(
    netCost,
    saleGross,
    vatRate
  );
  return (
    <p className="mt-1 text-xs text-gray-600">
      Маржа:{' '}
      <span className="font-semibold tabular-nums text-gray-900">
        {marginPercent}%
      </span>
      {' · '}
      нет {formatPrice(saleNet - netCost)} з {formatPrice(saleNet)}
    </p>
  );
}

export default function AcceptReceivedOfferModal({
  open,
  offer,
  submitting,
  error,
  onClose,
  onSubmit,
}: Props) {
  const [mounted, setMounted] = useState(false);
  const [mode, setMode] = useState<AcceptMode>('link');
  const [vatRate, setVatRate] = useState<5 | 23>(5);
  const [products, setProducts] = useState<ProductOption[]>([]);
  const [productsLoading, setProductsLoading] = useState(false);
  const [productsError, setProductsError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedId, setSelectedId] = useState('');
  const [salePriceInput, setSalePriceInput] = useState('');
  const [createPreview, setCreatePreview] =
    useState<KirmaCreateShopifyProductPreview | null>(null);
  const [createPreviewLoading, setCreatePreviewLoading] = useState(false);
  const [createPreviewError, setCreatePreviewError] = useState<string | null>(
    null
  );
  const [createSalePriceInput, setCreateSalePriceInput] = useState('');
  const [creatingProduct, setCreatingProduct] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [barcodeConflict, setBarcodeConflict] = useState(false);
  const [barcodeChoice, setBarcodeChoice] =
    useState<BarcodeConflictChoice>(null);
  const [manualBarcodeInput, setManualBarcodeInput] = useState('');

  useEffect(() => {
    setMounted(true);
  }, []);

  useEffect(() => {
    if (!open || !offer) return;
    setMode('link');
    setVatRate(5);
    setSearchQuery('');
    setSelectedId(offer.shopifyProductId || '');
    setSalePriceInput('');
    setProductsError(null);
    setCreatePreview(null);
    setCreatePreviewError(null);
    setCreateSalePriceInput('');
    setCreateError(null);
    setCreatingProduct(false);
    setBarcodeConflict(false);
    setBarcodeChoice(null);
    setManualBarcodeInput('');

    let cancelled = false;
    setProductsLoading(true);
    setCreatePreviewLoading(true);

    const productsPromise = fetchProductsWithSuppliers()
      .then((rows) => {
        if (cancelled) return;
        const options: ProductOption[] = rows.map((row) => ({
          shopifyProductId: row.shopifyProductId,
          label: [row.productName, row.productAuthor]
            .filter(Boolean)
            .join(', '),
          salePrice: row.shopifySalePrice,
          quantityInStock: row.shopifyQuantityInStock,
        }));
        setProducts(options);
        if (!offer.shopifyProductId && options.length === 1) {
          setSelectedId(options[0].shopifyProductId);
        }
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setProductsError(
            err instanceof Error
              ? err.message
              : 'Не ўдалося загрузіць прадукты Shopify.'
          );
        }
      })
      .finally(() => {
        if (!cancelled) setProductsLoading(false);
      });

    const previewPromise = fetchKirmaCreateShopifyProductPreview(offer.id)
      .then((preview) => {
        if (cancelled) return;
        setCreatePreview(preview);
        const suggested =
          preview.odooListPrice != null &&
          Number.isFinite(preview.odooListPrice)
            ? preview.odooListPrice
            : null;
        setCreateSalePriceInput(suggested != null ? String(suggested) : '');
        setManualBarcodeInput(preview.barcodeDigits ?? '');
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setCreatePreview(null);
          setCreatePreviewError(
            err instanceof Error
              ? err.message
              : 'Не ўдалося загрузіць даныя з Odoo.'
          );
        }
      })
      .finally(() => {
        if (!cancelled) setCreatePreviewLoading(false);
      });

    void productsPromise;
    void previewPromise;

    return () => {
      cancelled = true;
    };
  }, [open, offer]);

  useEffect(() => {
    if (!open || !offer || mode !== 'create') return;

    setCreateError(null);
    setBarcodeConflict(false);
    setBarcodeChoice(null);
    if (createPreview?.barcodeDigits) {
      setManualBarcodeInput(createPreview.barcodeDigits);
    }
  }, [open, offer, mode, createPreview?.barcodeDigits]);

  const selected = useMemo(
    () => products.find((p) => p.shopifyProductId === selectedId) ?? null,
    [products, selectedId]
  );

  useEffect(() => {
    if (!selected) {
      setSalePriceInput('');
      return;
    }
    setSalePriceInput(
      Number.isFinite(selected.salePrice) && selected.salePrice > 0
        ? String(selected.salePrice)
        : ''
    );
  }, [selected]);

  const filtered = useMemo(() => {
    const search = searchQuery.trim().toLowerCase();
    if (!search) return products.slice(0, 80);
    return products
      .filter(
        (p) =>
          p.label.toLowerCase().includes(search) ||
          p.shopifyProductId.toLowerCase().includes(search)
      )
      .slice(0, 80);
  }, [products, searchQuery]);

  if (!open || !mounted || !offer) return null;

  const offerCost = roundMoney(offer.grossUnitCost);
  const parsedLinkPrice = Number.parseFloat(salePriceInput.replace(',', '.'));
  const linkPriceValid =
    salePriceInput.trim() !== '' &&
    Number.isFinite(parsedLinkPrice) &&
    parsedLinkPrice >= 0;

  const parsedCreatePrice = Number.parseFloat(
    createSalePriceInput.replace(',', '.')
  );
  const createPriceValid =
    createSalePriceInput.trim() !== '' &&
    Number.isFinite(parsedCreatePrice) &&
    parsedCreatePrice >= 0;

  const manualBarcodeDigits = digitsOnly(manualBarcodeInput);
  const barcodeConflictResolved =
    !barcodeConflict ||
    barcodeChoice === 'clear' ||
    (barcodeChoice === 'manual' && manualBarcodeDigits.length > 0);

  const busy = submitting || creatingProduct;
  const canSubmitLink =
    !busy && mode === 'link' && selectedId.trim().length > 0 && linkPriceValid;
  const canSubmitCreate =
    !busy &&
    mode === 'create' &&
    !createPreviewLoading &&
    createPreview != null &&
    createPriceValid &&
    barcodeConflictResolved;
  const canSubmit = mode === 'link' ? canSubmitLink : canSubmitCreate;

  const resolveLinkSalePrice = (): number | null | undefined => {
    if (!selected || !linkPriceValid) return undefined;
    const next = roundMoney(parsedLinkPrice);
    const current = roundMoney(selected.salePrice);
    return next === current ? undefined : next;
  };

  const handleSubmitLink = () => {
    if (!selected || !canSubmitLink) return;
    onSubmit({
      shopifyProductId: selected.shopifyProductId,
      salePrice: resolveLinkSalePrice(),
    });
  };

  const handleSubmitCreate = async () => {
    if (!canSubmitCreate) return;
    setCreatingProduct(true);
    setCreateError(null);
    try {
      const created = await createKirmaShopifyProduct(
        offer.id,
        roundMoney(parsedCreatePrice),
        barcodeConflict
          ? barcodeChoice === 'clear'
            ? { omitBarcode: true }
            : { barcodeDigits: manualBarcodeDigits }
          : undefined
      );
      onSubmit({
        shopifyProductId: created.shopifyProductId,
        shopifyVariantId: created.shopifyVariantId,
        salePrice: undefined,
      });
    } catch (err: unknown) {
      if (isBarcodeConflictError(err)) {
        setBarcodeConflict(true);
        setBarcodeChoice(null);
        setManualBarcodeInput(createPreview?.barcodeDigits ?? '');
        setCreateError(
          err instanceof Error
            ? err.message.replace(/^barcode_conflict:\s*/i, '')
            : 'Штрихкод ужо заняты ў Shopify.'
        );
      } else {
        setBarcodeConflict(false);
        setCreateError(
          err instanceof Error
            ? err.message
            : 'Не ўдалося стварыць картку ў Shopify.'
        );
      }
    } finally {
      setCreatingProduct(false);
    }
  };

  const handleSubmit = () => {
    if (mode === 'create') {
      void handleSubmitCreate();
      return;
    }
    handleSubmitLink();
  };

  const linkMarginPrice = linkPriceValid ? roundMoney(parsedLinkPrice) : null;
  const createMarginPrice = createPriceValid
    ? roundMoney(parsedCreatePrice)
    : null;

  const bukinistkaListPrice = (() => {
    const fromOffer =
      offer.bukinistkaSalePrice != null &&
      Number.isFinite(offer.bukinistkaSalePrice) &&
      offer.bukinistkaSalePrice > 0
        ? roundMoney(offer.bukinistkaSalePrice)
        : null;
    if (fromOffer != null) return fromOffer;
    if (
      createPreview?.odooListPrice != null &&
      Number.isFinite(createPreview.odooListPrice) &&
      createPreview.odooListPrice > 0
    ) {
      return roundMoney(createPreview.odooListPrice);
    }
    return null;
  })();

  return createPortal(
    <div
      className="fixed inset-0 z-[90] flex items-end justify-center overflow-y-auto bg-black/40 p-3 sm:items-center sm:p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="accept-received-offer-title"
    >
      <div className="flex max-h-[90vh] w-full max-w-xl flex-col overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl">
        <div className="flex shrink-0 items-start justify-between gap-4 border-b border-gray-100 px-5 py-4">
          <div>
            <h2
              id="accept-received-offer-title"
              className="text-lg font-semibold text-gray-900"
            >
              {offer.isAssignment
                ? 'Прыняць назначэнне ад Букіністкі'
                : 'Прыняць прапанову ад Букіністкі'}
            </h2>
            <p className="mt-1 text-sm text-gray-600">
              {offer.productName}
              {offer.productAuthor.trim()
                ? ` — ${offer.productAuthor.trim()}`
                : ''}
            </p>
            <p className="mt-0.5 text-xs text-gray-500">
              {offer.quantity} шт.
              {offer.isAssignment
                ? ' · Завяжыце з Shopify — у Odoo ўласнік стане Кірмаш.'
                : ' · Завяжыце з Shopify — склад павялічыцца.'}
            </p>
            <div className="mt-2 grid grid-cols-2 gap-2 text-sm">
              <div className="rounded-lg border border-gray-200 bg-gray-50 px-2.5 py-2">
                <div className="text-[11px] font-medium uppercase tracking-wide text-gray-500">
                  Кошт брута
                </div>
                <div className="mt-0.5 font-semibold tabular-nums text-gray-900">
                  {formatPrice(offerCost)}
                </div>
              </div>
              <div className="rounded-lg border border-indigo-100 bg-indigo-50 px-2.5 py-2">
                <div className="text-[11px] font-medium uppercase tracking-wide text-indigo-700">
                  Цана ў Букіністцы
                </div>
                <div className="mt-0.5 font-semibold tabular-nums text-indigo-950">
                  {bukinistkaListPrice != null
                    ? formatPrice(bukinistkaListPrice)
                    : createPreviewLoading
                      ? '…'
                      : '—'}
                </div>
              </div>
            </div>
            {createPreviewError && bukinistkaListPrice == null ? (
              <p className="mt-1 text-xs text-amber-800">
                {createPreviewError}
              </p>
            ) : null}
          </div>
          <button
            type="button"
            onClick={onClose}
            disabled={busy}
            className="inline-flex size-8 items-center justify-center rounded-lg text-gray-500 transition hover:bg-gray-100 hover:text-gray-800 disabled:opacity-50"
            aria-label="Закрыць"
          >
            <FiX className="size-5" aria-hidden />
          </button>
        </div>

        <div className="flex shrink-0 flex-wrap items-center gap-2 border-b border-gray-100 px-5 py-3">
          <button
            type="button"
            onClick={() => setMode('link')}
            disabled={busy}
            className={`rounded-lg px-3 py-1.5 text-sm font-medium transition disabled:opacity-50 ${
              mode === 'link'
                ? 'bg-primary/10 text-primary'
                : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
            }`}
          >
            Звязаць з існуючым
          </button>
          <button
            type="button"
            onClick={() => setMode('create')}
            disabled={busy}
            className={`rounded-lg px-3 py-1.5 text-sm font-medium transition disabled:opacity-50 ${
              mode === 'create'
                ? 'bg-primary/10 text-primary'
                : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
            }`}
          >
            Дадаць новы
          </button>
          <label className="ml-auto flex items-center gap-2 text-xs text-gray-600">
            VAT
            <select
              value={vatRate}
              onChange={(e) =>
                setVatRate(Number(e.target.value) === 23 ? 23 : 5)
              }
              disabled={busy}
              className="rounded-md border border-gray-200 bg-white px-2 py-1 text-xs text-gray-900"
            >
              <option value={5}>5%</option>
              <option value={23}>23%</option>
            </select>
          </label>
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
                  placeholder="Пошук па назве або ID…"
                  className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-3 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2"
                />
              </div>

              {productsLoading ? (
                <p className="py-8 text-center text-sm text-gray-500">
                  Загрузка прадуктаў Shopify…
                </p>
              ) : productsError ? (
                <div className="space-y-2 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
                  <p>{productsError}</p>
                  <button
                    type="button"
                    onClick={() => {
                      setProductsError(null);
                      setProductsLoading(true);
                      fetchProductsWithSuppliers(true)
                        .then((rows) => {
                          setProducts(
                            rows.map((row) => ({
                              shopifyProductId: row.shopifyProductId,
                              label: [row.productName, row.productAuthor]
                                .filter(Boolean)
                                .join(', '),
                              salePrice: row.shopifySalePrice,
                              quantityInStock: row.shopifyQuantityInStock,
                            }))
                          );
                        })
                        .catch((err: unknown) => {
                          setProductsError(
                            err instanceof Error
                              ? err.message
                              : 'Не ўдалося загрузіць прадукты Shopify.'
                          );
                        })
                        .finally(() => setProductsLoading(false));
                    }}
                    className="rounded-md border border-red-200 bg-white px-2.5 py-1 text-xs font-medium text-red-800 transition hover:bg-red-50"
                  >
                    Паўтарыць
                  </button>
                </div>
              ) : filtered.length === 0 ? (
                <p className="py-8 text-center text-sm text-gray-500">
                  Нічога не знойдзена. Стварыце новую картку на ўкладцы «Дадаць
                  новы».
                </p>
              ) : (
                <ul className="max-h-56 divide-y divide-gray-100 overflow-y-auto rounded-lg border border-gray-200">
                  {filtered.map((row) => {
                    const active = selectedId === row.shopifyProductId;
                    return (
                      <li key={row.shopifyProductId}>
                        <button
                          type="button"
                          onClick={() => setSelectedId(row.shopifyProductId)}
                          className={`flex w-full flex-col gap-0.5 px-3 py-2.5 text-left transition ${
                            active
                              ? 'bg-primary/10'
                              : 'bg-white hover:bg-gray-50'
                          }`}
                        >
                          <span className="text-sm font-medium text-gray-900">
                            {row.label || row.shopifyProductId}
                          </span>
                          <span className="text-xs text-gray-500">
                            У наяўнасці: {row.quantityInStock}
                            {' · '}
                            Цана: {formatPrice(row.salePrice)}
                            {' · '}
                            ID: {row.shopifyProductId}
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
                      {selected.label}
                    </span>
                  </p>
                  {bukinistkaListPrice != null ? (
                    <p className="text-xs text-gray-600">
                      Цана ў Букіністцы:{' '}
                      <span className="font-semibold tabular-nums text-gray-900">
                        {formatPrice(bukinistkaListPrice)}
                      </span>
                      {linkPriceValid ? (
                        <>
                          {' · '}
                          Kirma: {formatPrice(linkMarginPrice!)}
                          {linkMarginPrice! > bukinistkaListPrice ? (
                            <span className="text-emerald-700">
                              {' '}
                              (+
                              {formatPrice(
                                linkMarginPrice! - bukinistkaListPrice
                              )}
                              )
                            </span>
                          ) : linkMarginPrice! < bukinistkaListPrice ? (
                            <span className="text-amber-700">
                              {' '}
                              (−
                              {formatPrice(
                                bukinistkaListPrice - linkMarginPrice!
                              )}
                              )
                            </span>
                          ) : null}
                        </>
                      ) : null}
                    </p>
                  ) : null}
                  <label className="block">
                    <span className="mb-1.5 block text-sm font-medium text-gray-700">
                      Цана продажу ў Shopify
                    </span>
                    <input
                      type="number"
                      min={0}
                      step="0.01"
                      value={salePriceInput}
                      onChange={(e) => setSalePriceInput(e.target.value)}
                      className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2"
                    />
                    <MarginHint
                      grossCost={offerCost}
                      saleGross={linkMarginPrice}
                      vatRate={vatRate}
                    />
                  </label>
                </div>
              ) : null}
            </>
          ) : (
            <div className="space-y-4">
              {createPreviewLoading ? (
                <p className="py-8 text-center text-sm text-gray-500">
                  Загрузка даных з Odoo…
                </p>
              ) : createPreviewError ? (
                <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
                  {createPreviewError}
                </p>
              ) : (
                <>
                  <div className="space-y-2 rounded-lg border border-gray-200 bg-gray-50 px-3 py-3 text-sm text-gray-700">
                    <p>
                      <span className="font-medium text-gray-900">
                        {createPreview?.productName || offer.productName}
                      </span>
                    </p>
                    <p>
                      Кошт брута закупкі:{' '}
                      <span className="font-semibold tabular-nums text-gray-900">
                        {formatPrice(offerCost)}
                      </span>
                    </p>
                    {createPreview?.odooListPrice != null ? (
                      <p>
                        Цана ў Букіністцы:{' '}
                        <span className="font-semibold tabular-nums text-gray-900">
                          {formatPrice(createPreview.odooListPrice)}
                        </span>
                      </p>
                    ) : null}
                    {createPreview?.vendor ? (
                      <p>
                        Выдавец:{' '}
                        <span className="font-medium text-gray-900">
                          {createPreview.vendor}
                        </span>
                      </p>
                    ) : null}
                    {createPreview?.productAuthor ? (
                      <p>
                        Аўтар:{' '}
                        <span className="font-medium text-gray-900">
                          {createPreview.productAuthor}
                        </span>
                      </p>
                    ) : null}
                    {createPreview?.weightKg ? (
                      <p className="text-xs text-gray-500">
                        Вага / фармат: {createPreview.weightKg}
                      </p>
                    ) : null}
                    {createPreview?.barcodeDigits ? (
                      <p className="text-xs text-gray-500">
                        ISBN / barcode: {createPreview.barcodeDigits}
                      </p>
                    ) : null}
                    {createPreview?.descriptionHtml ? (
                      <p className="text-xs text-gray-500">
                        Апісанне з нататак Odoo будзе ў body_html.
                      </p>
                    ) : null}
                    <p className="text-xs text-gray-500">
                      Картка будзе створана як чернавік (draft) у Shopify.
                    </p>
                  </div>

                  <label className="block">
                    <span className="mb-1.5 block text-sm font-medium text-gray-700">
                      Цана продажу ў Shopify
                    </span>
                    <input
                      type="number"
                      min={0}
                      step="0.01"
                      value={createSalePriceInput}
                      onChange={(e) => setCreateSalePriceInput(e.target.value)}
                      disabled={busy}
                      className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2 disabled:opacity-50"
                    />
                    <MarginHint
                      grossCost={offerCost}
                      saleGross={createMarginPrice}
                      vatRate={vatRate}
                    />
                    <span className="mt-1 block text-xs text-gray-500">
                      Будзе створаная новая картка ў Shopify з данымі з Odoo.
                    </span>
                  </label>

                  {barcodeConflict ? (
                    <div
                      className="space-y-3 rounded-lg border border-amber-200 bg-amber-50 px-3 py-3"
                      role="alertdialog"
                    >
                      <div>
                        <p className="text-sm font-medium text-amber-950">
                          ISBN / barcode ужо заняты ў Shopify
                        </p>
                        <p className="mt-1 text-sm text-amber-900">
                          У журналаў часта аднолькавы ISBN. Што зрабіць?
                        </p>
                      </div>
                      <fieldset className="space-y-2">
                        <legend className="sr-only">Выбар ISBN</legend>
                        <label className="flex cursor-pointer items-start gap-2 text-sm text-amber-950">
                          <input
                            type="radio"
                            name="shopify-barcode-choice"
                            className="mt-0.5"
                            checked={barcodeChoice === 'clear'}
                            onChange={() => setBarcodeChoice('clear')}
                            disabled={busy}
                          />
                          <span>Стварыць без ISBN (пусты barcode)</span>
                        </label>
                        <label className="flex cursor-pointer items-start gap-2 text-sm text-amber-950">
                          <input
                            type="radio"
                            name="shopify-barcode-choice"
                            className="mt-0.5"
                            checked={barcodeChoice === 'manual'}
                            onChange={() => setBarcodeChoice('manual')}
                            disabled={busy}
                          />
                          <span>Увесці ISBN уручную</span>
                        </label>
                      </fieldset>
                      {barcodeChoice === 'manual' ? (
                        <label className="block">
                          <span className="mb-1.5 block text-sm font-medium text-amber-950">
                            Новы ISBN / barcode
                          </span>
                          <input
                            type="text"
                            inputMode="numeric"
                            autoComplete="off"
                            value={manualBarcodeInput}
                            onChange={(e) =>
                              setManualBarcodeInput(e.target.value)
                            }
                            disabled={busy}
                            placeholder="Толькі лічбы"
                            className="w-full rounded-lg border border-amber-200 bg-white px-3 py-2 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2 disabled:opacity-50"
                          />
                        </label>
                      ) : null}
                    </div>
                  ) : null}
                </>
              )}

              {createError && !barcodeConflict ? (
                <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
                  {createError}
                </p>
              ) : null}
              {createError && barcodeConflict ? (
                <p className="rounded-lg bg-red-50 px-3 py-2 text-xs text-red-700">
                  {createError}
                </p>
              ) : null}
            </div>
          )}

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
            disabled={busy}
            className="rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-50"
          >
            Скасаваць
          </button>
          <button
            type="button"
            disabled={!canSubmit}
            onClick={handleSubmit}
            className="inline-flex items-center gap-2 rounded-lg bg-primary px-3 py-2 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-50"
          >
            {busy ? (
              <span className="size-3.5 animate-spin rounded-full border-2 border-white/30 border-t-white" />
            ) : null}
            {mode === 'create'
              ? barcodeConflict
                ? 'Паўтарыць стварэнне'
                : 'Стварыць і прыняць'
              : 'Прыняць'}
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
