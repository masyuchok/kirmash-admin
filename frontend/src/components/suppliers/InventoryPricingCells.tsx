'use client';

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import {
  calcGrossUnitPrice,
  formatMoneyInput,
  normalizeCatalogVatRate,
  parseMoneyInput,
  parsePercentInput,
  recalcMarginBySaleGross,
  recalcSaleByMargin,
  resolveDisplaySalePrice,
  roundPercent,
} from '@/lib/suppliers/inventoryPricing';
import { formatInventoryProductTitle } from '@/lib/suppliers/inventoryTree';
import type { SupplierInventoryRow } from '@/types/supplier-inventory';

export type InventoryPricingSaveValues = {
  netUnitPrice: number;
  vatRatePercent: number;
  marginPercent: number;
  salePrice: number;
  syncWithShopify: boolean;
};

type EditorContextValue = {
  row: SupplierInventoryRow;
  disabled: boolean;
  saving: boolean;
  hasChanges: boolean;
  save: () => Promise<void>;
  netInput: string;
  setNetInput: (value: string) => void;
  vatRate: 5 | 23;
  applyVatChange: (next: 5 | 23) => void;
  grossUnitPrice: number;
  marginInput: string;
  applyMarginChange: (value: string) => void;
  marginPreview: { saleNet: number } | null;
  parsedSale: number | null;
  netUnitPrice: number;
  saleInput: string;
  applySaleChange: (value: string) => void;
  recalcFromNetOrVat: () => void;
};

const EditorContext = createContext<EditorContextValue | null>(null);

function useEditorContext(): EditorContextValue {
  const ctx = useContext(EditorContext);
  if (!ctx) {
    throw new Error('InventoryPricingEditorProvider is required');
  }
  return ctx;
}

type ProviderProps = {
  row: SupplierInventoryRow;
  disabled?: boolean;
  onSave: (
    row: SupplierInventoryRow,
    values: InventoryPricingSaveValues
  ) => Promise<void>;
  children: ReactNode;
};

export function InventoryPricingEditorProvider({
  row,
  disabled = false,
  onSave,
  children,
}: ProviderProps) {
  const [netInput, setNetInput] = useState(formatMoneyInput(row.supplierPrice));
  const [vatRate, setVatRate] = useState<5 | 23>(
    normalizeCatalogVatRate(row.vatRatePercent)
  );
  const [marginInput, setMarginInput] = useState(
    String(Math.round(row.marginPercent))
  );
  const [saleInput, setSaleInput] = useState(
    formatMoneyInput(resolveDisplaySalePrice(row))
  );
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setNetInput(formatMoneyInput(row.supplierPrice));
    setVatRate(normalizeCatalogVatRate(row.vatRatePercent));
    setMarginInput(String(Math.round(row.marginPercent)));
    setSaleInput(formatMoneyInput(resolveDisplaySalePrice(row)));
  }, [
    row.supplierPrice,
    row.vatRatePercent,
    row.marginPercent,
    row.salePrice,
    row.shopifyPrice,
  ]);

  const baselineSalePrice = resolveDisplaySalePrice(row);

  const parsedNet = parseMoneyInput(netInput);
  const parsedMargin = parsePercentInput(marginInput);
  const parsedSale = parseMoneyInput(saleInput);
  const netUnitPrice = parsedNet ?? row.supplierPrice;
  const grossUnitPrice = calcGrossUnitPrice(
    netUnitPrice,
    vatRate,
    row.supplierIsVatPayer
  );
  const marginPreview =
    parsedSale !== null && netUnitPrice > 0
      ? recalcMarginBySaleGross(netUnitPrice, parsedSale, vatRate)
      : parsedMargin !== null && netUnitPrice > 0
        ? recalcSaleByMargin(netUnitPrice, parsedMargin, vatRate)
        : null;

  const hasChanges = useMemo(() => {
    const net = parseMoneyInput(netInput);
    const margin = parsePercentInput(marginInput);
    const sale = parseMoneyInput(saleInput);
    if (net === null || margin === null || sale === null) return true;
    return (
      !roundEqual(net, row.supplierPrice) ||
      normalizeCatalogVatRate(vatRate) !==
        normalizeCatalogVatRate(row.vatRatePercent) ||
      !roundEqual(margin, row.marginPercent) ||
      !roundEqual(sale, baselineSalePrice)
    );
  }, [
    netInput,
    vatRate,
    marginInput,
    saleInput,
    row.supplierPrice,
    row.vatRatePercent,
    row.marginPercent,
    baselineSalePrice,
  ]);

  const recalcFromNetOrVat = useCallback(() => {
    const net = parseMoneyInput(netInput) ?? row.supplierPrice;
    const margin = parsePercentInput(marginInput);
    const sale = parseMoneyInput(saleInput);
    if (net <= 0) return;
    if (margin !== null) {
      const calculated = recalcSaleByMargin(net, margin, vatRate);
      setSaleInput(formatMoneyInput(calculated.saleGross));
    } else if (sale !== null) {
      const calculated = recalcMarginBySaleGross(net, sale, vatRate);
      setMarginInput(String(calculated.marginPercent));
    }
  }, [marginInput, netInput, row.supplierPrice, saleInput, vatRate]);

  const applyMarginChange = useCallback(
    (value: string) => {
      setMarginInput(value);
      const net = parseMoneyInput(netInput) ?? row.supplierPrice;
      const margin = parsePercentInput(value);
      if (net > 0 && margin !== null) {
        const calculated = recalcSaleByMargin(net, margin, vatRate);
        setSaleInput(formatMoneyInput(calculated.saleGross));
      }
    },
    [netInput, row.supplierPrice, vatRate]
  );

  const applySaleChange = useCallback(
    (value: string) => {
      setSaleInput(value);
      const net = parseMoneyInput(netInput) ?? row.supplierPrice;
      const sale = parseMoneyInput(value);
      if (net > 0 && sale !== null) {
        const calculated = recalcMarginBySaleGross(net, sale, vatRate);
        setMarginInput(String(calculated.marginPercent));
      }
    },
    [netInput, row.supplierPrice, vatRate]
  );

  const applyVatChange = useCallback(
    (next: 5 | 23) => {
      setVatRate(next);
      const net = parseMoneyInput(netInput) ?? row.supplierPrice;
      const margin = parsePercentInput(marginInput);
      if (net > 0 && margin !== null) {
        const calculated = recalcSaleByMargin(net, margin, next);
        setSaleInput(formatMoneyInput(calculated.saleGross));
      }
    },
    [marginInput, netInput, row.supplierPrice]
  );

  const save = useCallback(async () => {
    if (disabled || saving) return;
    const net = parseMoneyInput(netInput);
    if (net === null || net < 0) {
      setNetInput(formatMoneyInput(row.supplierPrice));
      return;
    }
    const margin = Math.max(
      0,
      parsePercentInput(marginInput) ?? row.marginPercent
    );
    const sale = parseMoneyInput(saleInput);
    if (sale === null || sale < 0) {
      setSaleInput(formatMoneyInput(baselineSalePrice));
      return;
    }

    const syncWithShopify = sale > 0 && !roundEqual(sale, row.shopifyPrice);

    setSaving(true);
    try {
      await onSave(row, {
        netUnitPrice: net,
        vatRatePercent: vatRate,
        marginPercent: roundPercent(margin),
        salePrice: sale,
        syncWithShopify,
      });
    } finally {
      setSaving(false);
    }
  }, [
    disabled,
    marginInput,
    netInput,
    onSave,
    row,
    saleInput,
    saving,
    vatRate,
    baselineSalePrice,
  ]);

  const value: EditorContextValue = {
    row,
    disabled,
    saving,
    hasChanges,
    save,
    netInput,
    setNetInput,
    vatRate,
    applyVatChange,
    grossUnitPrice,
    marginInput,
    applyMarginChange,
    marginPreview,
    parsedSale,
    netUnitPrice,
    saleInput,
    applySaleChange,
    recalcFromNetOrVat,
  };

  return (
    <EditorContext.Provider value={value}>{children}</EditorContext.Provider>
  );
}

const inputClass =
  'w-full rounded-md border border-gray-200 bg-white px-2 py-1 text-right text-sm tabular-nums text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20 disabled:opacity-60';

export default function InventoryPricingCells() {
  const {
    row,
    disabled,
    saving,
    netInput,
    setNetInput,
    vatRate,
    applyVatChange,
    grossUnitPrice,
    marginInput,
    applyMarginChange,
    marginPreview,
    parsedSale,
    netUnitPrice,
    saleInput,
    applySaleChange,
    recalcFromNetOrVat,
  } = useEditorContext();

  return (
    <>
      <td className="whitespace-nowrap px-3 py-3 text-right align-top">
        <input
          type="text"
          inputMode="decimal"
          value={netInput}
          disabled={disabled || saving}
          onChange={(e) => setNetInput(e.currentTarget.value)}
          onBlur={recalcFromNetOrVat}
          className={`${inputClass} min-w-[5.5rem]`}
          aria-label={`Кошт нета: ${formatInventoryProductTitle(row)}`}
        />
        {row.hasPriceOverride && (
          <div className="mt-1 text-[10px] uppercase tracking-wide text-primary">
            зменена
          </div>
        )}
      </td>
      <td className="whitespace-nowrap px-3 py-3 text-right align-top">
        <select
          value={vatRate}
          disabled={disabled || saving}
          onChange={(e) =>
            applyVatChange(
              normalizeCatalogVatRate(Number(e.currentTarget.value))
            )
          }
          className="min-w-[4.5rem] rounded-md border border-gray-200 bg-white px-1.5 py-1 text-right text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20 disabled:opacity-60"
          aria-label={`ПДВ %: ${formatInventoryProductTitle(row)}`}
        >
          <option value={5}>5%</option>
          <option value={23}>23%</option>
        </select>
      </td>
      <td className="whitespace-nowrap px-3 py-3 text-right align-top text-sm font-medium tabular-nums text-gray-700">
        {formatMoneyInput(grossUnitPrice)}
      </td>
      <td className="whitespace-nowrap px-3 py-3 text-right align-top">
        <div className="relative inline-flex min-w-[4.5rem] items-center">
          <input
            type="text"
            inputMode="decimal"
            value={marginInput}
            disabled={disabled || saving}
            onChange={(e) => applyMarginChange(e.currentTarget.value)}
            className={`${inputClass} pr-5`}
            aria-label={`Маржа: ${formatInventoryProductTitle(row)}`}
          />
          <span className="pointer-events-none absolute right-2 text-xs text-gray-500">
            %
          </span>
        </div>
        {marginPreview && parsedSale !== null && netUnitPrice > 0 && (
          <div className="mt-1 text-[10px] tabular-nums text-gray-500">
            нет {formatMoneyInput(marginPreview.saleNet - netUnitPrice)}
          </div>
        )}
      </td>
      <td className="whitespace-nowrap px-3 py-3 text-right align-top">
        <input
          type="text"
          inputMode="decimal"
          value={saleInput}
          disabled={disabled || saving}
          onChange={(e) => applySaleChange(e.currentTarget.value)}
          className={`${inputClass} min-w-[5.5rem]`}
          aria-label={`Цана продажу: ${formatInventoryProductTitle(row)}`}
        />
      </td>
    </>
  );
}

export function InventoryPricingSaveButton() {
  const { saving, hasChanges, save, disabled } = useEditorContext();

  return (
    <button
      type="button"
      disabled={disabled || saving || !hasChanges}
      onClick={() => void save()}
      className="inline-flex items-center justify-center rounded-lg border border-primary/30 bg-primary/10 px-2.5 py-1.5 text-xs font-medium text-primary transition hover:bg-primary/15 disabled:cursor-not-allowed disabled:opacity-50"
    >
      {saving ? '…' : 'Захаваць'}
    </button>
  );
}

function roundEqual(a: number, b: number): boolean {
  return Math.round(a * 100) === Math.round(b * 100);
}
