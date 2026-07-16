'use client';

import { loginBukinistka, getShopifyLoginUrl } from '@/lib/api/auth';
import { organizations, type OrganizationId } from '@/lib/auth/organizations';
import { useSearchParams } from 'next/navigation';
import { useState } from 'react';
import {
  FiArrowLeft,
  FiBookOpen,
  FiChevronRight,
  FiShoppingBag,
} from 'react-icons/fi';

function OrganizationCard({
  id,
  name,
  subtitle,
  onSelect,
}: {
  id: OrganizationId;
  name: string;
  subtitle: string;
  onSelect: (id: OrganizationId) => void;
}) {
  const isKirma = id === 'kirma';

  return (
    <button
      type="button"
      onClick={() => onSelect(id)}
      className="group flex w-full items-center gap-4 rounded-xl border border-gray-200 bg-white p-4 text-left transition hover:border-primary/40 hover:bg-primary/5 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
    >
      <div
        className={`flex size-14 shrink-0 items-center justify-center rounded-lg ${
          isKirma ? 'bg-primary/10 text-primary' : 'bg-amber-50 text-amber-700'
        }`}
      >
        {isKirma ? (
          <FiShoppingBag className="size-7" aria-hidden />
        ) : (
          <FiBookOpen className="size-7" aria-hidden />
        )}
      </div>
      <div className="min-w-0 flex-1">
        <p className="text-base font-semibold text-gray-900">{name}</p>
        <p className="mt-0.5 text-sm text-gray-500">{subtitle}</p>
      </div>
      <FiChevronRight
        className="size-5 shrink-0 text-gray-400 transition group-hover:text-primary"
        aria-hidden
      />
    </button>
  );
}

function BukinistkaLoginForm({
  onBack,
  initialError,
}: {
  onBack: () => void;
  initialError?: string | null;
}) {
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(initialError ?? null);

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (submitting) return;

    setSubmitting(true);
    setError(null);
    try {
      const result = await loginBukinistka(login, password);
      window.location.href = result.redirectUrl;
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не ўдалося ўвайсці.');
      setSubmitting(false);
    }
  };

  return (
    <form onSubmit={handleSubmit} className="mt-6 space-y-4">
      <button
        type="button"
        onClick={onBack}
        className="inline-flex items-center gap-2 text-sm text-gray-600 transition hover:text-gray-900"
      >
        <FiArrowLeft aria-hidden />
        Назад да выбару арганізацыі
      </button>

      <div>
        <label
          htmlFor="odoo-login"
          className="mb-1 block text-sm font-medium text-gray-700"
        >
          Логін Odoo
        </label>
        <input
          id="odoo-login"
          type="text"
          autoComplete="username"
          value={login}
          onChange={(e) => setLogin(e.target.value)}
          className="h-11 w-full rounded-lg border border-gray-200 px-3 text-sm outline-none transition focus:border-primary focus:ring-2 focus:ring-primary/20"
          required
        />
      </div>

      <div>
        <label
          htmlFor="odoo-password"
          className="mb-1 block text-sm font-medium text-gray-700"
        >
          Пароль Odoo
        </label>
        <input
          id="odoo-password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          className="h-11 w-full rounded-lg border border-gray-200 px-3 text-sm outline-none transition focus:border-primary focus:ring-2 focus:ring-primary/20"
          required
        />
      </div>

      {error && (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800">
          {error}
        </p>
      )}

      <button
        type="submit"
        disabled={submitting}
        className="inline-flex h-11 w-full items-center justify-center rounded-lg bg-primary px-4 text-sm font-medium text-white shadow-sm transition hover:bg-primary-hover disabled:opacity-60"
      >
        {submitting ? 'Уваход…' : 'Увайсці'}
      </button>
    </form>
  );
}

export default function OrganizationLogin() {
  const searchParams = useSearchParams();
  const loggedOut = searchParams?.get('loggedOut') === '1';
  const queryError = searchParams?.get('error') ?? null;
  const initialOrg =
    searchParams?.get('org') === 'bukinistka' ? 'bukinistka' : null;
  const [selectedOrg, setSelectedOrg] = useState<OrganizationId | null>(
    initialOrg
  );

  const handleSelect = (id: OrganizationId) => {
    const org = organizations.find((item) => item.id === id);
    if (!org) return;

    if (org.authType === 'shopify') {
      window.location.href = getShopifyLoginUrl();
      return;
    }

    setSelectedOrg('bukinistka');
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-gray-50/95 p-6">
      <div className="w-full max-w-lg rounded-2xl border border-gray-200 bg-white p-8 shadow-sm">
        <h1 className="text-center text-xl font-semibold text-gray-900">
          Уваход у адмін-панэль
        </h1>
        <p className="mt-2 text-center text-sm text-gray-600">
          {selectedOrg === 'bukinistka'
            ? 'Уваход Bukinistka праз Odoo'
            : 'Абярыце арганізацыю'}
        </p>

        {loggedOut && (
          <p className="mt-4 rounded-lg bg-green-50 px-3 py-2 text-center text-sm text-green-800">
            Вы выйшлі з уліковага запісу.
          </p>
        )}

        {selectedOrg === 'bukinistka' ? (
          <BukinistkaLoginForm
            onBack={() => setSelectedOrg(null)}
            initialError={queryError}
          />
        ) : (
          <div className="mt-6 space-y-3">
            {queryError && (
              <p className="rounded-lg bg-red-50 px-3 py-2 text-center text-sm text-red-800">
                {queryError}
              </p>
            )}
            {organizations.map((org) => (
              <OrganizationCard
                key={org.id}
                id={org.id}
                name={org.name}
                subtitle={org.subtitle}
                onSelect={handleSelect}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
