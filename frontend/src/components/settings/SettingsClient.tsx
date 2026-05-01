'use client';

import { useEffect, useState } from 'react';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { fetchInvoiceSettings, saveInvoiceSettings } from '@/lib/api/settings';

type SettingsTabId = 'invoices';

export default function SettingsClient() {
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [activeTab, setActiveTab] = useState<SettingsTabId>('invoices');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [savedInvoiceSettings, setSavedInvoiceSettings] = useState({
    companyName: '',
    address: '',
    email: '',
    website: '',
    nip: '',
    currency: 'PLN',
  });
  const [invoiceSettings, setInvoiceSettings] = useState({
    companyName: '',
    address: '',
    email: '',
    website: '',
    nip: '',
    currency: 'PLN',
  });

  useEffect(() => {
    setTopbarPage({
      title: 'Налады',
      subtitle: 'Кіраванне параметрамі сістэмы',
    });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [setTopbarButtons, setTopbarPage]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchInvoiceSettings()
      .then((data) => {
        if (cancelled) return;
        setSavedInvoiceSettings(data);
        setInvoiceSettings(data);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : 'Памылка загрузкі налад');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const validateEmail = (value: string) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value.trim());
  const validateWebsite = (value: string) => {
    const trimmed = value.trim();
    if (!trimmed || /\s/.test(trimmed)) return false;
    try {
      const withScheme = trimmed.includes('://') ? trimmed : `https://${trimmed}`;
      const url = new URL(withScheme);
      return !!url.hostname && url.hostname.includes('.');
    } catch {
      return false;
    }
  };
  const validateNip = (value: string) => /^\d{10}$/.test(value.trim());

  const handleSave = async () => {
    setError(null);
    setSuccess(null);

    if (!invoiceSettings.companyName.trim() ||
        !invoiceSettings.address.trim() ||
        !invoiceSettings.email.trim() ||
        !invoiceSettings.website.trim() ||
        !invoiceSettings.nip.trim() ||
        !invoiceSettings.currency.trim()) {
      setError('Усе палі абавязковыя.');
      return;
    }
    if (!validateEmail(invoiceSettings.email)) {
      setError('Увядзіце карэктны e-mail.');
      return;
    }
    if (!validateWebsite(invoiceSettings.website)) {
      setError('Увядзіце карэктную спасылку.');
      return;
    }
    if (!validateNip(invoiceSettings.nip)) {
      setError('NIP павінен утрымліваць роўна 10 лічбаў.');
      return;
    }
    if (!/^[A-Za-z]{3}$/.test(invoiceSettings.currency.trim())) {
      setError('Валюта павінна ўтрымліваць 3 літары (напрыклад, PLN).');
      return;
    }

    setSaving(true);
    try {
      await saveInvoiceSettings({
        companyName: invoiceSettings.companyName.trim(),
        address: invoiceSettings.address.trim(),
        email: invoiceSettings.email.trim(),
        website: invoiceSettings.website.trim(),
        nip: invoiceSettings.nip.trim(),
        currency: invoiceSettings.currency.trim().toUpperCase(),
      });
      setSavedInvoiceSettings({
        companyName: invoiceSettings.companyName.trim(),
        address: invoiceSettings.address.trim(),
        email: invoiceSettings.email.trim(),
        website: invoiceSettings.website.trim(),
        nip: invoiceSettings.nip.trim(),
        currency: invoiceSettings.currency.trim().toUpperCase(),
      });
      setSuccess('Налады захаваны.');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка захавання налад');
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return <LoadingSpinner label="Загрузка налад..." />;
  }

  return (
    <div className="mx-auto w-full max-w-6xl space-y-4">
      <div className="rounded-xl border border-gray-200 bg-white p-2 shadow-sm">
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => setActiveTab('invoices')}
            className={`rounded-lg px-4 py-2 text-sm font-medium transition ${
              activeTab === 'invoices'
                ? 'bg-primary text-white shadow-sm'
                : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
            }`}
          >
            Фактуры
          </button>
        </div>
      </div>

      <div className="rounded-xl border border-gray-200 bg-white p-6 shadow-sm">
        {activeTab === 'invoices' && (
          <div className="space-y-4">
            <div>
              <h2 className="text-base font-semibold text-gray-900">Дадзеныя для выстаўлення фактур</h2>
            </div>
            {error && (
              <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
                {error}
              </div>
            )}
            {success && (
              <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-800">
                {success}
              </div>
            )}
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Назва юр. асобы</span>
                <input
                  type="text"
                  value={invoiceSettings.companyName}
                  onChange={(e) => {
                    const value = e.currentTarget.value;
                    setInvoiceSettings((prev) => ({ ...prev, companyName: value }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">NIP</span>
                <input
                  type="text"
                  value={invoiceSettings.nip}
                  onChange={(e) => {
                    const value = e.currentTarget.value;
                    setInvoiceSettings((prev) => ({ ...prev, nip: value }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Адрас</span>
                <input
                  type="text"
                  value={invoiceSettings.address}
                  onChange={(e) => {
                    const value = e.currentTarget.value;
                    setInvoiceSettings((prev) => ({ ...prev, address: value }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Валюта</span>
                <input
                  type="text"
                  value={invoiceSettings.currency}
                  onChange={(e) => {
                    const value = e.currentTarget.value;
                    setInvoiceSettings((prev) => ({ ...prev, currency: value }));
                  }}
                  placeholder="PLN"
                  maxLength={3}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm uppercase text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">e-mail</span>
                <input
                  type="email"
                  value={invoiceSettings.email}
                  onChange={(e) => {
                    const value = e.currentTarget.value;
                    setInvoiceSettings((prev) => ({ ...prev, email: value }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Спасылка</span>
                <input
                  type="text"
                  value={invoiceSettings.website}
                  onChange={(e) => {
                    const value = e.currentTarget.value;
                    setInvoiceSettings((prev) => ({ ...prev, website: value }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
            </div>
            <div className="flex justify-end">
              <button
                type="button"
                onClick={() => {
                  setInvoiceSettings(savedInvoiceSettings);
                  setError(null);
                  setSuccess(null);
                }}
                disabled={saving}
                className="mr-2 inline-flex min-w-24 items-center justify-center rounded-lg border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-60"
              >
                Скінуць
              </button>
              <button
                type="button"
                onClick={handleSave}
                disabled={saving}
                className="inline-flex min-w-28 items-center justify-center rounded-lg border border-primary bg-primary px-4 py-2 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-60"
              >
                {saving ? 'Захаванне…' : 'Захаваць'}
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
