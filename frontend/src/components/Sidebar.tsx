'use client';

import KirmaLogo from '@/components/brand/KirmaLogo';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import {
  FiBarChart2,
  FiDollarSign,
  FiFileText,
  FiPackage,
  FiSettings,
  FiShoppingBag,
  FiTruck,
  FiTrendingUp,
} from 'react-icons/fi';

const nav = [
  { href: '/', label: 'Аналітыка', icon: FiBarChart2 },
  { href: '/suppliers', label: 'Пастаўшчыкі', icon: FiTruck },
  { href: '/supplies', label: 'Пастаўкі', icon: FiPackage },
  { href: '/products', label: 'Прадукты', icon: FiShoppingBag },
  { href: '/sales', label: 'Продажы', icon: FiTrendingUp },
  { href: '/documents', label: 'Дакументы', icon: FiFileText },
  { href: '/finances', label: 'Фінансы', icon: FiDollarSign },
  { href: '/settings', label: 'Налады', icon: FiSettings },
] as const;

function navItemActive(href: string, pathname: string | null): boolean {
  if (!pathname) return false;
  if (href === '/') return pathname === '/';
  return pathname === href || pathname.startsWith(`${href}/`);
}

const Sidebar = () => {
  const pathname = usePathname();

  return (
    <aside className="flex w-[260px] shrink-0 flex-col border-r border-gray-200 bg-white">
      <div className="border-b border-gray-100 px-3 py-3">
        <KirmaLogo />
        <p className="mt-2 text-center text-[10px] font-medium uppercase tracking-wide text-gray-400">
          Адмін-панэль
        </p>
      </div>
      <nav className="flex-1 overflow-y-auto p-3">
        <ul className="space-y-1">
          {nav.map(({ href, label, icon: Icon }) => {
            const active = navItemActive(href, pathname);
            return (
              <li key={href}>
                <Link
                  href={href}
                  className={`flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium transition ${
                    active
                      ? 'bg-primary/10 text-primary'
                      : 'text-gray-700 hover:bg-gray-100 hover:text-gray-900'
                  } `}
                  aria-current={active ? 'page' : undefined}
                >
                  <Icon className="size-5 shrink-0 opacity-80" aria-hidden />
                  {label}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>
    </aside>
  );
};

export default Sidebar;
