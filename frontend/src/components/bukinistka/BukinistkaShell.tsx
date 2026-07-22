'use client';

import BukinistkaLogo from '@/components/brand/BukinistkaLogo';
import { logoutBukinistka } from '@/lib/api/auth';
import { fetchBukinistkaPendingOffersCount } from '@/lib/api/bukinistka-offers';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useEffect, useState } from 'react';
import { FiHome, FiInbox, FiLogOut, FiShoppingBag } from 'react-icons/fi';

const nav = [
  { href: '/bukinistka', label: 'Галоўная', icon: FiHome, exact: true },
  {
    href: '/bukinistka/products',
    label: 'Прадукты',
    icon: FiShoppingBag,
    exact: false,
  },
  {
    href: '/bukinistka/offers',
    label: 'Прапановы ад Кирмаша',
    icon: FiInbox,
    exact: false,
    badgeKey: 'pendingOffers' as const,
  },
] as const;

function navItemActive(
  href: string,
  pathname: string | null,
  exact: boolean
): boolean {
  if (!pathname) return false;
  if (exact) return pathname === href;
  return pathname === href || pathname.startsWith(`${href}/`);
}

function PendingBadge({ count }: { count: number }) {
  if (count <= 0) return null;
  const label = count > 99 ? '99+' : String(count);
  return (
    <span
      className="ml-auto inline-flex min-w-5 shrink-0 items-center justify-center rounded-full bg-red-600 px-1.5 py-0.5 text-[10px] font-semibold leading-none text-white"
      aria-label={`Неразобраных прапаноў: ${count}`}
    >
      {label}
    </span>
  );
}

export default function BukinistkaShell({
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
    const timer = window.setInterval(load, 60_000);
    const onFocus = () => load();
    const onOffersChanged = () => load();
    window.addEventListener('focus', onFocus);
    window.addEventListener('bukinistka-offers-changed', onOffersChanged);

    return () => {
      cancelled = true;
      window.clearInterval(timer);
      window.removeEventListener('focus', onFocus);
      window.removeEventListener('bukinistka-offers-changed', onOffersChanged);
    };
  }, [pathname]);

  return (
    <div className="flex h-screen bg-gray-50/80">
      <aside className="flex w-[260px] shrink-0 flex-col border-r border-gray-200 bg-white">
        <div className="border-b border-gray-100 px-3 py-3">
          <BukinistkaLogo />
          <p className="mt-2 text-center text-[10px] font-medium uppercase tracking-wide text-gray-400">
            Панэль кіравання
          </p>
        </div>
        <nav className="flex-1 overflow-y-auto p-3">
          <ul className="space-y-1">
            {nav.map((item) => {
              const { href, label, icon: Icon, exact } = item;
              const active = navItemActive(href, pathname, exact);
              const badge =
                'badgeKey' in item && item.badgeKey === 'pendingOffers'
                  ? pendingOffersCount
                  : 0;
              return (
                <li key={href}>
                  <Link
                    href={href}
                    className={`flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium transition ${
                      active
                        ? 'bg-amber-50 text-amber-900'
                        : 'text-gray-700 hover:bg-gray-100 hover:text-gray-900'
                    }`}
                    aria-current={active ? 'page' : undefined}
                  >
                    <Icon className="size-5 shrink-0 opacity-80" aria-hidden />
                    <span className="min-w-0 flex-1 truncate">{label}</span>
                    <PendingBadge count={badge} />
                  </Link>
                </li>
              );
            })}
          </ul>
        </nav>
        <div className="border-t border-gray-100 p-3">
          <button
            type="button"
            onClick={() => {
              void logoutBukinistka();
            }}
            className="flex w-full items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium text-gray-700 transition hover:bg-gray-100 hover:text-gray-900"
          >
            <FiLogOut className="size-5 shrink-0 opacity-80" aria-hidden />
            Выйсці
          </button>
        </div>
      </aside>
      <main className="min-h-0 flex-1 overflow-auto p-6 [scrollbar-gutter:stable]">
        {children}
      </main>
    </div>
  );
}
