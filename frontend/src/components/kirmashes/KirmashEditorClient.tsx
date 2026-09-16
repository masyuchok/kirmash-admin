'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { FiChevronLeft, FiChevronRight } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import KirmashPriceTagsPreview, {
  buildKirmashPriceTagsPrintHtml,
  collectKirmashPrintQrAssets,
  printKirmashPriceTagsHtml,
} from '@/components/kirmashes/KirmashPriceTagsPreview';
import {
  createKirmash,
  fetchKirmash,
  fetchKirmashPriceTags,
  generateKirmashPriceTags,
  updateKirmash,
  type KirmashDetail,
  type KirmashPriceTag,
} from '@/lib/api/kirmashes';
import { fetchProductsWithSuppliers } from '@/lib/api/products';
import { formatProductNameWithAuthor } from '@/lib/supply-draft';
import { makeSupplyLineKey } from '@/lib/supply-line-key';
import type { ProductWithSuppliers } from '@/types/product';

type DraftLine = {
  lineKey: string;
  shopifyProductId: string;
  shopifyVariantId: string;
  title: string;
  unitPrice: number;
  quantity: number;
};

type Props = {
  kirmashId?: number;
};

const CATALOG_PAGE_SIZE = 50;

const inputClass =
  'w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25';

function todayIso(): string {
  const d = new Date();
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

function round2(n: number): number {
  return Math.round(n * 100) / 100;
}

export default function KirmashEditorClient({ kirmashId }: Props) {
  const router = useRouter();
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const isNew = kirmashId == null;

  const [loading, setLoading] = useState(!isNew);
  const [saving, setSaving] = useState(false);
  const [generating, setGenerating] = useState(false);
  const [printing, setPrinting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [eventDate, setEventDate] = useState(todayIso());
  const [status, setStatus] = useState('draft');
  const [lines, setLines] = useState<DraftLine[]>([]);
  const [tags, setTags] = useState<KirmashPriceTag[]>([]);
  const [savedId, setSavedId] = useState<number | null>(kirmashId ?? null);

  const [catalog, setCatalog] = useState<ProductWithSuppliers[]>([]);
  const [catalogLoading, setCatalogLoading] = useState(false);
  const [productSearch, setProductSearch] = useState('');
  const [supplierFilter, setSupplierFilter] = useState('');
  const [catalogPage, setCatalogPage] = useState(1);
  const [showPreview, setShowPreview] = useState(false);

  useEffect(() => {
    setTopbarPage({
      title: isNew ? 'Новы кірмаш' : 'Кірмаш',
      subtitle: title.trim() || (isNew ? 'Стварэнне' : 'Рэдагаванне'),
    });
    setTopbarButtons([
      {
        label: 'Да спісу',
        onClick: () => router.push('/kirmashes'),
        variant: 'secondary',
      },
    ]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [isNew, router, setTopbarButtons, setTopbarPage, title]);

  useEffect(() => {
    let cancelled = false;
    setCatalogLoading(true);
    fetchProductsWithSuppliers()
      .then((rows) => {
        if (!cancelled) setCatalog(rows);
      })
      .catch(() => {
        if (!cancelled) setCatalog([]);
      })
      .finally(() => {
        if (!cancelled) setCatalogLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (isNew || kirmashId == null) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    Promise.all([fetchKirmash(kirmashId), fetchKirmashPriceTags(kirmashId)])
      .then(([detail, priceTags]) => {
        if (cancelled) return;
        applyDetail(detail);
        setTags(priceTags);
        setShowPreview(priceTags.length > 0);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(
            err instanceof Error ? err.message : 'Не ўдалося загрузіць кірмаш'
          );
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [isNew, kirmashId]);

  const applyDetail = (detail: KirmashDetail) => {
    setSavedId(detail.id);
    setTitle(detail.title);
    setDescription(detail.description);
    setEventDate(detail.eventDate || todayIso());
    setStatus(detail.status);
    setLines(
      detail.lines.map((line) => ({
        lineKey: makeSupplyLineKey(
          line.shopifyProductId,
          line.shopifyVariantId
        ),
        shopifyProductId: line.shopifyProductId,
        shopifyVariantId: line.shopifyVariantId,
        title: line.title,
        unitPrice: line.unitPrice,
        quantity: line.quantity,
      }))
    );
  };

  const supplierOptions = useMemo(() => {
    const names = new Set<string>();
    for (const product of catalog) {
      for (const name of product.suppliers ?? []) {
        const trimmed = name.trim();
        if (trimmed) names.add(trimmed);
      }
      for (const price of product.supplierPrices ?? []) {
        const trimmed = price.supplierName.trim();
        if (trimmed) names.add(trimmed);
      }
      const last = product.lastSyncedSupplierName?.trim();
      if (last) names.add(last);
    }
    return [...names].sort((a, b) => a.localeCompare(b, 'be'));
  }, [catalog]);

  const visibleCatalog = useMemo(() => {
    const q = productSearch.trim().toLowerCase();
    const supplier = supplierFilter.trim().toLowerCase();
    const selected = new Set(lines.map((l) => l.lineKey));
    return catalog
      .filter((product) => {
        if (!supplier) return true;
        const names = [
          ...(product.suppliers ?? []),
          ...(product.supplierPrices ?? []).map((p) => p.supplierName),
          product.lastSyncedSupplierName ?? '',
        ]
          .map((n) => n.trim().toLowerCase())
          .filter(Boolean);
        return names.includes(supplier);
      })
      .flatMap((product) => {
        const variants =
          product.variants.length > 0
            ? product.variants
            : [
                {
                  variantId: '',
                  variantName: 'Default Title',
                  quantityInStock: product.quantityInStock,
                },
              ];
        return variants
          .filter((variant) => {
            const stock =
              product.variants.length > 0
                ? variant.quantityInStock
                : Math.max(
                    product.quantityInStock,
                    product.shopifyQuantityInStock
                  );
            return stock > 0;
          })
          .map((variant) => {
            const lineKey = makeSupplyLineKey(
              product.shopifyProductId,
              variant.variantId
            );
            const stock =
              product.variants.length > 0
                ? variant.quantityInStock
                : Math.max(
                    product.quantityInStock,
                    product.shopifyQuantityInStock
                  );
            const title = formatProductNameWithAuthor(
              product.productName,
              product.productAuthor
            );
            const withVariant =
              variant.variantName &&
              variant.variantName.trim() &&
              variant.variantName !== 'Default Title'
                ? `${title} · ${variant.variantName}`
                : title;
            return {
              lineKey,
              shopifyProductId: product.shopifyProductId,
              shopifyVariantId: variant.variantId,
              title: withVariant,
              unitPrice: round2(product.shopifySalePrice || 0),
              mainImageUrl: product.mainImageUrl,
              quantityInStock: stock,
              selected: selected.has(lineKey),
            };
          });
      })
      .filter((row) => {
        if (!q) return true;
        return row.title.toLowerCase().includes(q);
      });
  }, [catalog, lines, productSearch, supplierFilter]);

  const catalogTotalPages = Math.max(
    1,
    Math.ceil(visibleCatalog.length / CATALOG_PAGE_SIZE)
  );
  const safeCatalogPage = Math.min(catalogPage, catalogTotalPages);
  const pagedCatalog = useMemo(() => {
    const start = (safeCatalogPage - 1) * CATALOG_PAGE_SIZE;
    return visibleCatalog.slice(start, start + CATALOG_PAGE_SIZE);
  }, [visibleCatalog, safeCatalogPage]);

  useEffect(() => {
    setCatalogPage(1);
  }, [productSearch, supplierFilter]);

  const unitsTotal = useMemo(
    () => lines.reduce((sum, line) => sum + line.quantity, 0),
    [lines]
  );

  const toggleProduct = (row: {
    lineKey: string;
    shopifyProductId: string;
    shopifyVariantId: string;
    title: string;
    unitPrice: number;
    selected: boolean;
  }) => {
    setLines((prev) => {
      if (row.selected) {
        return prev.filter((line) => line.lineKey !== row.lineKey);
      }
      return [
        ...prev,
        {
          lineKey: row.lineKey,
          shopifyProductId: row.shopifyProductId,
          shopifyVariantId: row.shopifyVariantId,
          title: row.title,
          unitPrice: row.unitPrice,
          quantity: 1,
        },
      ];
    });
    setTags([]);
    setShowPreview(false);
    setStatus('draft');
  };

  const updateLineQuantity = (lineKey: string, quantity: number) => {
    const qty = Math.max(1, Math.trunc(quantity) || 1);
    setLines((prev) =>
      prev.map((line) =>
        line.lineKey === lineKey ? { ...line, quantity: qty } : line
      )
    );
    setTags([]);
    setShowPreview(false);
  };

  const updateLinePrice = (lineKey: string, unitPrice: number) => {
    setLines((prev) =>
      prev.map((line) =>
        line.lineKey === lineKey
          ? { ...line, unitPrice: Math.max(0, round2(unitPrice)) }
          : line
      )
    );
    setTags([]);
    setShowPreview(false);
  };

  const buildPayload = () => ({
    title: title.trim(),
    description: description.trim(),
    eventDate,
    lines: lines.map((line) => ({
      shopifyProductId: line.shopifyProductId,
      shopifyVariantId: line.shopifyVariantId,
      title: line.title,
      unitPrice: line.unitPrice,
      quantity: line.quantity,
    })),
  });

  const save = async (): Promise<number | null> => {
    setSaving(true);
    setError(null);
    try {
      const payload = buildPayload();
      const detail =
        savedId != null
          ? await updateKirmash(savedId, payload)
          : await createKirmash(payload);
      applyDetail(detail);
      if (isNew) {
        router.replace(`/kirmashes/${detail.id}`);
      }
      return detail.id;
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка захавання');
      return null;
    } finally {
      setSaving(false);
    }
  };

  const onGenerateTags = async () => {
    setGenerating(true);
    setError(null);
    try {
      let id = savedId;
      if (id == null || status === 'draft' || tags.length === 0) {
        id = await save();
      } else {
        // Always persist current lines before regenerating.
        id = await save();
      }
      if (id == null) return;
      const generated = await generateKirmashPriceTags(id);
      setTags(generated);
      setStatus('ready');
      setShowPreview(true);
    } catch (err: unknown) {
      setError(
        err instanceof Error ? err.message : 'Не ўдалося стварыць цэннікі'
      );
    } finally {
      setGenerating(false);
    }
  };

  const onPrint = async () => {
    if (tags.length === 0) return;
    setPrinting(true);
    setError(null);
    try {
      const qrAssets = await collectKirmashPrintQrAssets(tags);
      const html = buildKirmashPriceTagsPrintHtml(
        tags,
        title.trim() || 'Кірмаш',
        qrAssets
      );
      await printKirmashPriceTagsHtml(html);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка друку');
    } finally {
      setPrinting(false);
    }
  };

  const loadExistingTags = useCallback(async () => {
    if (savedId == null) return;
    const existing = await fetchKirmashPriceTags(savedId);
    setTags(existing);
    setShowPreview(existing.length > 0);
  }, [savedId]);

  if (loading) {
    return (
      <div className="flex justify-center py-16">
        <LoadingSpinner />
      </div>
    );
  }

  return (
    <div className="space-y-6 p-4 sm:p-6">
      {error && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
          {error}
        </div>
      )}

      <div className="grid grid-cols-1 gap-4 rounded-xl border border-gray-200 bg-white p-4 sm:grid-cols-2">
        <label className="block space-y-1.5 sm:col-span-2">
          <span className="text-sm font-medium text-gray-700">Назва</span>
          <input
            className={inputClass}
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Напр. Кірмаш у Варшаве"
          />
        </label>
        <label className="block space-y-1.5">
          <span className="text-sm font-medium text-gray-700">Дата</span>
          <input
            type="date"
            className={inputClass}
            value={eventDate}
            onChange={(e) => setEventDate(e.target.value)}
          />
        </label>
        <div className="flex items-end text-sm text-gray-500">
          Статус:{' '}
          <span className="ml-1 font-medium text-gray-800">
            {status === 'ready' ? 'Гатовы' : 'Чарнавік'}
          </span>
        </div>
        <label className="block space-y-1.5 sm:col-span-2">
          <span className="text-sm font-medium text-gray-700">Апісанне</span>
          <textarea
            className={inputClass}
            rows={3}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Кароткае апісанне кірмаша"
          />
        </label>
      </div>

      <div className="space-y-3 rounded-xl border border-gray-200 bg-white p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <div className="text-sm font-semibold text-gray-900">Тавары</div>
            <div className="text-xs text-gray-500">
              Выбрана: {lines.length} паз. · {unitsTotal} цэннікаў
            </div>
          </div>
        </div>

        {lines.length > 0 && (
          <div className="divide-y divide-gray-100 rounded-lg border border-gray-200">
            {lines.map((line) => (
              <div
                key={line.lineKey}
                className="flex flex-col gap-2 px-3 py-2 sm:flex-row sm:items-center"
              >
                <div className="min-w-0 flex-1 text-sm font-medium text-gray-900">
                  {line.title}
                </div>
                <label className="inline-flex items-center gap-1.5 text-xs text-gray-600">
                  Кол.
                  <input
                    type="number"
                    min={1}
                    className="w-16 rounded-md border border-gray-200 px-2 py-1 text-sm"
                    value={line.quantity}
                    onChange={(e) =>
                      updateLineQuantity(line.lineKey, Number(e.target.value))
                    }
                  />
                </label>
                <label className="inline-flex items-center gap-1.5 text-xs text-gray-600">
                  Цана
                  <input
                    type="number"
                    min={0}
                    step="0.01"
                    className="w-24 rounded-md border border-gray-200 px-2 py-1 text-sm"
                    value={line.unitPrice || ''}
                    onChange={(e) =>
                      updateLinePrice(line.lineKey, Number(e.target.value))
                    }
                  />
                </label>
                <button
                  type="button"
                  className="text-xs text-red-600 hover:underline"
                  onClick={() =>
                    toggleProduct({ ...line, selected: true, unitPrice: 0 })
                  }
                >
                  Прыбраць
                </button>
              </div>
            ))}
          </div>
        )}

        <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <input
            type="search"
            className={inputClass}
            value={productSearch}
            onChange={(e) => setProductSearch(e.target.value)}
            placeholder="Пошук тавару…"
          />
          <select
            className={inputClass}
            value={supplierFilter}
            onChange={(e) => setSupplierFilter(e.target.value)}
          >
            <option value="">Усе пастаўшчыкі</option>
            {supplierOptions.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
        </div>
        <div className="text-xs text-gray-500">
          Паказваюцца толькі тавары ў наяўнасці
          {supplierFilter ? ` · фільтр: ${supplierFilter}` : ''}
          {visibleCatalog.length > 0
            ? ` · ${visibleCatalog.length} пазіцый`
            : ''}
          .
        </div>
        <div className="overflow-hidden rounded-lg border border-gray-200">
          <div className="max-h-72 overflow-y-auto">
            {catalogLoading ? (
              <div className="px-3 py-4 text-sm text-gray-500">Загрузка…</div>
            ) : visibleCatalog.length === 0 ? (
              <div className="px-3 py-4 text-sm text-gray-500">
                Нічога не знойдзена ў наяўнасці
              </div>
            ) : (
              pagedCatalog.map((row) => (
                <label
                  key={row.lineKey}
                  className="flex cursor-pointer items-center gap-3 border-b border-gray-100 px-3 py-2 last:border-b-0 hover:bg-gray-50"
                >
                  <input
                    type="checkbox"
                    checked={row.selected}
                    onChange={() => toggleProduct(row)}
                    className="size-4 accent-primary"
                  />
                  {row.mainImageUrl ? (
                    // eslint-disable-next-line @next/next/no-img-element
                    <img
                      src={row.mainImageUrl}
                      alt=""
                      className="h-10 w-8 rounded border border-gray-200 object-cover"
                    />
                  ) : (
                    <div className="h-10 w-8 rounded border border-gray-200 bg-gray-100" />
                  )}
                  <span className="min-w-0 flex-1 text-sm text-gray-800">
                    <span className="line-clamp-2">{row.title}</span>
                    <span className="block text-xs text-gray-500">
                      {row.unitPrice > 0
                        ? `${row.unitPrice.toFixed(2)} зл`
                        : 'без цаны'}{' '}
                      · у наяўнасці: {row.quantityInStock}
                    </span>
                  </span>
                </label>
              ))
            )}
          </div>
          {visibleCatalog.length > CATALOG_PAGE_SIZE ? (
            <div className="flex items-center justify-between gap-2 border-t border-gray-100 bg-gray-50 px-3 py-2">
              <p className="text-xs text-gray-500">
                Старонка {safeCatalogPage} з {catalogTotalPages}
              </p>
              <div className="flex items-center gap-1">
                <button
                  type="button"
                  aria-label="Папярэдняя старонка"
                  disabled={safeCatalogPage <= 1}
                  onClick={() => setCatalogPage((p) => Math.max(1, p - 1))}
                  className="inline-flex size-8 items-center justify-center rounded-lg border border-gray-200 bg-white text-gray-700 transition hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <FiChevronLeft className="size-4" aria-hidden />
                </button>
                <button
                  type="button"
                  aria-label="Наступная старонка"
                  disabled={safeCatalogPage >= catalogTotalPages}
                  onClick={() =>
                    setCatalogPage((p) => Math.min(catalogTotalPages, p + 1))
                  }
                  className="inline-flex size-8 items-center justify-center rounded-lg border border-gray-200 bg-white text-gray-700 transition hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <FiChevronRight className="size-4" aria-hidden />
                </button>
              </div>
            </div>
          ) : null}
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          onClick={() => void save()}
          disabled={saving || generating}
          className="rounded-lg border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
        >
          {saving ? 'Захаванне…' : 'Захаваць'}
        </button>
        <button
          type="button"
          onClick={() => void onGenerateTags()}
          disabled={saving || generating || lines.length === 0}
          className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary/90 disabled:opacity-60"
        >
          {generating ? 'Ствараем цэннікі…' : 'Стварыць цэннікі'}
        </button>
        {tags.length > 0 && (
          <>
            <button
              type="button"
              onClick={() => setShowPreview((v) => !v)}
              className="rounded-lg border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
            >
              {showPreview ? 'Схаваць прэв’ю' : 'Паказаць прэв’ю'}
            </button>
            <button
              type="button"
              onClick={() => void onPrint()}
              disabled={printing}
              className="rounded-lg border border-primary bg-white px-4 py-2 text-sm font-medium text-primary hover:bg-primary/5 disabled:opacity-60"
            >
              {printing ? 'Падрыхтоўка…' : 'Друкаваць / PDF'}
            </button>
            <button
              type="button"
              onClick={() => void loadExistingTags()}
              className="text-sm text-gray-500 hover:underline"
            >
              Абнавіць цэннікі
            </button>
          </>
        )}
      </div>

      {showPreview && tags.length > 0 && (
        <div className="rounded-xl border border-gray-200 bg-white p-4">
          <KirmashPriceTagsPreview
            tags={tags}
            kirmashTitle={title.trim() || 'Кірмаш'}
          />
        </div>
      )}
    </div>
  );
}
