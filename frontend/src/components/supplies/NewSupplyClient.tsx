'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import { FiArrowLeft, FiPlus, FiRotateCcw, FiX } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { fetchSupplierOptions } from '@/lib/api/suppliers';
import { fetchProductsWithSuppliers } from '@/lib/api/products';
import { saveSupply } from '@/lib/api/supply-save';
import { deleteSupply, fetchSupplyById } from '@/lib/api/supplies';
import {
  createDraftLinesForProduct,
  createDraftRowFromSupplyProduct,
  displayDraftLabel,
  formatProductNameWithAuthor,
  normalizeSupplyDraftRow,
  readFieldValue,
  type SupplyProductDraft,
} from '@/lib/supply-draft';
import { makeSupplyLineKey, parseSupplyLineKey } from '@/lib/supply-line-key';
import type { ProductWithSuppliers } from '@/types/product';

type Props = {
  initialSupplierId?: string;
  initialSupplierName?: string;
  initialDate: string;
  supplyId?: string;
  selectedProductIds?: string[];
  selectedProductQuantities?: Record<string, string>;
  restoreDraft?: boolean;
};

type SupplierOption = {
  id: number;
  name: string;
  isVatPayer: boolean;
};

function cloneDrafts(rows: SupplyProductDraft[]): SupplyProductDraft[] {
  return rows.map((row) => normalizeSupplyDraftRow(row));
}

type SupplyDraftSessionPayload = {
  productDrafts: SupplyProductDraft[];
  currentSupplierId: string;
  currentSupplierName: string;
  currentDate: string;
};

const SUPPLY_DRAFT_SESSION_PREFIX = 'kirma.supplyDraft.session.v1:';

function supplyDraftSessionKey(supplyId: string | undefined): string {
  return `${SUPPLY_DRAFT_SESSION_PREFIX}${supplyId ?? 'new'}`;
}

function peekDraftSessionAfterPicker(supplyId: string | undefined): SupplyDraftSessionPayload | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.sessionStorage.getItem(supplyDraftSessionKey(supplyId));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as SupplyDraftSessionPayload;
    if (!parsed || !Array.isArray(parsed.productDrafts)) return null;
    return parsed;
  } catch {
    return null;
  }
}

function removeDraftSessionIfPresent(supplyId: string | undefined): void {
  if (typeof window === 'undefined') return;
  try {
    window.sessionStorage.removeItem(supplyDraftSessionKey(supplyId));
  } catch {
    /* ignore */
  }
}

export default function NewSupplyClient({
  initialSupplierId = '',
  initialSupplierName = '',
  initialDate,
  supplyId,
  selectedProductIds = [],
  selectedProductQuantities = {},
  restoreDraft = false,
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

  const formatPercent = (value: number): string => {
    if (!Number.isFinite(value)) return '';
    return String(Math.round(value));
  };

  const roundPercent = (value: number): number => Math.round(value);

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
  const [baselineDrafts, setBaselineDrafts] = useState<SupplyProductDraft[]>([]);
  const [baselineSupplierId, setBaselineSupplierId] = useState('');
  const [baselineSupplierName, setBaselineSupplierName] = useState('');
  const [baselineDate, setBaselineDate] = useState('');
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false);
  const [deleteConfirmStep, setDeleteConfirmStep] = useState<1 | 2>(1);
  const [refreshing, setRefreshing] = useState(false);
  const [addingProductsLoading, setAddingProductsLoading] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveOk, setSaveOk] = useState<string | null>(null);
  const [initialLoading, setInitialLoading] = useState(Boolean(supplyId));

  const handleSaveRef = useRef<() => Promise<void>>(async () => {});
  const handleResetToBaselineRef = useRef<() => void>(() => {});
  const handleDeleteSupplyRef = useRef<() => Promise<void>>(async () => {});

  const persistDraftSessionForNavigation = () => {
    if (typeof window === 'undefined') return;
    try {
      const payload: SupplyDraftSessionPayload = {
        productDrafts: cloneDrafts(productDrafts),
        currentSupplierId,
        currentSupplierName,
        currentDate,
      };
      window.sessionStorage.setItem(supplyDraftSessionKey(supplyId), JSON.stringify(payload));
    } catch {
      /* ignore quota / private mode */
    }
  };

  const openProductPicker = () => {
    persistDraftSessionForNavigation();
    const query = new URLSearchParams();
    if (supplyId) query.set('supplyId', supplyId);
    if (currentSupplierId) query.set('supplierId', currentSupplierId);
    if (currentSupplierName) query.set('supplierName', currentSupplierName);
    if (currentDate) query.set('date', currentDate);
    const currentIds = productDrafts.map((p) => p.lineKey);
    if (currentIds.length > 0) query.set('selectedProductIds', currentIds.join(','));
    if (currentIds.length > 0) {
      const quantitiesPayload = productDrafts.reduce<Record<string, string>>((acc, row) => {
        if (row.quantity.trim()) acc[row.lineKey] = row.quantity;
        return acc;
      }, {});
      query.set('selectedProductQuantities', JSON.stringify(quantitiesPayload));
    }
    router.push(`/supplies/products?${query.toString()}`);
  };

  useEffect(() => {
    setTopbarPage({ title: supplyId ? 'Пастаўка' : 'Новая пастаўка' });
    setTopbarButtons([
      ...(supplyId
        ? [
            {
              label: 'Да спісу паставак',
              icon: <FiArrowLeft />,
              onClick: () => router.push('/supplies'),
              variant: 'secondary' as const,
              disabled: saving || deleting,
              iconOnly: true,
              position: 'left' as const,
            },
          ]
        : []),
      {
        label: saving ? 'Захоўваю...' : 'Захаваць змены',
        icon: saving ? (
          <span
            className="size-4 animate-spin rounded-full border-2 border-white/35 border-t-white"
            aria-hidden
          />
        ) : undefined,
        onClick: () => {
          void handleSaveRef.current();
        },
        variant: 'primary',
        disabled: saving || deleting,
      },
      ...(supplyId
        ? [
            {
              label: deleting ? 'Выдаляю...' : 'Выдаліць пастаўку',
              onClick: () => {
                setDeleteConfirmStep(1);
                setDeleteConfirmOpen(true);
              },
              variant: 'danger' as const,
              disabled: saving || refreshing || deleting,
            },
          ]
        : []),
      ...(supplyId
        ? [
            {
              label: 'Скінуць',
              icon: <FiRotateCcw />,
              onClick: () => handleResetToBaselineRef.current(),
              variant: 'secondary' as const,
              disabled: saving || refreshing || deleting,
            },
          ]
        : []),
    ]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [
    setTopbarButtons,
    setTopbarPage,
    supplyId,
    saving,
    deleting,
    refreshing,
    initialSupplierId,
    router,
  ]);

  useEffect(() => {
    let cancelled = false;
    fetchSupplierOptions()
      .then((rows) => {
        if (!cancelled) setSuppliers(rows);
      })
      .catch(() => {
        if (!cancelled) setSuppliers([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (supplyId) return;
    if (!restoreDraft) return;
    const sessionPayload = peekDraftSessionAfterPicker(undefined);
    if (!sessionPayload) return;

    setCurrentSupplierId(sessionPayload.currentSupplierId || currentSupplierId);
    setCurrentSupplierName(sessionPayload.currentSupplierName || currentSupplierName);
    setCurrentDate(sessionPayload.currentDate || currentDate);

    let cancelled = false;
    fetchProductsWithSuppliers()
      .then((products) => {
        if (cancelled) return;
        setProductCatalog(products);
        const sessionRows = cloneDrafts(sessionPayload.productDrafts);
        setProductDrafts((prev) => {
          const byId = new Map<string, SupplyProductDraft>();
          for (const row of sessionRows) byId.set(row.lineKey, row);
          for (const row of prev) {
            if (!byId.has(row.lineKey)) byId.set(row.lineKey, row);
          }
          const merged = Array.from(byId.values());
          removeDraftSessionIfPresent(undefined);
          return merged;
        });
      })
      .catch(() => {
        if (!cancelled) {
          setProductDrafts(() => {
            const merged = cloneDrafts(sessionPayload.productDrafts);
            removeDraftSessionIfPresent(undefined);
            return merged;
          });
        }
      });

    return () => {
      cancelled = true;
    };
  }, [supplyId, restoreDraft]);

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
        const sessionPayload = restoreDraft ? peekDraftSessionAfterPicker(supplyId) : null;
        setProductCatalog(products);
        setCurrentSupplierId(String(supply.supplierId || ''));
        setCurrentSupplierName(supply.supplierName || '');
        setCurrentDate(supply.date || '');

        const productMap = new Map(products.map((p) => [p.shopifyProductId, p]));
        const supplierIdStr = String(supply.supplierId || '');
        const supplierNm = supply.supplierName || '';
        const dateStr = supply.date || '';

        const dbDrafts: SupplyProductDraft[] = supply.products.map((p) =>
          createDraftRowFromSupplyProduct(p, productMap, normalizeVatRateOption)
        );
        setBaselineDrafts(cloneDrafts(dbDrafts));
        setBaselineSupplierId(supplierIdStr);
        setBaselineSupplierName(supplierNm);
        setBaselineDate(dateStr);
        if (sessionPayload) {
          setCurrentSupplierId(sessionPayload.currentSupplierId || supplierIdStr);
          setCurrentSupplierName(sessionPayload.currentSupplierName || supplierNm);
          setCurrentDate(sessionPayload.currentDate || dateStr);
        }
        // Prefer in-memory drafts over DB for the same productId so returning from the picker
        // does not wipe unsaved edits.
        setProductDrafts(() => {
          const byId = new Map<string, SupplyProductDraft>();
          if (sessionPayload) {
            for (const row of dbDrafts) byId.set(row.lineKey, row);
            for (const row of cloneDrafts(sessionPayload.productDrafts)) byId.set(row.lineKey, row);
            removeDraftSessionIfPresent(supplyId);
          } else {
            for (const row of dbDrafts) byId.set(row.lineKey, row);
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
  }, [supplyId, restoreDraft]);

  useEffect(() => {
    if (selectedProductIds.length === 0) {
      setAddingProductsLoading(false);
      return;
    }
    if (supplyId && initialLoading) return;
    let cancelled = false;
    setAddingProductsLoading(true);
    const selectedKeys = new Set(selectedProductIds);
    const productIds = new Set(
      selectedProductIds.map((key) => parseSupplyLineKey(key).productId).filter(Boolean)
    );
    fetchProductsWithSuppliers()
      .then((rows) => {
        if (cancelled) return;
        setProductCatalog(rows);
        const selected = rows.filter((p) => productIds.has(p.shopifyProductId));
        setSelectedProducts(selected);
        setProductDrafts((prev) => {
          const prevMap = new Map(prev.map((row) => [row.lineKey, row]));
          const next = [...prev];
          for (const product of selected) {
            const lines = createDraftLinesForProduct(
              product,
              selectedProductQuantities,
              resolveDefaultVatRatePercent(product.productType)
            );
            for (const line of lines) {
              const shouldAdd =
                selectedKeys.has(line.lineKey) || selectedKeys.has(product.shopifyProductId);
              if (shouldAdd && !prevMap.has(line.lineKey)) {
                next.push(line);
                prevMap.set(line.lineKey, line);
              }
            }
          }
          return next;
        });
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
  }, [selectedProductIds, selectedProductQuantities, supplyId, initialLoading]);

  const updateDraftField = (
    lineKey: string,
    field: 'quantity' | 'supplierPrice' | 'vatRatePercent' | 'marginPercent' | 'salePrice',
    value: string
  ) => {
    const selectedSupplier = suppliers.find((s) => String(s.id) === currentSupplierId);
    const isSupplierVatPayer = selectedSupplier?.isVatPayer ?? true;

    const recalcByMargin = (
      supplierPriceValue: number,
      marginPct: number,
      vatRatePercentValue: number
    ): { saleGross: number; vatAmount: number; netSale: number } => {
      const marginFactor = 1 - marginPct / 100;
      if (!isSupplierVatPayer) {
        const saleNet =
          marginFactor > 0 ? round2(supplierPriceValue / marginFactor) : supplierPriceValue;
        const saleGross = round2((saleNet * (100 + vatRatePercentValue)) / 100);
        const vatGrossPart = round2(saleGross - saleNet);
        return { saleGross, vatAmount: vatGrossPart, netSale: saleNet };
      }

      const saleGross =
        marginFactor > 0 ? round2(supplierPriceValue / marginFactor) : supplierPriceValue;
      const saleNet = round2((saleGross * 100) / (100 + vatRatePercentValue));
      const vatGrossPart = round2(saleGross - saleNet);
      return { saleGross, vatAmount: vatGrossPart, netSale: saleNet };
    };

    const recalcByGross = (
      supplierPriceValue: number,
      saleGross: number,
      vatRatePercentValue: number
    ): { marginPct: number; vatAmount: number; netSale: number } => {
      const vatRate = vatRatePercentValue / 100;
      const saleNet = round2((saleGross * 100) / (100 + vatRatePercentValue));
      const vatGrossPart = round2(saleGross - saleNet);
      const marginPct = isSupplierVatPayer
        ? saleGross > 0
          ? roundPercent(((saleGross - supplierPriceValue) / saleGross) * 100)
          : 0
        : saleNet > 0
          ? roundPercent(((saleNet - supplierPriceValue) / saleNet) * 100)
          : 0;
      return { marginPct, vatAmount: vatGrossPart, netSale: saleNet };
    };

    setProductDrafts((prev) =>
      prev.map((row) => {
        if (row.lineKey !== lineKey) return row;

        const next: SupplyProductDraft = { ...row, [field]: value };
        if (field === 'marginPercent') {
          const parsedMargin = parseDecimal(value);
          if (parsedMargin !== null) {
            next.marginPercent = formatPercent(parsedMargin);
          }
        }
        const supplierPrice = parseDecimal(next.supplierPrice);
        const vatRatePercent = parseDecimal(next.vatRatePercent);
        const marginPercent = parseDecimal(next.marginPercent);
        const salePrice = parseDecimal(next.salePrice);
        const vatRatePercentValue = vatRatePercent ?? 23;

        // If user edits margin, auto-calc sale price (gross) from net margin logic.
        if (field === 'marginPercent' && supplierPrice !== null && marginPercent !== null) {
          const calculated = recalcByMargin(supplierPrice, marginPercent, vatRatePercentValue);
          next.salePrice = formatDecimal(calculated.saleGross);
        }

        // If user edits sale price (gross), auto-calc margin from net sale.
        if (field === 'salePrice' && supplierPrice !== null && supplierPrice > 0 && salePrice !== null) {
          const calculated = recalcByGross(supplierPrice, salePrice, vatRatePercentValue);
          next.marginPercent = formatPercent(calculated.marginPct);
        }

        // If supplier net price changes, keep sale/margin synced.
        if (field === 'supplierPrice' && supplierPrice !== null && supplierPrice > 0) {
          if (marginPercent !== null) {
            const calculated = recalcByMargin(supplierPrice, marginPercent, vatRatePercentValue);
            next.salePrice = formatDecimal(calculated.saleGross);
          } else if (salePrice !== null) {
            const calculated = recalcByGross(supplierPrice, salePrice, vatRatePercentValue);
            next.marginPercent = formatPercent(calculated.marginPct);
          }
        }
        if (field === 'vatRatePercent' && supplierPrice !== null && supplierPrice > 0) {
          if (marginPercent !== null) {
            const calculated = recalcByMargin(supplierPrice, marginPercent, vatRatePercentValue);
            next.salePrice = formatDecimal(calculated.saleGross);
          } else if (salePrice !== null) {
            const calculated = recalcByGross(supplierPrice, salePrice, vatRatePercentValue);
            next.marginPercent = formatPercent(calculated.marginPct);
          }
        }

        return next;
      })
    );
  };

  const removeDraft = (lineKey: string) => {
    const { productId } = parseSupplyLineKey(lineKey);
    setProductDrafts((prev) => {
      const next = prev.filter((row) => row.lineKey !== lineKey);
      const stillHasProduct = next.some((row) => row.productId === productId);
      if (!stillHasProduct) {
        setSelectedProducts((sp) => sp.filter((row) => row.shopifyProductId !== productId));
      }
      return next;
    });
  };

  const toggleSyncWithShopify = (lineKey: string) => {
    setProductDrafts((prev) =>
      prev.map((row) =>
        row.lineKey === lineKey ? { ...row, syncWithShopify: !row.syncWithShopify } : row
      )
    );
  };

  const handleResetToBaseline = () => {
    if (!supplyId) return;
    setSaveError(null);
    setSaveOk(null);
    removeDraftSessionIfPresent(supplyId);
    setCurrentSupplierId(baselineSupplierId);
    setCurrentSupplierName(baselineSupplierName);
    setCurrentDate(baselineDate);
    setProductDrafts(cloneDrafts(baselineDrafts));
    const allowedProducts = new Set(baselineDrafts.map((r) => r.productId));
    setSelectedProducts((prev) => prev.filter((p) => allowedProducts.has(p.shopifyProductId)));
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
        setSaveError(`Праверце колькасць для "${displayDraftLabel(row)}" (павінна быць > 0).`);
        return;
      }
      if (!Number.isFinite(supplierPrice) || supplierPrice < 0) {
        setSaveError(`Праверце цану пастаўшчыка для "${displayDraftLabel(row)}".`);
        return;
      }
      if (!Number.isFinite(marginPercent) || marginPercent < 0) {
        setSaveError(`Праверце "Маржа" для "${displayDraftLabel(row)}".`);
        return;
      }
      if (!Number.isFinite(vatRatePercent) || !VAT_RATE_OPTIONS.includes(vatRatePercent as 5 | 23)) {
        setSaveError(`Праверце "VAT %" для "${displayDraftLabel(row)}" (5% або 23%).`);
        return;
      }
      if (!Number.isFinite(salePrice) || salePrice < 0) {
        setSaveError(`Праверце цану продажу для "${displayDraftLabel(row)}".`);
        return;
      }
    }

    setSaving(true);
    setSaveError(null);
    setSaveOk(null);
    try {
      const payloadProducts = productDrafts.map((row) => ({
        shopifyProductId: row.productId,
        shopifyVariantId: row.variantId || undefined,
        quantity: Number(row.quantity || 0),
        supplierPrice: Number(row.supplierPrice || 0),
        vatRatePercent: Number(row.vatRatePercent || 0),
        marginPercent: roundPercent(Number(row.marginPercent || 0)),
        salePrice: Number(row.salePrice || 0),
        syncWithShopify: row.syncWithShopify,
      }));

      const result = await saveSupply({
        supplyId: supplyId ? Number(supplyId) : undefined,
        supplierId: supplierIdNumber,
        date: currentDate,
        products: payloadProducts,
      });

      removeDraftSessionIfPresent(supplyId);
      if (!supplyId) removeDraftSessionIfPresent(undefined);

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
      const savedDrafts = cloneDrafts(productDrafts);

      if (!supplyId && result.id > 0) {
        const query = new URLSearchParams();
        query.set('supplierId', String(supplierIdNumber));
        if (currentSupplierName) query.set('supplierName', currentSupplierName);
        query.set('date', currentDate);
        router.replace(`/supplies/${result.id}?${query.toString()}`);
      } else if (supplyId) {
        try {
          const supply = await fetchSupplyById(Number(supplyId));
          const productMap = new Map(productCatalog.map((p) => [p.shopifyProductId, p]));
          const dbDrafts = supply.products.map((p) =>
            createDraftRowFromSupplyProduct(p, productMap, normalizeVatRateOption)
          );
          const synced = cloneDrafts(dbDrafts.length > 0 ? dbDrafts : savedDrafts);
          setProductDrafts(synced);
          setBaselineDrafts(synced);
        } catch {
          setProductDrafts(savedDrafts);
          setBaselineDrafts(savedDrafts);
        }
        setBaselineSupplierId(currentSupplierId);
        setBaselineSupplierName(currentSupplierName);
        setBaselineDate(currentDate);
      }
    } catch (err: unknown) {
      setSaveError(err instanceof Error ? err.message : 'Памылка захавання');
    } finally {
      setSaving(false);
    }
  };

  handleSaveRef.current = handleSave;
  handleResetToBaselineRef.current = handleResetToBaseline;
  handleDeleteSupplyRef.current = async () => {
    if (!supplyId) return;

    setDeleting(true);
    setSaveError(null);
    setSaveOk(null);
    try {
      await deleteSupply(Number(supplyId));
      removeDraftSessionIfPresent(supplyId);
      setDeleteConfirmOpen(false);
      router.push('/supplies');
    } catch (err: unknown) {
      setSaveError(err instanceof Error ? err.message : 'Памылка выдалення пастаўкі');
    } finally {
      setDeleting(false);
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
          const variantMatch = live.variants.find((v) => v.variantId === row.variantId);
          return {
            ...row,
            productName: live.productName,
            productType: live.productType,
            variantName: variantMatch?.variantName ?? row.variantName,
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

  if (initialLoading) {
    return <LoadingSpinner label="Загрузка пастаўкі..." />;
  }

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      <div className="rounded-xl border border-gray-200 bg-white px-6 py-4 shadow-sm">
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="space-y-1">
            <span className="text-sm font-medium text-gray-900">Пастаўшчык</span>
            <select
              value={currentSupplierId}
              onChange={(e) => {
                const nextId = readFieldValue(e);
                setCurrentSupplierId(nextId);
                const match = suppliers.find((s) => String(s.id) === nextId);
                setCurrentSupplierName(match?.name ?? '');
              }}
              disabled={saving}
              className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25 disabled:opacity-60"
            >
              <option value="">Выберыце пастаўшчыка</option>
              {suppliers.map((supplier) => (
                <option key={supplier.id} value={String(supplier.id)}>
                  {supplier.name}
                </option>
              ))}
              {currentSupplierId &&
                !suppliers.some((supplier) => String(supplier.id) === currentSupplierId) && (
                  <option value={currentSupplierId}>{supplierName}</option>
                )}
            </select>
          </label>
          <label className="space-y-1">
            <span className="text-sm font-medium text-gray-900">Дата пастаўкі</span>
            <input
              type="date"
              value={currentDate}
              onChange={(e) => setCurrentDate(readFieldValue(e))}
              disabled={saving}
              className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25 disabled:opacity-60"
            />
          </label>
        </div>
        <div className="mt-4 flex flex-wrap items-center gap-3">
          <button
            type="button"
            onClick={openProductPicker}
            disabled={saving}
            className="inline-flex size-10 items-center justify-center rounded-lg bg-primary text-white shadow-sm transition hover:bg-primary-hover disabled:opacity-50"
            aria-label="Дадаць тавар"
            title="Дадаць тавар"
          >
            <FiPlus className="size-5" />
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
                <th className="whitespace-nowrap px-4 py-3.5 w-14"></th>
                <th className="whitespace-nowrap px-6 py-3.5">Назва</th>
                <th className="whitespace-nowrap px-4 py-3.5">Колькасць</th>
                <th className="whitespace-nowrap px-4 py-3.5">Цана пастаўшчыка</th>
                <th className="whitespace-nowrap px-4 py-3.5">VAT %</th>
                <th className="whitespace-nowrap px-4 py-3.5">Маржа</th>
                <th className="whitespace-nowrap px-6 py-3.5">Цана продажу</th>
                <th className="whitespace-nowrap px-4 py-3.5 text-center">Shopify</th>
                <th className="whitespace-nowrap px-4 py-3.5 text-right">Дзеянні</th>
              </tr>
            </thead>
            <tbody className="bg-white">
              {addingProductsLoading ? (
                <tr>
                  <td colSpan={9} className="px-6 py-16 text-center">
                    <LoadingSpinner label="Дадаю выбраныя тавары..." />
                  </td>
                </tr>
              ) : productDrafts.length === 0 ? (
                <tr>
                  <td colSpan={9} className="px-6 py-16 text-center">
                    <p className="text-sm font-medium text-gray-900">Тавары яшчэ не дададзеныя</p>
                    <p className="mt-1 text-sm text-gray-500">Націсніце «+» пад датай пастаўкі, каб выбраць прадукты.</p>
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
                  const author = meta?.productAuthor?.trim() ?? '';
                  const titleLabel = row.variantName.trim()
                    ? formatProductNameWithAuthor(row.productName, author)
                    : displayDraftLabel(row, author);

                  return (
                  <tr
                    key={row.lineKey}
                    className={`border-b border-gray-100 last:border-b-0 ${
                      hasOtherSupplierStock ? 'bg-purple-100/80' : ''
                    }`}
                  >
                    <td className="px-4 py-3.5">
                      {meta?.mainImageUrl ? (
                        <a
                          href={meta.mainImageUrl}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex overflow-hidden rounded-md border border-gray-200 bg-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40"
                          aria-label={`Адкрыць выяву: ${titleLabel}`}
                        >
                          <img
                            src={meta.mainImageUrl}
                            alt={row.productName}
                            className="h-12 w-8 object-cover object-center"
                            loading="lazy"
                          />
                        </a>
                      ) : (
                        <div className="h-12 w-8 rounded-md border border-gray-200 bg-gray-100" aria-hidden />
                      )}
                    </td>
                    <td className="px-6 py-3.5 text-gray-900">
                      <div className="space-y-1">
                        <div className="flex items-center gap-2">
                          {hasOtherSupplierStock && (
                            <span className="inline-flex rounded-full bg-purple-700 px-2 py-0.5 text-[11px] font-semibold text-white">
                              Іншы пастаўшчык
                            </span>
                          )}
                          <p>{titleLabel}</p>
                        </div>
                        {row.variantName.trim() && (
                          <p className="text-xs text-gray-600">{row.variantName}</p>
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
                        onChange={(e) => updateDraftField(row.lineKey, 'quantity', readFieldValue(e))}
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
                          updateDraftField(row.lineKey, 'supplierPrice', readFieldValue(e))
                        }
                        className="w-28 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-sm text-gray-900 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25"
                      />
                    </td>
                    <td className="px-4 py-3.5">
                      <select
                        value={row.vatRatePercent}
                        onChange={(e) =>
                          updateDraftField(row.lineKey, 'vatRatePercent', readFieldValue(e))
                        }
                        className="w-24 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-sm text-gray-900 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25"
                      >
                        <option value="5">5%</option>
                        <option value="23">23%</option>
                      </select>
                    </td>
                    <td className="px-4 py-3.5">
                      <div className="relative inline-flex w-24 items-center">
                        <input
                          type="number"
                          step="1"
                          value={row.marginPercent}
                          onChange={(e) =>
                            updateDraftField(row.lineKey, 'marginPercent', readFieldValue(e))
                          }
                          className="w-full rounded-lg border border-gray-200 bg-white py-1.5 pl-2.5 pr-7 text-sm text-gray-900 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25"
                        />
                        <span
                          className="pointer-events-none absolute right-2.5 text-sm text-gray-500"
                          aria-hidden
                        >
                          %
                        </span>
                      </div>
                    </td>
                    <td className="px-6 py-3.5">
                      <input
                        type="number"
                        min="0"
                        step="0.01"
                        value={row.salePrice}
                        onChange={(e) => updateDraftField(row.lineKey, 'salePrice', readFieldValue(e))}
                        className="w-28 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-sm text-gray-900 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25"
                      />
                    </td>
                    <td className="px-4 py-3.5 text-center">
                      <label className="inline-flex items-center gap-2 text-xs text-gray-600">
                        <input
                          type="checkbox"
                          className="size-4 rounded border-gray-300 accent-primary focus:ring-primary"
                          checked={row.syncWithShopify}
                          onChange={() => toggleSyncWithShopify(row.lineKey)}
                        />
                        <span>Абнаўляць</span>
                      </label>
                    </td>
                    <td className="px-4 py-3.5 text-right">
                      <button
                        type="button"
                        onClick={() => removeDraft(row.lineKey)}
                        className="inline-flex size-8 items-center justify-center rounded-lg text-gray-500 transition hover:bg-red-50 hover:text-red-700"
                        aria-label={`Выдаліць ${displayDraftLabel(row, author)}`}
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

      {deleteConfirmOpen && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
          role="dialog"
          aria-modal="true"
          aria-labelledby="delete-supply-title"
        >
          <div className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-6 shadow-xl">
            <h2 id="delete-supply-title" className="text-lg font-semibold text-gray-900">
              {deleteConfirmStep === 1 ? 'Выдаліць пастаўку?' : 'Апошняе пацвярджэнне'}
            </h2>
            <p className="mt-3 text-sm text-gray-700">
              {deleteConfirmStep === 1
                ? 'Пасля выдалення вярнуць пастаўку будзе нельга.'
                : 'Вы сапраўды хочаце незваротна выдаліць пастаўку?'}
            </p>
            <div className="mt-6 flex flex-wrap justify-end gap-3">
              <button
                type="button"
                onClick={() => {
                  setDeleteConfirmOpen(false);
                  setDeleteConfirmStep(1);
                }}
                disabled={deleting}
                className="rounded-lg border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition hover:bg-gray-50 disabled:opacity-60"
              >
                Адмена
              </button>
              {deleteConfirmStep === 1 ? (
                <button
                  type="button"
                  onClick={() => setDeleteConfirmStep(2)}
                  disabled={deleting}
                  className="rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-700 shadow-sm transition hover:bg-red-100 disabled:opacity-60"
                >
                  Працягнуць
                </button>
              ) : (
                <button
                  type="button"
                  onClick={() => {
                    void handleDeleteSupplyRef.current();
                  }}
                  disabled={deleting}
                  className="rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-700 shadow-sm transition hover:bg-red-100 disabled:opacity-60"
                >
                  {deleting ? 'Выдаляю...' : 'Выдаліць назаўжды'}
                </button>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
