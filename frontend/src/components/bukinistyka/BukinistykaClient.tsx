'use client';

import { useEffect, useState } from 'react';
import { FiEdit2, FiExternalLink, FiX } from 'react-icons/fi';
import ProposeToBukinistkaModal, {
  type ProposeToBukinistkaDraft,
} from '@/components/products/ProposeToBukinistkaModal';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import {
  cancelKirmaBukinistkaOffer,
  fetchKirmaSentBukinistkaOffers,
  updateKirmaBukinistkaOffer,
  type KirmaBukinistkaOffer,
} from '@/lib/api/bukinistka-offers';
import {
  fetchBukinistkaPosSales,
  syncBukinistkaPosSales,
  type BukinistkaPosSale,
} from '@/lib/api/bukinistka-sales';

type MainTabId = 'offers' | 'sales';
type OffersSubTabId = 'sent' | 'received';

const offersSubTabs: { id: OffersSubTabId; label: string }[] = [
  { id: 'sent', label: 'Высланыя' },
  { id: 'received', label: 'Атрыманыя' },
];

function formatPrice(value: number): string {
  if (!Number.isFinite(value)) return '—';
  return value.toLocaleString('be-BY', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
}

function formatDate(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('be-BY', {
    timeZone: 'Europe/Warsaw',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function isPendingOffer(row: KirmaBukinistkaOffer): boolean {
  const status = (row.status || 'Pending').trim().toLowerCase();
  return status === 'pending' || status === '';
}

function isRejectedOffer(row: KirmaBukinistkaOffer): boolean {
  return (row.status || '').trim().toLowerCase() === 'rejected';
}

function isAcceptedOffer(row: KirmaBukinistkaOffer): boolean {
  return (row.status || '').trim().toLowerCase() === 'accepted';
}

function statusLabel(row: KirmaBukinistkaOffer): string | null {
  const status = (row.status || 'Pending').trim().toLowerCase();
  if (status === 'accepted') return 'Прынята';
  if (status === 'rejected') return 'Адхілена';
  return null;
}

function OffersTable({
  rows,
  emptyText,
  busyId,
  onEdit,
  onCancel,
  onDeleteRejected,
}: {
  rows: KirmaBukinistkaOffer[];
  emptyText: string;
  busyId: number | null;
  onEdit?: (row: KirmaBukinistkaOffer) => void;
  onCancel?: (row: KirmaBukinistkaOffer) => void;
  onDeleteRejected?: (row: KirmaBukinistkaOffer) => void;
}) {
  if (rows.length === 0) {
    return (
      <p className="rounded-lg border border-dashed border-gray-200 bg-gray-50 px-4 py-10 text-center text-sm text-gray-500">
        {emptyText}
      </p>
    );
  }

  const showActions = Boolean(onEdit || onCancel || onDeleteRejected);

  return (
    <div className="overflow-hidden rounded-xl border border-gray-200">
      <table className="w-full table-fixed divide-y divide-gray-100 text-left text-sm">
        <thead className="bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
          <tr>
            <th className={`${showActions ? 'w-[38%]' : 'w-[46%]'} px-4 py-3`}>
              Прадукт
            </th>
            <th className="w-[10%] px-4 py-3 text-right">Колькасць</th>
            <th className="w-[12%] px-4 py-3 text-right">Кошт брута</th>
            <th className="w-[12%] px-4 py-3">Пастаўшчык</th>
            <th className="w-[12%] px-4 py-3">Дата</th>
            {showActions ? (
              <th className="w-[16%] px-4 py-3 text-right">Дзеянні</th>
            ) : null}
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100 bg-white">
          {rows.map((row) => {
            const href = row.storefrontUrl.trim() || row.productAdminUrl.trim();
            const author = row.productAuthor.trim();
            const pending = isPendingOffer(row);
            const rejected = isRejectedOffer(row);
            const accepted = isAcceptedOffer(row);
            const status = statusLabel(row);
            const busy = busyId === row.id;
            const qtyBefore = row.odooQuantityBeforeAccept ?? 0;
            return (
              <tr key={row.id} className="align-top">
                <td className="px-4 py-3">
                  <button
                    type="button"
                    className={`flex w-full items-start gap-3 text-left ${
                      href
                        ? 'cursor-pointer hover:opacity-90'
                        : 'cursor-default'
                    }`}
                    onClick={() => {
                      if (!href) return;
                      window.open(href, '_blank', 'noopener,noreferrer');
                    }}
                  >
                    {row.mainImageUrl ? (
                      // eslint-disable-next-line @next/next/no-img-element
                      <img
                        src={row.mainImageUrl}
                        alt=""
                        className="size-10 shrink-0 rounded-md object-cover ring-1 ring-gray-200"
                      />
                    ) : (
                      <div className="size-10 shrink-0 rounded-md bg-gray-100 ring-1 ring-gray-200" />
                    )}
                    <div className="min-w-0 flex-1">
                      <div className="flex items-start gap-1.5 font-medium text-gray-900">
                        <span className="break-words [overflow-wrap:anywhere]">
                          {row.productName}
                        </span>
                        {href ? (
                          <FiExternalLink
                            className="mt-0.5 size-3.5 shrink-0 text-gray-400"
                            aria-hidden
                          />
                        ) : null}
                      </div>
                      {author ? (
                        <p className="mt-0.5 break-words text-xs text-gray-500 [overflow-wrap:anywhere]">
                          {author}
                        </p>
                      ) : null}
                      {status ? (
                        <span
                          className={`mt-1 inline-flex rounded-full px-2 py-0.5 text-[11px] font-medium ring-1 ring-inset ${
                            rejected
                              ? 'bg-red-50 text-red-700 ring-red-600/20'
                              : accepted
                                ? 'bg-emerald-50 text-emerald-800 ring-emerald-600/20'
                                : 'bg-gray-100 text-gray-700 ring-gray-500/20'
                          }`}
                        >
                          {status}
                        </span>
                      ) : null}
                    </div>
                  </button>
                </td>
                <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums text-gray-700">
                  {row.quantity}
                </td>
                <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums text-gray-700">
                  {formatPrice(row.grossUnitCost)}
                </td>
                <td className="px-4 py-3 text-gray-700">
                  <span className="break-words [overflow-wrap:anywhere]">
                    {row.supplierName?.trim() || '—'}
                  </span>
                </td>
                <td className="whitespace-nowrap px-4 py-3 text-gray-500">
                  {formatDate(row.createdAtUtc)}
                </td>
                {showActions ? (
                  <td className="px-4 py-3 text-right">
                    {pending ? (
                      <div className="inline-flex items-center justify-end gap-1.5">
                        <button
                          type="button"
                          disabled={busy}
                          onClick={() => onEdit?.(row)}
                          className="inline-flex size-8 items-center justify-center rounded-lg border border-gray-200 bg-white text-gray-700 transition hover:border-primary/30 hover:bg-primary/5 hover:text-primary disabled:opacity-50"
                          aria-label="Рэдагаваць прапанову"
                          title="Рэдагаваць"
                        >
                          <FiEdit2 className="size-3.5" aria-hidden />
                        </button>
                        <button
                          type="button"
                          disabled={busy}
                          onClick={() => onCancel?.(row)}
                          className="inline-flex size-8 items-center justify-center rounded-lg border border-gray-200 bg-white text-gray-700 transition hover:border-red-300 hover:bg-red-50 hover:text-red-700 disabled:opacity-50"
                          aria-label="Адмяніць прапанову"
                          title="Адмяніць"
                        >
                          {busy ? (
                            <span className="size-3.5 animate-spin rounded-full border-2 border-red-200 border-t-red-600" />
                          ) : (
                            <FiX className="size-3.5" aria-hidden />
                          )}
                        </button>
                      </div>
                    ) : rejected ? (
                      <label
                        className="inline-flex cursor-pointer items-center gap-2 text-xs text-gray-600"
                        title="Выдаліць адхіленую прапанову"
                      >
                        <input
                          type="checkbox"
                          className="size-4 rounded border-gray-300 accent-red-600"
                          checked={false}
                          disabled={busy}
                          onChange={(e) => {
                            if (!e.target.checked) return;
                            onDeleteRejected?.(row);
                          }}
                        />
                        <span>Выдаліць</span>
                      </label>
                    ) : accepted && qtyBefore > 0 ? (
                      <button
                        type="button"
                        disabled
                        title="Хутка"
                        className="inline-flex cursor-not-allowed items-center rounded-lg border border-emerald-200 bg-emerald-50/60 px-2.5 py-1.5 text-xs font-medium text-emerald-800/80"
                      >
                        Дадаць {qtyBefore} ад Букіністкі
                      </button>
                    ) : (
                      <span className="text-xs text-gray-400">—</span>
                    )}
                  </td>
                ) : null}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

export default function BukinistykaClient() {
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [activeTab, setActiveTab] = useState<MainTabId>('offers');
  const [offersSubTab, setOffersSubTab] = useState<OffersSubTabId>('sent');
  const [sentOffers, setSentOffers] = useState<KirmaBukinistkaOffer[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<number | null>(null);
  const [editOpen, setEditOpen] = useState(false);
  const [editRow, setEditRow] = useState<KirmaBukinistkaOffer | null>(null);
  const [editDraft, setEditDraft] = useState<ProposeToBukinistkaDraft | null>(
    null
  );
  const [editSubmitting, setEditSubmitting] = useState(false);
  const [editError, setEditError] = useState<string | null>(null);
  const [sales, setSales] = useState<BukinistkaPosSale[]>([]);
  const [salesLoading, setSalesLoading] = useState(false);
  const [salesError, setSalesError] = useState<string | null>(null);
  const [salesSyncing, setSalesSyncing] = useState(false);

  useEffect(() => {
    setTopbarPage({
      title: 'Букіністка',
      subtitle: 'Супрацоўніцтва з Букіністкай',
    });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [setTopbarButtons, setTopbarPage]);

  const reloadSent = async () => {
    const rows = await fetchKirmaSentBukinistkaOffers();
    setSentOffers(rows);
  };

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchKirmaSentBukinistkaOffers()
      .then((rows) => {
        if (!cancelled) setSentOffers(rows);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(
            err instanceof Error ? err.message : 'Памылка загрузкі прапаноў'
          );
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const reloadSales = async () => {
    const rows = await fetchBukinistkaPosSales();
    setSales(rows);
  };

  useEffect(() => {
    if (activeTab !== 'sales') return;
    let cancelled = false;
    setSalesLoading(true);
    setSalesError(null);
    fetchBukinistkaPosSales()
      .then((rows) => {
        if (!cancelled) setSales(rows);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setSalesError(
            err instanceof Error ? err.message : 'Памылка загрузкі продажаў'
          );
        }
      })
      .finally(() => {
        if (!cancelled) setSalesLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [activeTab]);

  const openEdit = (row: KirmaBukinistkaOffer) => {
    setEditRow(row);
    setEditDraft({
      productLabel: row.productAuthor.trim()
        ? `${row.productName} — ${row.productAuthor}`
        : row.productName,
      quantity: row.quantity,
      grossUnitCost: row.grossUnitCost,
    });
    setEditError(null);
    setEditOpen(true);
  };

  const closeEdit = () => {
    if (editSubmitting) return;
    setEditOpen(false);
    setEditRow(null);
    setEditDraft(null);
    setEditError(null);
  };

  const submitEdit = async (quantity: number, grossUnitCost: number) => {
    if (!editRow) return;
    setEditSubmitting(true);
    setEditError(null);
    try {
      const updated = await updateKirmaBukinistkaOffer(editRow.id, {
        quantity,
        grossUnitCost,
      });
      setSentOffers((prev) =>
        prev.map((row) => (row.id === updated.id ? updated : row))
      );
      setEditOpen(false);
      setEditRow(null);
      setEditDraft(null);
    } catch (err: unknown) {
      setEditError(
        err instanceof Error ? err.message : 'Не ўдалося абнавіць прапанову.'
      );
    } finally {
      setEditSubmitting(false);
    }
  };

  const handleCancel = async (row: KirmaBukinistkaOffer) => {
    const ok = window.confirm(
      `Адмяніць прапанову «${row.productName}»? Яна будзе выдаленая са спіса.`
    );
    if (!ok) return;
    setBusyId(row.id);
    setError(null);
    try {
      await cancelKirmaBukinistkaOffer(row.id);
      setSentOffers((prev) => prev.filter((item) => item.id !== row.id));
    } catch (err: unknown) {
      setError(
        err instanceof Error ? err.message : 'Не ўдалося адмяніць прапанову.'
      );
      try {
        await reloadSent();
      } catch {
        // ignore reload error
      }
    } finally {
      setBusyId(null);
    }
  };

  const handleDeleteRejected = async (row: KirmaBukinistkaOffer) => {
    const ok = window.confirm(
      `Выдаліць адхіленую прапанову «${row.productName}» са спіса?`
    );
    if (!ok) return;
    setBusyId(row.id);
    setError(null);
    try {
      await cancelKirmaBukinistkaOffer(row.id);
      setSentOffers((prev) => prev.filter((item) => item.id !== row.id));
    } catch (err: unknown) {
      setError(
        err instanceof Error ? err.message : 'Не ўдалося выдаліць прапанову.'
      );
      try {
        await reloadSent();
      } catch {
        // ignore reload error
      }
    } finally {
      setBusyId(null);
    }
  };

  const handleSalesSync = async () => {
    setSalesSyncing(true);
    setSalesError(null);
    try {
      const result = await syncBukinistkaPosSales();
      await reloadSales();
      if (result.skipped) {
        setSalesError(
          result.skipReason ||
            'Сінхранізацыя прапушчаная: праверце канфіг Shopify/Odoo.'
        );
      }
    } catch (err: unknown) {
      setSalesError(
        err instanceof Error
          ? err.message
          : 'Не ўдалося сінхранізаваць продажы.'
      );
    } finally {
      setSalesSyncing(false);
    }
  };

  if (loading) {
    return <LoadingSpinner label="Загрузка…" />;
  }

  return (
    <div className="mx-auto w-full max-w-6xl space-y-4">
      <div className="rounded-xl border border-gray-200 bg-white p-2 shadow-sm">
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => setActiveTab('offers')}
            className={`rounded-lg px-4 py-2 text-sm font-medium transition ${
              activeTab === 'offers'
                ? 'bg-primary text-white shadow-sm'
                : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
            }`}
          >
            Прапановы
          </button>
          <button
            type="button"
            onClick={() => {
              setActiveTab('sales');
              setError(null);
            }}
            className={`rounded-lg px-4 py-2 text-sm font-medium transition ${
              activeTab === 'sales'
                ? 'bg-primary text-white shadow-sm'
                : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
            }`}
          >
            Продажы
          </button>
        </div>
      </div>

      <div className="rounded-xl border border-gray-200 bg-white p-6 shadow-sm">
        {activeTab === 'offers' && (
          <div className="space-y-4">
            <div className="flex flex-wrap gap-2 border-b border-gray-100 pb-4">
              {offersSubTabs.map((tab) => (
                <button
                  key={tab.id}
                  type="button"
                  onClick={() => {
                    setOffersSubTab(tab.id);
                    setError(null);
                  }}
                  className={`rounded-lg px-3 py-1.5 text-sm font-medium transition ${
                    offersSubTab === tab.id
                      ? 'bg-primary/10 text-primary'
                      : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
                  }`}
                >
                  {tab.label}
                </button>
              ))}
            </div>

            {error ? (
              <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
                {error}
              </div>
            ) : null}

            {offersSubTab === 'sent' ? (
              <div className="space-y-3">
                <h2 className="text-base font-semibold text-gray-900">
                  Высланыя
                </h2>
                <p className="text-sm text-gray-500">
                  Прапановы, дасланыя ў Букіністыку з раздзела «Прадукты».
                  Непрынятыя можна рэдагаваць або адмяніць; адхіленыя — выдаліць
                  галочкай.
                </p>
                <OffersTable
                  rows={sentOffers}
                  emptyText="Пакуль няма высланых прапаноў."
                  busyId={busyId}
                  onEdit={openEdit}
                  onCancel={(row) => {
                    void handleCancel(row);
                  }}
                  onDeleteRejected={(row) => {
                    void handleDeleteRejected(row);
                  }}
                />
              </div>
            ) : (
              <div className="space-y-3">
                <h2 className="text-base font-semibold text-gray-900">
                  Атрыманыя
                </h2>
                <p className="text-sm text-gray-500">
                  Прапановы, атрыманыя ад Букіністкі.
                </p>
                <OffersTable
                  rows={[]}
                  emptyText="Пакуль няма атрыманых прапаноў."
                  busyId={null}
                />
              </div>
            )}
          </div>
        )}

        {activeTab === 'sales' && (
          <div className="space-y-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h2 className="text-base font-semibold text-gray-900">
                  Продажы ў Букіністцы
                </h2>
                <p className="mt-1 text-sm text-gray-500">
                  POS-продажы прынятых кніг. Склад у Shopify змяншаецца
                  аўтаматычна (кожныя ~10 хвілін) або па кнопцы «Абнавіць».
                </p>
              </div>
              <button
                type="button"
                disabled={salesSyncing || salesLoading}
                onClick={() => {
                  void handleSalesSync();
                }}
                className="inline-flex items-center gap-2 rounded-lg bg-primary px-3 py-2 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-50"
              >
                {salesSyncing ? (
                  <span className="size-3.5 animate-spin rounded-full border-2 border-white/30 border-t-white" />
                ) : null}
                Абнавіць
              </button>
            </div>

            {salesError ? (
              <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
                {salesError}
              </div>
            ) : null}

            {salesLoading ? (
              <LoadingSpinner label="Загрузка продажаў…" />
            ) : sales.length === 0 ? (
              <p className="rounded-lg border border-dashed border-gray-200 bg-gray-50 px-4 py-10 text-center text-sm text-gray-500">
                Пакуль няма сінхранізаваных продажаў.
              </p>
            ) : (
              <div className="overflow-hidden rounded-xl border border-gray-200">
                <table className="w-full table-fixed divide-y divide-gray-100 text-left text-sm">
                  <thead className="bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                    <tr>
                      <th className="w-[42%] px-4 py-3">Прадукт</th>
                      <th className="w-[14%] px-4 py-3 text-right">
                        Колькасць
                      </th>
                      <th className="w-[22%] px-4 py-3">Заказ Odoo</th>
                      <th className="w-[22%] px-4 py-3">Дата продажу</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-100 bg-white">
                    {sales.map((row) => (
                      <tr key={row.id} className="align-top">
                        <td className="px-4 py-3 font-medium text-gray-900">
                          <span className="break-words [overflow-wrap:anywhere]">
                            {row.productName}
                          </span>
                        </td>
                        <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums text-gray-700">
                          {row.quantity}
                        </td>
                        <td className="px-4 py-3 text-gray-600">
                          {row.odooPosOrderName?.trim() ||
                            `#${row.odooPosOrderId}`}
                        </td>
                        <td className="whitespace-nowrap px-4 py-3 text-gray-500">
                          {formatDate(row.soldAtUtc)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}
      </div>

      <ProposeToBukinistkaModal
        open={editOpen}
        draft={editDraft}
        submitting={editSubmitting}
        error={editError}
        title="Рэдагаваць прапанову"
        submitLabel="Захаваць"
        onClose={closeEdit}
        onSubmit={(quantity, grossUnitCost) => {
          void submitEdit(quantity, grossUnitCost);
        }}
      />
    </div>
  );
}
