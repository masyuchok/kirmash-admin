'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { fetchBukinistkaPendingOffersCount } from '@/lib/api/bukinistka-offers';
import { useEffect, useState } from 'react';

const tabs = [
  { href: '/bukinistka/kirma/offers', label: 'Прапановы', badge: true },
  { href: '/bukinistka/kirma/sales', label: 'Продажы', badge: false },
  {
    href: '/bukinistka/kirma/inventory',
    label: 'Інвентарызацыя',
    badge: false,
  },
] as const;

export default function BukinistkaKirmaTabs({
  children,
}: {
  children: React.ReactNode;
}) {
  const pathname = usePathname();
  const [pendingOffersCount, setPendingOffersCount] = useState(0);

  useEffect(() => {
    let cancelled = false;
    const load = () => {
      fetchBukinistkaPendingOffersCount()
        .then((count) => {
          if (!cancelled) setPendingOffersCount(count);
        })
        .catch(() => {
          if (!cancelled) setPendingOffersCount(0);
        });
    };
    load();
    const onOffersChanged = () => load();
    window.addEventListener('bukinistka-offers-changed', onOffersChanged);
    return () => {
      cancelled = true;
      window.removeEventListener('bukinistka-offers-changed', onOffersChanged);
    };
  }, [pathname]);

  return (
    <div className="mx-auto w-full max-w-6xl space-y-4">
      <div>
        <h1 className="text-xl font-semibold text-gray-900">Kirma.sh</h1>
        <p className="mt-1 text-sm text-gray-600">
          Прапановы, продажы і інвентарызацыя тавараў ад Кірмаша.
        </p>
      </div>

      <div
        className="flex flex-wrap gap-1 border-b border-gray-200"
        role="tablist"
        aria-label="Раздзелы Kirma.sh"
      >
        {tabs.map((tab) => {
          const active =
            pathname === tab.href || pathname?.startsWith(`${tab.href}/`);
          const showBadge = tab.badge && pendingOffersCount > 0;
          return (
            <Link
              key={tab.href}
              href={tab.href}
              role="tab"
              aria-selected={active}
              className={`-mb-px inline-flex items-center gap-2 border-b-2 px-4 py-2.5 text-sm font-medium transition ${
                active
                  ? 'border-amber-700 text-amber-950'
                  : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-800'
              }`}
            >
              {tab.label}
              {showBadge ? (
                <span className="inline-flex min-w-5 items-center justify-center rounded-full bg-red-600 px-1.5 py-0.5 text-[10px] font-semibold leading-none text-white">
                  {pendingOffersCount > 99 ? '99+' : pendingOffersCount}
                </span>
              ) : null}
            </Link>
          );
        })}
      </div>

      <div>{children}</div>
    </div>
  );
}
