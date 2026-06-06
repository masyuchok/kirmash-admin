'use client';

import { useEffect, useState } from 'react';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { fetchFinancePersons, type FinancePerson } from '@/lib/api/finances';
import {
  createExpenseInvoiceType,
  deleteExpenseInvoiceType,
  fetchExpenseInvoiceTypes,
  fetchInvoiceSettings,
  fetchVatAutoFinanceSettings,
  saveInvoiceSettings,
  saveVatAutoFinanceSettings,
  updateExpenseInvoiceType,
  type ExpenseInvoiceType,
  type VatAutoFinanceSettings,
} from '@/lib/api/settings';
import { FiEdit2, FiPlus, FiTrash2, FiX } from 'react-icons/fi';

type SettingsTabId = 'invoices';
type InvoiceSubTabId = 'data' | 'expense';

const invoiceSubTabs: { id: InvoiceSubTabId; label: string }[] = [
  { id: 'data', label: 'Дадзеныя' },
  { id: 'expense', label: 'Расход' },
];

export default function SettingsClient() {
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [activeTab, setActiveTab] = useState<SettingsTabId>('invoices');
  const [invoiceSubTab, setInvoiceSubTab] = useState<InvoiceSubTabId>('data');
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
  const [expenseInvoiceTypes, setExpenseInvoiceTypes] = useState<ExpenseInvoiceType[]>([]);
  const [typeDraft, setTypeDraft] = useState('');
  const [editingTypeId, setEditingTypeId] = useState<number | null>(null);
  const [editingTypeName, setEditingTypeName] = useState('');
  const [typeSaving, setTypeSaving] = useState(false);
  const [financePersons, setFinancePersons] = useState<FinancePerson[]>([]);
  const [vatAutoFinance, setVatAutoFinance] = useState<VatAutoFinanceSettings>({
    isEnabled: false,
    financePersonId: null,
  });
  const [savedVatAutoFinance, setSavedVatAutoFinance] = useState<VatAutoFinanceSettings>({
    isEnabled: false,
    financePersonId: null,
  });
  const [vatAutoSaving, setVatAutoSaving] = useState(false);

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
    Promise.all([
      fetchInvoiceSettings(),
      fetchExpenseInvoiceTypes(),
      fetchVatAutoFinanceSettings(),
      fetchFinancePersons(),
    ])
      .then(([data, types, vatAuto, persons]) => {
        if (cancelled) return;
        setSavedInvoiceSettings(data);
        setInvoiceSettings(data);
        setExpenseInvoiceTypes(types);
        setVatAutoFinance(vatAuto);
        setSavedVatAutoFinance(vatAuto);
        setFinancePersons(persons);
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

  const reloadExpenseTypes = async () => {
    const rows = await fetchExpenseInvoiceTypes();
    setExpenseInvoiceTypes(rows);
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
            <div className="flex flex-wrap gap-2 border-b border-gray-100 pb-4">
              {invoiceSubTabs.map((tab) => (
                <button
                  key={tab.id}
                  type="button"
                  onClick={() => {
                    setInvoiceSubTab(tab.id);
                    setError(null);
                    setSuccess(null);
                  }}
                  className={`rounded-lg px-3 py-1.5 text-sm font-medium transition ${
                    invoiceSubTab === tab.id
                      ? 'bg-primary/10 text-primary'
                      : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
                  }`}
                >
                  {tab.label}
                </button>
              ))}
            </div>

            {invoiceSubTab === 'data' && (
              <>
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
              </>
            )}

            {invoiceSubTab === 'expense' && (
              <>
            <div>
              <h2 className="text-base font-semibold text-gray-900">Расход</h2>
              <p className="mt-1 text-sm text-gray-600">Тыпы расходаў для ўліку ў справаздачах</p>
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
              <div className="space-y-2">
                {expenseInvoiceTypes.map((item) => (
                  <div
                    key={item.id}
                    className="flex items-center justify-between gap-2 rounded-lg border border-gray-200 bg-gray-50 px-3 py-2"
                  >
                    {editingTypeId === item.id ? (
                      <input
                        value={editingTypeName}
                        onChange={(e) => setEditingTypeName(e.currentTarget.value)}
                        className="w-full rounded-md border border-gray-200 bg-white px-2 py-1.5 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                      />
                    ) : (
                      <span className="text-sm text-gray-800">{item.name}</span>
                    )}
                    <div className="inline-flex items-center gap-1">
                      {editingTypeId === item.id ? (
                        <>
                          <button
                            type="button"
                            disabled={typeSaving}
                            onClick={async () => {
                              if (!editingTypeName.trim()) return;
                              setTypeSaving(true);
                              try {
                                await updateExpenseInvoiceType(item.id, editingTypeName.trim());
                                await reloadExpenseTypes();
                                setEditingTypeId(null);
                                setEditingTypeName('');
                              } catch (err: unknown) {
                                setError(err instanceof Error ? err.message : 'Памылка абнаўлення тыпу');
                              } finally {
                                setTypeSaving(false);
                              }
                            }}
                            className="rounded-md border border-primary bg-primary px-2 py-1 text-xs font-medium text-white disabled:opacity-60"
                          >
                            Захаваць
                          </button>
                          <button
                            type="button"
                            onClick={() => {
                              setEditingTypeId(null);
                              setEditingTypeName('');
                            }}
                            className="inline-flex size-7 items-center justify-center rounded-md border border-gray-200 bg-white text-gray-700 hover:bg-gray-100"
                          >
                            <FiX className="size-3.5" />
                          </button>
                        </>
                      ) : (
                        <>
                          {!item.isSystem && (
                            <>
                              <button
                                type="button"
                                onClick={() => {
                                  setEditingTypeId(item.id);
                                  setEditingTypeName(item.name);
                                }}
                                className="inline-flex size-7 items-center justify-center rounded-md border border-gray-200 bg-white text-gray-700 hover:bg-gray-100"
                              >
                                <FiEdit2 className="size-3.5" />
                              </button>
                              <button
                                type="button"
                                disabled={typeSaving}
                                onClick={async () => {
                                  setTypeSaving(true);
                                  try {
                                    await deleteExpenseInvoiceType(item.id);
                                    await reloadExpenseTypes();
                                  } catch (err: unknown) {
                                    setError(err instanceof Error ? err.message : 'Памылка выдалення тыпу');
                                  } finally {
                                    setTypeSaving(false);
                                  }
                                }}
                                className="inline-flex size-7 items-center justify-center rounded-md border border-red-200 bg-white text-red-600 hover:bg-red-50 disabled:opacity-60"
                              >
                                <FiTrash2 className="size-3.5" />
                              </button>
                            </>
                          )}
                        </>
                      )}
                    </div>
                  </div>
                ))}
              </div>
              <div className="mt-3 flex items-center gap-2">
                <input
                  type="text"
                  value={typeDraft}
                  onChange={(e) => setTypeDraft(e.currentTarget.value)}
                  placeholder="Новы тып"
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
                <button
                  type="button"
                  disabled={typeSaving || !typeDraft.trim()}
                  onClick={async () => {
                    if (!typeDraft.trim()) return;
                    setTypeSaving(true);
                    try {
                      await createExpenseInvoiceType(typeDraft.trim());
                      setTypeDraft('');
                      await reloadExpenseTypes();
                    } catch (err: unknown) {
                      setError(err instanceof Error ? err.message : 'Памылка дадання тыпу');
                    } finally {
                      setTypeSaving(false);
                    }
                  }}
                  className="inline-flex size-9 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:opacity-60"
                >
                  <FiPlus className="size-4" />
                </button>
              </div>

              <div className="mt-8 border-t border-gray-200 pt-6">
                <h3 className="text-base font-semibold text-gray-900">Аўтарасход для VAT</h3>
                <p className="mt-1 text-sm text-gray-600">
                  Пры генерацыі або змене справаздачы аўтаматычна дадаецца аплата выбранай асобе з сумай «Усяго
                  VAT».
                </p>
                <div className="mt-4 space-y-4">
                  <label className="flex cursor-pointer items-center gap-3">
                    <input
                      type="checkbox"
                      checked={vatAutoFinance.isEnabled}
                      onChange={(e) => {
                        const enabled = e.currentTarget.checked;
                        setVatAutoFinance((prev) => ({
                          ...prev,
                          isEnabled: enabled,
                          financePersonId: enabled ? prev.financePersonId : null,
                        }));
                      }}
                      className="size-4 rounded border-gray-300 text-primary focus:ring-primary/30"
                    />
                    <span className="text-sm font-medium text-gray-800">Уключыць аўтарасход</span>
                  </label>
                  {vatAutoFinance.isEnabled && (
                    <label className="block max-w-md space-y-1.5">
                      <span className="text-sm font-medium text-gray-700">Асоба (Фінансы)</span>
                      <select
                        value={vatAutoFinance.financePersonId ?? ''}
                        onChange={(e) => {
                          const value = Number(e.currentTarget.value);
                          setVatAutoFinance((prev) => ({
                            ...prev,
                            financePersonId: value > 0 ? value : null,
                          }));
                        }}
                        className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                      >
                        <option value="">— выберыце асобу —</option>
                        {financePersons.map((person) => (
                          <option key={person.id} value={person.id}>
                            {person.name}
                          </option>
                        ))}
                      </select>
                      {financePersons.length === 0 && (
                        <p className="text-xs text-amber-700">
                          Спачатку дадайце асобу на старонцы «Фінансы».
                        </p>
                      )}
                    </label>
                  )}
                  <div className="flex justify-end gap-2">
                    <button
                      type="button"
                      onClick={() => setVatAutoFinance(savedVatAutoFinance)}
                      disabled={vatAutoSaving}
                      className="rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 transition hover:bg-gray-50 disabled:opacity-60"
                    >
                      Скінуць
                    </button>
                    <button
                      type="button"
                      disabled={
                        vatAutoSaving ||
                        (vatAutoFinance.isEnabled && !vatAutoFinance.financePersonId)
                      }
                      onClick={async () => {
                        setVatAutoSaving(true);
                        setError(null);
                        setSuccess(null);
                        try {
                          await saveVatAutoFinanceSettings(vatAutoFinance);
                          setSavedVatAutoFinance(vatAutoFinance);
                          setSuccess('Налады аўтарасходу VAT захаваны.');
                        } catch (err: unknown) {
                          setError(
                            err instanceof Error ? err.message : 'Памылка захавання аўтарасходу VAT'
                          );
                        } finally {
                          setVatAutoSaving(false);
                        }
                      }}
                      className="rounded-lg border border-primary bg-primary px-4 py-2 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-60"
                    >
                      {vatAutoSaving ? 'Захаванне…' : 'Захаваць'}
                    </button>
                  </div>
                </div>
              </div>
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
