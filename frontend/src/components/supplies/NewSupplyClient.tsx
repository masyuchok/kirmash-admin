'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { FiPlus, FiX } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { apiCredentials, getApiBaseUrl } from '@/lib/api/common';
import { fetchProductsWithSuppliers } from '@/lib/api/products';
import { saveSupply } from '@/lib/api/supply-save';
import { fetchSupplyById } from '@/lib/api/supplies';
import type { ProductWithSuppliers } from '@/types/product';

type Props = {
  initialSupplierId?: string;
  initialSupplierName?: string;
  initialDate: string;
  supplyId?: string;
  selectedProductIds?: string[];
};

type SupplierOption = {
  id: number;
  name: string;
  isVatPayer: boolean;
};

type SupplyProductDraft = {
  productId: string;
  productName: string;
  productType: string;
  syncWithShopify: boolean;
  quantity: string;
  supplierPrice: string;
  vatRatePercent: string;
  marginPercent: string;
  salePrice: string;
};

export default function NewSupplyClient({
  initialSupplierId = '',
  initialSupplierName = '',
  initialDate,
  supplyId,
  selectedProductIds = [],
}: Props) {
  const VAT_RATE_OPTIONS = [5, 23] as const;
  const VAT_BOOK = 0.05;
  const VAT_DEFAULT = 0.23;

  const parseDecimal = (value: string): number | null => {
    const normalized = value.replace(',', '.').trim();
    if (!normalized) return null;
    const n = Number(normalized);
    return Number.isFinite(n) ? n : null;
  };

  const formatDecimal = (value: number): string => {
    if (!Number.isFinite(value)) return '';
    return value.toFixed(2);
  };

  const resolveDefaultVatRatePercent = (productType: string): number => {
    const t = productType.trim().toLowerCase();
    if (t.includes('кніг') || t.includes('книга') || t.includes('book')) return VAT_BOOK * 100;
    return VAT_DEFAULT * 100;
  };

  const normalizeVatRateOption = (value: number): number => {
    return VAT_RATE_OPTIONS.includes(value as (typeof VAT_RATE_OPTIONS)[number]) ? value : 23;
  };

  const round2 = (value: number): number => Math.round((value + Number.EPSILON) * 100) / 100;

  const router = useRouter();
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [currentSupplierId, setCurrentSupplierId] = useState(initialSupplierId);
  const [currentSupplierName, setCurrentSupplierName] = useState(initialSupplierName);
  const [currentDate, setCurrentDate] = useState(initialDate);
  const [suppliers, setSuppliers] = useState<SupplierOption[]>([]);
  const [selectedProducts, setSelectedProducts] = useState<ProductWithSuppliers[]>([]);
  const [productCatalog, setProductCatalog] = useState<ProductWithSuppliers[]>([]);
  const [productDrafts, setProductDrafts] = useState<SupplyProductDraft[]>([]);
  const [saving, setSaving] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [addingProductsLoading, setAddingProductsLoading] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveOk, setSaveOk] = useState<string | null>(null);
  const [initialLoading, setInitialLoading] = useState(Boolean(supplyId));

  useEffect(() => {
    setTopbarPage({ title: supplyId ? `Пастаўка #${supplyId}` : 'Новая пастаўка' });
    setTopbarButtons([
      {
        label: 'Дадаць тавар',
        icon: <FiPlus />,
        onClick: () => {
          const query = new URLSearchParams();
          if (supplyId) query.set('supplyId', supplyId);
          if (currentSupplierId) query.set('supplierId', currentSupplierId);
          if (currentSupplierName) query.set('supplierName', currentSupplierName);
          if (currentDate) query.set('date', currentDate);
          const currentIds = productDrafts.map((p) => p.productId);
          if (currentIds.length > 0) query.set('selectedProductIds', currentIds.join(','));
          router.push(`/supplies/products?${query.toString()}`);
        },
        variant: 'primary',
      },
    ]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [
    setTopbarButtons,
    setTopbarPage,
    supplyId,
    initialSupplierId,
    currentSupplierId,
    currentSupplierName,
    currentDate,
    productDrafts,
    router,
  ]);

  useEffect(() => {
    let cancelled = false;
    fetch(`${getApiBaseUrl()}/suppliers`, { credentials: apiCredentials })
      .then((res) => res.json())
      .then((data: unknown) => {
        if (cancelled || !Array.isArray(data)) return;
        const rows = data
          .map((row) => {
            const r = row as Record<string, unknown>;
            const id = typeof r.id === 'number' ? r.id : Number(r.id);
            const name = typeof r.name === 'string' ? r.name : '';
            const isVatPayer = Boolean(
              r.isVatPayer ?? r.isVATPayer ?? r.IsVatPayer ?? r.IsVATPayer ?? false
            );
            if (!Number.isFinite(id) || !name.trim()) return null;
            return { id, name, isVatPayer };
          })
          .filter((row): row is SupplierOption => row !== null);
        setSuppliers(rows);
      })
      .catch(() => {
        if (!cancelled) setSuppliers([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!supplyId) {
      setInitialLoading(false);
      return;
    }
    let cancelled = false;
    setInitialLoading(true);
    Promise.all([fetchSupplyById(Number(supplyId)), fetchProductsWithSuppliers()])
      .then(([supply, products]) => {
        if (cancelled) return;
        setProductCatalog(products);
        setCurrentSupplierId(String(supply.supplierId || ''));
        setCurrentSupplierName(supply.supplierName || '');
        setCurrentDate(supply.date || '');

        const productMap = new Map(products.map((p) => [p.shopifyProductId, p]));
        const dbDrafts: SupplyProductDraft[] = supply.products.map((p) => {
          const match = productMap.get(p.shopifyProductId);
          return {
            productId: p.shopifyProductId,
            productName: match?.productName ?? p.shopifyProductId,
            productType: match?.productType ?? '',
            syncWithShopify: p.syncWithShopify,
            quantity: p.quantity > 0 ? String(p.quantity) : '',
            supplierPrice: p.supplierPrice > 0 ? String(p.supplierPrice) : '',
            vatRatePercent: String(normalizeVatRateOption(p.vatRatePercent > 0 ? p.vatRatePercent : 23)),
            marginPercent: p.marginPercent > 0 ? String(p.marginPercent) : '',
            salePrice: p.salePrice > 0 ? String(p.salePrice) : '',
          };
        });
        // Merge DB rows with rows already chosen in picker to avoid race-based overwrite.
        setProductDrafts((prev) => {
          const byId = new Map<string, SupplyProductDraft>();
          for (const row of dbDrafts) byId.set(row.productId, row);
          for (const row of prev) {
            if (!byId.has(row.productId)) byId.set(row.productId, row);
          }
          return Array.from(byId.values());
        });
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setSaveError(err instanceof Error ? err.message : 'Не ўдалося загрузіць пастаўку');
        }
      })
      .finally(() => {
        if (!cancelled) setInitialLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [supplyId]);

  useEffect(() => {
    if (selectedProductIds.length === 0) {
      setAddingProductsLoading(false);
      return;
    }
    let cancelled = false;
    setAddingProductsLoading(true);
    fetchProductsWithSuppliers()
      .then((rows) => {
        if (cancelled) return;
        setProductCatalog(rows);
        const selected = rows.filter((p) => selectedProductIds.includes(p.shopifyProductId));
        setSelectedProducts(selected);
      })
      .catch(() => {
        if (!cancelled) setSelectedProducts([]);
      })
      .finally(() => {
        if (!cancelled) setAddingProductsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [selectedProductIds]);

  useEffect(() => {
    if (selectedProducts.length === 0) return;
    setProductDrafts((prev) => {
      const prevMap = new Map(prev.map((row) => [row.productId, row]));
      const next = [...prev];
      for (const product of selectedProducts) {
        if (prevMap.has(product.shopifyProductId)) continue;
        next.push({
          productId: product.shopifyProductId,
          productName: product.productName,
          productType: product.productType,
          syncWithShopify: true,
          quantity: '',
          supplierPrice: '',
          vatRatePercent: String(resolveDefaultVatRatePercent(product.productType)),
          marginPercent: '',
          salePrice: '',
        });
      }
      return next;
    });
  }, [selectedProducts]);

  const updateDraftField = (
    productId: string,
    field: 'quantity' | 'supplierPrice' | 'vatRatePercent' | 'marginPercent' | 'salePrice',
    value: string
  ) => {
    const recalcByMargin = (
      supplierNetPrice: number,
      marginPct: number
    ): { saleGross: number; vatAmount: number; netSale: number } => {
      const netSale = round2(supplierNetPrice * (1 + marginPct / 100));
      // Business rule: when margin is set, gross = net * 1.23.
      const saleGross = round2(netSale * 1.23);
      const vatGrossPart = round2((saleGross * 23) / 123);
      const vatToPay = vatGrossPart;
      return { saleGross, vatAmount: vatToPay, netSale };
    };

    const recalcByGross = (
      supplierNetPrice: number,
      saleGross: number
    ): { marginPct: number; vatAmount: number; netSale: number } => {
      const vatGrossPart = round2((saleGross * 23) / 123);
      const vatToPay = vatGrossPart;
      const netSale = round2(saleGross - vatToPay);
      const marginPct = supplierNetPrice > 0 ? round2(((netSale - supplierNetPrice) / supplierNetPrice) * 100) : 0;
      return { marginPct, vatAmount: vatToPay, netSale };
    };

    setProductDrafts((prev) =>
      prev.map((row) => {
        if (row.productId !== productId) return row;

        const next: SupplyProductDraft = { ...row, [field]: value };
        const supplierPrice = parseDecimal(next.supplierPrice);
        const vatRatePercent = parseDecimal(next.vatRatePercent);
        const marginPercent = parseDecimal(next.marginPercent);
        const salePrice = parseDecimal(next.salePrice);
        const vatRate = vatRatePercent ?? 23;

        // If user edits margin, auto-calc sale price (gross) from net margin logic.
        if (field === 'marginPercent' && supplierPrice !== null && marginPercent !== null) {
          const calculated = recalcByMargin(supplierPrice, marginPercent);
          next.salePrice = formatDecimal(calculated.saleGross);
        }

        // If user edits sale price (gross), auto-calc margin from net sale.
        if (field === 'salePrice' && supplierPrice !== null && supplierPrice > 0 && salePrice !== null) {
          const calculated = recalcByGross(supplierPrice, salePrice);
          next.marginPercent = formatDecimal(calculated.marginPct);
        }

        // If supplier net price changes, keep sale/margin synced.
        if (field === 'supplierPrice' && supplierPrice !== null && supplierPrice > 0) {
          if (marginPercent !== null) {
            const calculated = recalcByMargin(supplierPrice, marginPercent);
            next.salePrice = formatDecimal(calculated.saleGross);
          } else if (salePrice !== null) {
            const calculated = recalcByGross(supplierPrice, salePrice);
            next.marginPercent = formatDecimal(calculated.marginPct);
          }
        }
        if (field === 'vatRatePercent' && supplierPrice !== null && supplierPrice > 0) {
          if (marginPercent !== null) {
            const calculated = recalcByMargin(supplierPrice, marginPercent);
            next.salePrice = formatDecimal(calculated.saleGross);
          } else if (salePrice !== null) {
            const calculated = recalcByGross(supplierPrice, salePrice);
            next.marginPercent = formatDecimal(calculated.marginPct);
          }
        }

        return next;
      })
    );
  };

  const removeDraft = (productId: string) => {
    setProductDrafts((prev) => prev.filter((row) => row.productId !== productId));
    setSelectedProducts((prev) => prev.filter((row) => row.shopifyProductId !== productId));
  };

  const toggleSyncWithShopify = (productId: string) => {
    setProductDrafts((prev) =>
      prev.map((row) =>
        row.productId === productId ? { ...row, syncWithShopify: !row.syncWithShopify } : row
      )
    );
  };

  const handleSave = async () => {
    const supplierIdNumber = Number(currentSupplierId);
    if (!Number.isFinite(supplierIdNumber) || supplierIdNumber <= 0) {
      setSaveError('Не зададзены пастаўшчык для захавання.');
      return;
    }
    if (!currentDate) {
      setSaveError('Не зададзена дата пастаўкі.');
      return;
    }
    if (productDrafts.length === 0 && !supplyId) {
      setSaveError('Дадайце хаця б адзін тавар перад захаваннем.');
      return;
    }

    for (const row of productDrafts) {
      const quantity = Number(row.quantity);
      const supplierPrice = Number(row.supplierPrice);
      const marginPercent = Number(row.marginPercent);
      const vatRatePercent = Number(row.vatRatePercent);
      const salePrice = Number(row.salePrice);
      if (!Number.isFinite(quantity) || quantity <= 0) {
        setSaveError(`Праверце колькасць для "${row.productName}" (павінна быць > 0).`);
        return;
      }
      if (!Number.isFinite(supplierPrice) || supplierPrice < 0) {
        setSaveError(`Праверце цану пастаўшчыка для "${row.productName}".`);
        return;
      }
      if (!Number.isFinite(marginPercent) || marginPercent < 0) {
        setSaveError(`Праверце "Наш %" для "${row.productName}".`);
        return;
      }
      if (!Number.isFinite(vatRatePercent) || !VAT_RATE_OPTIONS.includes(vatRatePercent as 5 | 23)) {
        setSaveError(`Праверце "VAT %" для "${row.productName}" (5% або 23%).`);
        return;
      }
      if (!Number.isFinite(salePrice) || salePrice < 0) {
        setSaveError(`Праверце цану продажу для "${row.productName}".`);
        return;
      }
    }

    setSaving(true);
    setSaveError(null);
    setSaveOk(null);
    try {
      const payloadProducts = productDrafts.map((row) => ({
        shopifyProductId: row.productId,
        quantity: Number(row.quantity || 0),
        supplierPrice: Number(row.supplierPrice || 0),
        vatRatePercent: Number(row.vatRatePercent || 0),
        marginPercent: Number(row.marginPercent || 0),
        salePrice: Number(row.salePrice || 0),
        syncWithShopify: row.syncWithShopify,
      }));

      const result = await saveSupply({
        supplyId: supplyId ? Number(supplyId) : undefined,
        supplierId: supplierIdNumber,
        date: currentDate,
        products: payloadProducts,
      });

      const updatedCount = result.inventoryUpdates.length;
      if (result.warning) {
        setSaveOk(
          updatedCount > 0
            ? `Змены захаваныя. Shopify часткова абноўлены для ${updatedCount} тав.`
            : 'Змены захаваныя ў БД, але без сінхранізацыі астаткаў у Shopify.'
        );
        setSaveError(`Сінхранізацыя з Shopify: ${result.warning}`);
      } else {
        setSaveOk(
          updatedCount > 0
            ? `Змены захаваныя. Астаткі ў Shopify абноўлены для ${updatedCount} тав.`
            : 'Змены захаваныя.'
        );
      }
      if (!supplyId && result.id > 0) {
        const query = new URLSearchParams();
        query.set('supplierId', String(supplierIdNumber));
        if (currentSupplierName) query.set('supplierName', currentSupplierName);
        query.set('date', currentDate);
        const currentIds = productDrafts.map((p) => p.productId);
        if (currentIds.length > 0) query.set('selectedProductIds', currentIds.join(','));
        router.replace(`/supplies/${result.id}?${query.toString()}`);
      }
    } catch (err: unknown) {
      setSaveError(err instanceof Error ? err.message : 'Памылка захавання');
    } finally {
      setSaving(false);
    }
  };

  const refreshFromShopify = async () => {
    if (productDrafts.length === 0) return;
    setRefreshing(true);
    setSaveError(null);
    setSaveOk(null);
    try {
      const products = await fetchProductsWithSuppliers();
      setProductCatalog(products);
      const productMap = new Map(products.map((p) => [p.shopifyProductId, p]));

      setProductDrafts((prev) =>
        prev.map((row) => {
          const live = productMap.get(row.productId);
          if (!live) return row;
          return {
            ...row,
            productName: live.productName,
            productType: live.productType,
          };
        })
      );

      setSelectedProducts((prev) =>
        prev.map((row) => {
          const live = productMap.get(row.shopifyProductId);
          return live ?? row;
        })
      );

      setSaveOk('Даныя па таварах абноўлены з Shopify.');
    } catch (err: unknown) {
      setSaveError(err instanceof Error ? err.message : 'Памылка абнаўлення з Shopify');
    } finally {
      setRefreshing(false);
    }
  };

  const supplierName = useMemo(() => {
    if (currentSupplierName.trim()) return currentSupplierName.trim();
    const match = suppliers.find((s) => String(s.id) === currentSupplierId);
    return match?.name ?? `ID: ${currentSupplierId}`;
  }, [currentSupplierId, currentSupplierName, suppliers]);

  const productMetaMap = useMemo(
    () => new Map(productCatalog.map((p) => [p.shopifyProductId, p])),
    [productCatalog]
  );

  const currentSupplierIdNum = Number(currentSupplierId);

  const formatMoney = (value: number): string => {
    return Number.isFinite(value) ? value.toFixed(2) : '0.00';
  };

  if (!currentDate || (!currentSupplierId && !currentSupplierName.trim())) {
    return (
      <div className="mx-auto w-full max-w-6xl rounded-xl border border-gray-200 bg-white px-6 py-8 shadow-sm">
        <p className="text-sm text-red-600">Не хапае параметраў для адкрыцця пастаўкі.</p>
        <Link href="/supplies" className="mt-3 inline-block text-sm font-medium text-primary hover:underline">
          Вярнуцца да паставак
        </Link>
      </div>
    );
  }

  if (initialLoading) {
    return <LoadingSpinner label="Загрузка пастаўкі..." />;
  }

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      <div className="rounded-xl border border-gray-200 bg-white px-6 py-4 shadow-sm">
        <p className="text-sm text-gray-700">
          <span className="font-medium text-gray-900">Пастаўшчык:</span> {supplierName}
        </p>
        <p className="mt-1 text-sm text-gray-700">
          <span className="font-medium text-gray-900">Дата пастаўкі:</span> {currentDate}
        </p>
        <div className="mt-4 flex flex-wrap items-center gap-3">
          <button
            type="button"
            onClick={handleSave}
            disabled={saving}
            className="inline-flex items-center gap-2 rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white shadow-sm transition hover:bg-primary-hover disabled:opacity-50"
          >
            {saving && (
              <span
                className="size-4 animate-spin rounded-full border-2 border-white/35 border-t-white"
                aria-hidden
              />
            )}
            {saving ? 'Захоўваю...' : 'Захаваць змены'}
          </button>
          <button
            type="button"
            onClick={refreshFromShopify}
            disabled={refreshing || productDrafts.length === 0}
            className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition hover:bg-gray-50 disabled:opacity-50"
          >
            {refreshing && (
              <span
                className="size-4 animate-spin rounded-full border-2 border-primary/30 border-t-primary"
                aria-hidden
              />
            )}
            {refreshing ? 'Абнаўляю...' : 'Абнавіць з Shopify'}
          </button>
          {saveOk && <p className="text-sm text-emerald-700">{saveOk}</p>}
          {saveError && <p className="text-sm text-red-600">{saveError}</p>}
        </div>
      </div>

      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="overflow-x-auto">
          <table className="min-w-full border-collapse text-left text-sm">
            <thead>
              <tr className="border-b border-gray-200 bg-gray-50/90 text-xs font-semibold uppercase tracking-wide text-gray-500 backdrop-blur-sm">
                <th className="whitespace-nowrap px-6 py-3.5">Назва</th>
                <th className="whitespace-nowrap px-4 py-3.5">Колькасць</th>
                <th className="whitespace-nowrap px-4 py-3.5">Цана пастаўшчыка</th>
                <th className="whitespace-nowrap px-4 py-3.5">VAT %</th>
                <th className="whitespace-nowrap px-4 py-3.5">Наш %</th>
                <th className="whitespace-nowrap px-6 py-3.5">Цана продажу</th>
                <th className="whitespace-nowrap px-4 py-3.5 text-center">Shopify</th>
                <th className="whitespace-nowrap px-4 py-3.5 text-right">Дзеянні</th>
              </tr>
            </thead>
            <tbody className="bg-white">
              {addingProductsLoading ? (
                <tr>
                  <td colSpan={8} className="px-6 py-16 text-center">
                    <LoadingSpinner label="Дадаю выбраныя тавары..." />
                  </td>
                </tr>
              ) : productDrafts.length === 0 ? (
                <tr>
                  <td colSpan={8} className="px-6 py-16 text-center">
                    <p className="text-sm font-medium text-gray-900">Тавары яшчэ не дададзеныя</p>
                    <p className="mt-1 text-sm text-gray-500">Націсніце "Дадаць тавар", каб выбраць прадукты.</p>
                  </td>
                </tr>
              ) : (
                productDrafts.map((row) => {
                  const meta = productMetaMap.get(row.productId);
                  const hasCurrentSupplierName = supplierName.trim().length > 0;
                  const otherSupplierPrices = (meta?.supplierPrices ?? []).filter((price) => {
                    if (Number.isFinite(currentSupplierIdNum) && currentSupplierIdNum > 0) {
                      return price.supplierId !== currentSupplierIdNum;
                    }
                    if (hasCurrentSupplierName) {
                      return price.supplierName.toLowerCase() !== supplierName.trim().toLowerCase();
                    }
                    return true;
                  });
                  const hasOtherSupplierStock = otherSupplierPrices.length > 0;

                  return (
                  <tr
                    key={row.productId}
                    className={`border-b border-gray-100 last:border-b-0 ${
                      hasOtherSupplierStock ? 'bg-purple-100/80' : ''
                    }`}
                  >
                    <td className="px-6 py-3.5 text-gray-900">
                      <div className="space-y-1">
                        <div className="flex items-center gap-2">
                          {hasOtherSupplierStock && (
                            <span className="inline-flex rounded-full bg-purple-700 px-2 py-0.5 text-[11px] font-semibold text-white">
                              Іншы пастаўшчык
                            </span>
                          )}
                          <p>{row.productName}</p>
                        </div>
                        {meta?.variants && meta.variants.length > 0 && (
                          <div className="space-y-0.5 text-xs text-gray-600">
                            {meta.variants.map((variant) => (
                              <p key={variant.variantId || variant.variantName}>- {variant.variantName}</p>
                            ))}
                          </div>
                        )}
                        {hasOtherSupplierStock && (
                          <div className="rounded-md border border-purple-300 bg-purple-100 px-2.5 py-1.5 text-xs text-purple-950">
                            <p className="font-medium">Ёсць у іншых пастаўшчыкоў:</p>
                            <ul className="mt-1 space-y-0.5">
                              {otherSupplierPrices.map((price) => (
                                <li key={`${row.productId}:${price.supplierId}`}>
                                  {price.supplierName}: закуп. {formatMoney(price.supplierPrice)} / продаж{' '}
                                  {formatMoney(price.salePrice)}
                                </li>
                              ))}
                            </ul>
                          </div>
                        )}
                      </div>
                    </td>
                    <td className="px-4 py-3.5">
                      <input
                        type="number"
                        min="0"
                        value={row.quantity}
                        onChange={(e) => updateDraftField(row.productId, 'quantity', e.currentTarget.value)}
                        className="w-24 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-sm text-gray-900 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25"
                      />
                    </td>
                    <td className="px-4 py-3.5">
                      <input
                        type="number"
                        min="0"
                        step="0.01"
                        value={row.supplierPrice}
                        onChange={(e) =>
                          updateDraftField(row.productId, 'supplierPrice', e.currentTarget.value)
                        }
                        className="w-28 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-sm text-gray-900 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25"
                      />
                    </td>
                    <td className="px-4 py-3.5">
                      <select
                        value={row.vatRatePercent}
                        onChange={(e) =>
                          updateDraftField(row.productId, 'vatRatePercent', e.currentTarget.value)
                        }
                        className="w-24 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-sm text-gray-900 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25"
                      >
                        <option value="5">5%</option>
                        <option value="23">23%</option>
                      </select>
                    </td>
                    <td className="px-4 py-3.5">
                      <div className="space-y-1">
                        <input
                          type="number"
                          step="0.01"
                          value={row.marginPercent}
                          onChange={(e) =>
                            updateDraftField(row.productId, 'marginPercent', e.currentTarget.value)
                          }
                          className="w-24 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-sm text-gray-900 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25"
                        />
                      </div>
                    </td>
                    <td className="px-6 py-3.5">
                      <input
                        type="number"
                        min="0"
                        step="0.01"
                        value={row.salePrice}
                        onChange={(e) => updateDraftField(row.productId, 'salePrice', e.currentTarget.value)}
                        className="w-28 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-sm text-gray-900 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25"
                      />
                    </td>
                    <td className="px-4 py-3.5 text-center">
                      <label className="inline-flex items-center gap-2 text-xs text-gray-600">
                        <input
                          type="checkbox"
                          className="size-4 rounded border-gray-300 accent-primary focus:ring-primary"
                          checked={row.syncWithShopify}
                          onChange={() => toggleSyncWithShopify(row.productId)}
                        />
                        <span>Абнаўляць</span>
                      </label>
                    </td>
                    <td className="px-4 py-3.5 text-right">
                      <button
                        type="button"
                        onClick={() => removeDraft(row.productId)}
                        className="inline-flex size-8 items-center justify-center rounded-lg text-gray-500 transition hover:bg-red-50 hover:text-red-700"
                        aria-label={`Выдаліць ${row.productName}`}
                        title="Выдаліць тавар з пастаўкі"
                      >
                        <FiX className="size-4" />
                      </button>
                    </td>
                  </tr>
                )})
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
