'use client';

import { fetchBukinistkaMe } from '@/lib/api/auth';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { useEffect, useState } from 'react';

export default function BukinistkaHomeClient() {
  const [loading, setLoading] = useState(true);
  const [userName, setUserName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchBukinistkaMe()
      .then((me) => setUserName(me.name || me.login))
      .catch((err: Error) => setError(err.message))
      .finally(() => setLoading(false));
  }, []);

  if (loading) {
    return (
      <div className="flex justify-center py-16">
        <LoadingSpinner />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-3xl rounded-2xl border border-gray-200 bg-white p-8 shadow-sm">
      <h1 className="text-2xl font-semibold text-gray-900">
        Сардэчна запрашаем у Bukinistka
      </h1>
      {userName && (
        <p className="mt-2 text-sm text-gray-600">
          Увайшлі як{' '}
          <span className="font-medium text-gray-900">{userName}</span>
        </p>
      )}
      {error && (
        <p className="mt-3 rounded-lg bg-red-50 px-4 py-3 text-sm text-red-800">
          {error}
        </p>
      )}
      <p className="mt-3 text-sm leading-6 text-gray-600">
        Гэта асобная панэль кіравання для Bukinistka. У раздзеле «Прадукты»
        можна глядзець каталог і колькасць у наяўнасці з Odoo.
      </p>
      <p className="mt-4 rounded-lg bg-amber-50 px-4 py-3 text-sm text-amber-900">
        Раздзел у распрацоўцы.
      </p>
    </div>
  );
}
