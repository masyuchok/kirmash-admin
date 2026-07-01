'use client';

import { useEffect, useState } from 'react';
import {
  calcGrossUnitPrice,
  formatMoneyInput,
  parseMoneyInput,
  parsePercentInput,
} from '@/lib/suppliers/inventoryPricing';
import type { SupplierInventoryRow } from '@/types/supplier-inventory';

type Props = {
  row: SupplierInventoryRow;
  disabled?: boolean;
  onSave: (row: SupplierInventoryRow, values: { netUnitPrice: number; vatRatePercent: number }) => Promise<void>;
};

export default function InventoryPricingCells({ row, disabled = false, onSave }: Props) {
  const [netInput, setNetInput] = useState(formatMoneyInput(row.supplierPrice));
  const [vatInput, setVatInput] = useState(String(row.vatRatePercent));
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setNetInput(formatMoneyInput(row.supplierPrice));
    setVatInput(String(row.vatRatePercent));
  }, [row.supplierPrice, row.vatRatePercent]);

  const parsedNet = parseMoneyInput(netInput);
  const parsedVat = parsePercentInput(vatInput) ?? 0;
  const grossUnitPrice = calcGrossUnitPrice(
    parsedNet ?? row.supplierPrice,
    row.supplierIsVatPayer ? parsedVat : 0,
    row.supplierIsVatPayer
  );

  const commit = async () => {
    if (disabled || saving) return;
    const netUnitPrice = parseMoneyInput(netInput);
    if (netUnitPrice === null || netUnitPrice < 0) {
      setNetInput(formatMoneyInput(row.supplierPrice));
      return;
    }
    const vatRatePercent = row.supplierIsVatPayer
      ? Math.min(100, Math.max(0, parsePercentInput(vatInput) ?? 0))
      : 0;
    const unchanged =
      roundEqual(netUnitPrice, row.supplierPrice) && roundEqual(vatRatePercent, row.vatRatePercent);
    if (unchanged) return;

    setSaving(true);
    try {
      await onSave(row, { netUnitPrice, vatRatePercent });
    } finally {
      setSaving(false);
    }
  };

  const inputClass =
    'w-full min-w-[5.5rem] rounded-md border border-gray-200 bg-white px-2 py-1 text-right text-sm tabular-nums text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20 disabled:opacity-60';

  return (
    <>
      <td className="px-4 py-3 text-right">
        <input
          type="text"
          inputMode="decimal"
          value={netInput}
          disabled={disabled || saving}
          onChange={(e) => setNetInput(e.currentTarget.value)}
          onBlur={() => void commit()}
          className={inputClass}
          aria-label={`Кошт нета: ${row.productName}`}
        />
        {row.hasPriceOverride && (
          <div className="mt-1 text-[10px] uppercase tracking-wide text-primary">зменена</div>
        )}
      </td>
      <td className="px-4 py-3 text-right">
        {row.supplierIsVatPayer ? (
          <div className="inline-flex items-center justify-end gap-1">
            <input
              type="text"
              inputMode="decimal"
              value={vatInput}
              disabled={disabled || saving}
              onChange={(e) => setVatInput(e.currentTarget.value)}
              onBlur={() => void commit()}
              className="w-16 rounded-md border border-gray-200 bg-white px-2 py-1 text-right text-sm tabular-nums text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20 disabled:opacity-60"
              aria-label={`ПДВ %: ${row.productName}`}
            />
            <span className="text-xs text-gray-500">%</span>
          </div>
        ) : (
          <span className="text-sm text-gray-400">—</span>
        )}
      </td>
      <td className="px-4 py-3 text-right text-sm font-medium tabular-nums text-gray-700">
        {formatMoneyInput(grossUnitPrice)}
      </td>
    </>
  );
}

function roundEqual(a: number, b: number): boolean {
  return Math.round(a * 100) === Math.round(b * 100);
}
