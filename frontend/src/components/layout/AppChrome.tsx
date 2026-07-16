'use client';

import Sidebar from '@/components/Sidebar';
import Topbar from '@/components/topbar/Topbar';
import { TopbarProvider } from '@/components/topbar/TopbarContext';
import BukinistkaShell from '@/components/bukinistka/BukinistkaShell';
import { usePathname } from 'next/navigation';

export default function AppChrome({ children }: { children: React.ReactNode }) {
  const pathname = usePathname() ?? '';
  const isLogin = pathname === '/login' || pathname.startsWith('/login/');
  const isBukinistka =
    pathname === '/bukinistka' || pathname.startsWith('/bukinistka/');

  if (isLogin) {
    return <>{children}</>;
  }

  if (isBukinistka) {
    return <BukinistkaShell>{children}</BukinistkaShell>;
  }

  return (
    <TopbarProvider>
      <div className="flex h-screen">
        <Sidebar />
        <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
          <Topbar />
          <main className="min-h-0 flex-1 overflow-auto bg-gray-50/80 p-6 [scrollbar-gutter:stable]">
            {children}
          </main>
        </div>
      </div>
    </TopbarProvider>
  );
}
