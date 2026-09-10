'use client';

import type { KirmaBukinistkaOffer } from '@/lib/api/bukinistka-offers';
import {
  calcNetUnitPriceFromGross,
  formatMoneyInput,
  parseMoneyInput,
  recalcMarginBySaleGross,
} from '@/lib/suppliers/inventoryPricing';
import { useEffect, useMemo, useState } from 'react';

type Props = {
  offer: KirmaBukinistkaOffer;
  shopifySalePrice: number;
  vatRate: 5 | 23;
  disabled: boolean;
  onSave: (salePrice: number) => Promise<void>;
};

export default function OfferShopifyPriceCell({
  offer,
  shopifySalePrice,
  vatRate,
  disabled,
  onSave,
}: Props) {
  const [saleInput, setSaleInput] = useState(() =>
    shopifySalePrice > 0 ? formatMoneyInput(shopifySalePrice) : ''
  );
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setSaleInput(
      shopifySalePrice > 0 ? formatMoneyInput(shopifySalePrice) : ''
    );
  }, [offer.id, shopifySalePrice]);

  const parsedSale = parseMoneyInput(saleInput);
  const marginPreview = useMemo(() => {
    if (parsedSale == null || parsedSale <= 0 || offer.grossUnitCost <= 0) {
      return null;
    }
    const netCost = calcNetUnitPriceFromGross(offer.grossUnitCost, vatRate);
    return recalcMarginBySaleGross(netCost, parsedSale, vatRate);
  }, [offer.grossUnitCost, parsedSale, vatRate]);

  const baseline =
    shopifySalePrice > 0 ? Math.round(shopifySalePrice * 100) / 100 : null;
  const hasChanges =
    parsedSale != null &&
    parsedSale >= 0 &&
    (baseline == null || Math.abs(parsedSale - baseline) >= 0.005);

  const handleSave = async () => {
    if (parsedSale == null || parsedSale < 0 || !hasChanges || saving) return;
    setSaving(true);
    try {
      await onSave(parsedSale);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-1">
      <div className="flex items-center gap-1">
        <input
          type="text"
          inputMode="decimal"
          value={saleInput}
          onChange={(e) => setSaleInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              void handleSave();
            }
          }}
          disabled={disabled || saving}
          placeholder="0,00"
          className="w-full min-w-0 rounded-md border border-gray-200 px-2 py-1 text-right text-sm tabular-nums text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-1 disabled:opacity-50"
          aria-label="Цана продажу ў Shopify"
        />
        <button
          type="button"
          disabled={disabled || saving || !hasChanges}
          onClick={() => void handleSave()}
          className="shrink-0 rounded-md border border-gray-200 bg-white px-1.5 py-1 text-[10px] font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-40"
          title="Захаваць цану ў Shopify"
        >
          {saving ? '…' : 'OK'}
        </button>
      </div>
      {marginPreview ? (
        <p className="text-[11px] tabular-nums text-gray-500">
          маржа{' '}
          <span className="font-semibold text-gray-800">
            {marginPreview.marginPercent}%
          </span>
        </p>
      ) : null}
    </div>
  );
}
