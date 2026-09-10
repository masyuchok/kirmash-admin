'use client';

import BukinistkaSalesList from '@/components/bukinistka/BukinistkaSalesList';
import {
  fetchBukinistkaPortalReceivedSales,
  fetchBukinistkaPortalSentSales,
  syncBukinistkaPortalSales,
  type BukinistkaPortalSale,
} from '@/lib/api/bukinistka-sales';
import { useCallback, useEffect, useState } from 'react';

type SalesSubTabId = 'received' | 'sent';

const subTabs: { id: SalesSubTabId; label: string }[] = [
  { id: 'received', label: 'Прынятага' },
  { id: 'sent', label: 'Высланага' },
];

export default function BukinistkaKirmaSalesClient() {
  const [activeSubTab, setActiveSubTab] = useState<SalesSubTabId>('received');
  const [receivedSales, setReceivedSales] = useState<BukinistkaPortalSale[]>(
    []
  );
  const [sentSales, setSentSales] = useState<BukinistkaPortalSale[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [syncing, setSyncing] = useState(false);

  const loadReceived = useCallback(async () => {
    const rows = await fetchBukinistkaPortalReceivedSales();
    setReceivedSales(rows);
  }, []);

  const loadSent = useCallback(async () => {
    const rows = await fetchBukinistkaPortalSentSales();
    setSentSales(rows);
  }, []);

  const loadActive = useCallback(async () => {
    setError(null);
    if (activeSubTab === 'received') {
      await loadReceived();
    } else {
      await loadSent();
    }
  }, [activeSubTab, loadReceived, loadSent]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    loadActive()
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(
            err instanceof Error ? err.message : 'Памылка загрузкі продажаў'
          );
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [loadActive]);

  const handleSync = async () => {
    setSyncing(true);
    setError(null);
    try {
      const result = await syncBukinistkaPortalSales();
      await Promise.all([loadReceived(), loadSent()]);
      if (result.skipped) {
        setError(
          result.skipReason ||
            'Сінхранізацыя прапушчаная: праверце канфіг Shopify/Odoo.'
        );
      }
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : 'Не ўдалося сінхранізаваць продажы.'
      );
    } finally {
      setSyncing(false);
    }
  };

  return (
    <div className="space-y-4">
      <div
        className="flex flex-wrap gap-1 border-b border-gray-200"
        role="tablist"
        aria-label="Продажы Kirma.sh"
      >
        {subTabs.map((tab) => {
          const active = activeSubTab === tab.id;
          return (
            <button
              key={tab.id}
              type="button"
              role="tab"
              aria-selected={active}
              onClick={() => setActiveSubTab(tab.id)}
              className={`-mb-px border-b-2 px-4 py-2.5 text-sm font-medium transition ${
                active
                  ? 'border-amber-700 text-amber-950'
                  : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-800'
              }`}
            >
              {tab.label}
            </button>
          );
        })}
      </div>

      {activeSubTab === 'received' ? (
        <BukinistkaSalesList
          title="Продажы прынятага тавару"
          description="POS-продажы кніг Кірмаша, якія Букіністка прыняла на склад. Брута — сума, якую Кірма павінен аплаціць."
          orderColumnLabel="Заказ Odoo"
          sales={receivedSales}
          loading={loading}
          error={error}
          syncing={syncing}
          onSync={() => {
            void handleSync();
          }}
        />
      ) : (
        <BukinistkaSalesList
          title="Продажы высланага тавару"
          description="Продажы ў Shopify кніг, якія Букіністка прапанавала Кірмашу і якія ён прыняў."
          orderColumnLabel="Заказ Shopify"
          sales={sentSales}
          loading={loading}
          error={error}
          syncing={syncing}
          onSync={() => {
            void handleSync();
          }}
        />
      )}
    </div>
  );
}
