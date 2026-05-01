'use client';

import { useEffect } from 'react';
import { FiClock } from 'react-icons/fi';
import { useTopbar } from '@/components/topbar/TopbarContext';

export default function SalesClient() {
  const { setTopbarButtons, setTopbarPage } = useTopbar();

  useEffect(() => {
    setTopbarPage({
      title: 'Продажы',
      subtitle: 'Раздзел у распрацоўцы',
    });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [setTopbarButtons, setTopbarPage]);

  return (
    <div className="mx-auto flex w-full max-w-3xl items-center justify-center py-20">
      <div className="w-full rounded-2xl border border-gray-200 bg-white p-8 text-center shadow-sm">
        <div className="mx-auto inline-flex size-12 items-center justify-center rounded-full bg-primary/10 text-primary">
          <FiClock className="size-6" aria-hidden />
        </div>
        <h2 className="mt-4 text-xl font-semibold text-gray-900">Раздзел яшчэ ў распрацоўцы</h2>
        <p className="mt-2 text-sm text-gray-600">
          Тут хутка з&apos;явіцца старонка продажаў. Пакуль функцыянальнасць недаступная.
        </p>
      </div>
    </div>
  );
}
