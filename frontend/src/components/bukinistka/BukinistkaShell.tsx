'use client';

import { logoutBukinistka } from '@/lib/api/auth';
import { FiBookOpen, FiLogOut } from 'react-icons/fi';

export default function BukinistkaShell({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <div className="flex h-screen flex-col bg-gray-50/80">
      <header className="shrink-0 border-b border-gray-200 bg-white">
        <div className="mx-auto flex w-full max-w-6xl items-center justify-between gap-4 px-6 py-4">
          <div className="flex items-center gap-3">
            <div className="flex size-10 items-center justify-center rounded-lg bg-amber-50 text-amber-700">
              <FiBookOpen className="size-5" aria-hidden />
            </div>
            <div>
              <p className="text-base font-semibold text-gray-900">
                Bukinistka
              </p>
              <p className="text-sm text-gray-500">Панэль кіравання</p>
            </div>
          </div>
          <button
            type="button"
            onClick={() => {
              void logoutBukinistka();
            }}
            className="inline-flex h-10 items-center gap-2 rounded-lg border border-gray-200 bg-white px-4 text-sm font-medium text-gray-700 shadow-sm transition hover:bg-gray-50"
          >
            <FiLogOut className="size-4" aria-hidden />
            Выйсці
          </button>
        </div>
      </header>
      <main className="min-h-0 flex-1 overflow-auto p-6 [scrollbar-gutter:stable]">
        {children}
      </main>
    </div>
  );
}
