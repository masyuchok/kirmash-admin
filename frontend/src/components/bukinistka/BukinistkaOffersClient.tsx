'use client';

import AcceptBukinistkaOfferModal from '@/components/bukinistka/AcceptBukinistkaOfferModal';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import {
  fetchKirmaBukinistkaOffers,
  rejectKirmaBukinistkaOffer,
  saveBukinistkaOfferReceipt,
  type KirmaBukinistkaOffer,
} from '@/lib/api/bukinistka-offers';
import { useEffect, useMemo, useState } from 'react';
import { FiExternalLink, FiSearch, FiX } from 'react-icons/fi';

type ReceiptDraft = {
  odooProductId: number;
  odooProductName: string;
  listPrice?: number | null;
  applyKirmaCostPrice?: boolean | null;
};

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

function offerOpenUrl(offer: KirmaBukinistkaOffer): string {
  const storefront = offer.storefrontUrl.trim();
  if (storefront) return storefront;
  return offer.productAdminUrl.trim();
}

export default function BukinistkaOffersClient() {
  const [rows, setRows] = useState<KirmaBukinistkaOffer[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [busyId, setBusyId] = useState<number | null>(null);
  const [receiptMode, setReceiptMode] = useState(false);
  const [drafts, setDrafts] = useState<Record<number, ReceiptDraft>>({});
  const [acceptOffer, setAcceptOffer] = useState<KirmaBukinistkaOffer | null>(
    null
  );
  const [acceptError, setAcceptError] = useState<string | null>(null);
  const [savingReceipt, setSavingReceipt] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchKirmaBukinistkaOffers()
      .then((offers) => {
        if (!cancelled) setRows(offers);
      })
      .catch((err: Error) => {
        if (!cancelled) setError(err.message);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const visibleRows = useMemo(() => {
    const search = searchQuery.trim().toLowerCase();
    if (!search) return rows;
    return rows.filter((row) => {
      const haystack = [
        row.productName,
        row.productAuthor,
        row.supplierName ?? '',
      ]
        .join(' ')
        .toLowerCase();
      return haystack.includes(search);
    });
  }, [rows, searchQuery]);

  const draftCount = Object.keys(drafts).length;
  const canSaveReceipt = receiptMode && draftCount > 0 && !savingReceipt;

  const startReceipt = () => {
    setReceiptMode(true);
    setError(null);
  };

  const cancelReceipt = () => {
    if (savingReceipt) return;
    setReceiptMode(false);
    setDrafts({});
    setAcceptOffer(null);
    setAcceptError(null);
  };

  const handleReject = async (row: KirmaBukinistkaOffer) => {
    if (receiptMode) return;
    const ok = window.confirm(
      `Адхіліць прапанову «${row.productName}»? Кирмаш убачыць статус «Адхілена».`
    );
    if (!ok) return;
    setBusyId(row.id);
    setError(null);
    try {
      await rejectKirmaBukinistkaOffer(row.id);
      setRows((prev) => prev.filter((item) => item.id !== row.id));
      window.dispatchEvent(new Event('bukinistka-offers-changed'));
    } catch (err: unknown) {
      setError(
        err instanceof Error ? err.message : 'Не ўдалося адхіліць прапанову.'
      );
    } finally {
      setBusyId(null);
    }
  };

  const closeAccept = () => {
    setAcceptOffer(null);
    setAcceptError(null);
  };

  const submitDraftLink = (input: {
    odooProductId: number;
    odooProductName: string;
    listPrice?: number | null;
    applyKirmaCostPrice?: boolean | null;
  }) => {
    if (!acceptOffer) return;
    setDrafts((prev) => ({
      ...prev,
      [acceptOffer.id]: {
        odooProductId: input.odooProductId,
        odooProductName: input.odooProductName,
        listPrice: input.listPrice,
        applyKirmaCostPrice: input.applyKirmaCostPrice,
      },
    }));
    setAcceptOffer(null);
    setAcceptError(null);
  };

  const removeDraft = (offerId: number) => {
    setDrafts((prev) => {
      const next = { ...prev };
      delete next[offerId];
      return next;
    });
  };

  const handleSaveReceipt = async () => {
    if (!canSaveReceipt) return;
    const lines = Object.entries(drafts).map(([offerId, draft]) => ({
      offerId: Number(offerId),
      odooProductId: draft.odooProductId,
      listPrice: draft.listPrice,
      applyKirmaCostPrice: draft.applyKirmaCostPrice,
    }));
    if (lines.length === 0) return;

    setSavingReceipt(true);
    setError(null);
    try {
      const result = await saveBukinistkaOfferReceipt(lines);
      const acceptedIds = new Set(lines.map((l) => l.offerId));
      setRows((prev) => prev.filter((row) => !acceptedIds.has(row.id)));
      setDrafts({});
      setReceiptMode(false);
      window.dispatchEvent(new Event('bukinistka-offers-changed'));
      window.alert(
        result.pickingName
          ? `Прыёмка «${result.pickingName}» створаная ў Odoo.`
          : 'Прыёмка створаная ў Odoo.'
      );
    } catch (err: unknown) {
      setError(
        err instanceof Error ? err.message : 'Не ўдалося захаваць прыёмку.'
      );
    } finally {
      setSavingReceipt(false);
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold text-gray-900">
            Прапановы ад Кирмаша
          </h1>
          <p className="mt-1 text-sm text-gray-500">
            Прадукты, прапанаваныя з панэлі Кирмаша. Націсніце на радок, каб
            адкрыць прадукт на сайце.
          </p>
        </div>
        {!receiptMode ? (
          <button
            type="button"
            onClick={startReceipt}
            disabled={loading || rows.length === 0}
            className="inline-flex items-center rounded-lg bg-amber-700 px-3.5 py-2 text-sm font-medium text-white transition hover:bg-amber-800 disabled:opacity-50"
          >
            Стварыць новую прыёмку
          </button>
        ) : null}
      </div>

      {receiptMode ? (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3">
          <p className="text-sm font-medium text-amber-950">
            Абярыце кнігі для прыёмкі ад Kirma.sh
            {draftCount > 0 ? (
              <span className="ml-2 font-normal text-amber-800">
                ({draftCount})
              </span>
            ) : null}
          </p>
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={cancelReceipt}
              disabled={savingReceipt}
              className="rounded-lg border border-amber-300 bg-white px-3 py-1.5 text-sm font-medium text-amber-900 transition hover:bg-amber-100 disabled:opacity-50"
            >
              Скасаваць
            </button>
            <button
              type="button"
              disabled={!canSaveReceipt}
              onClick={() => {
                void handleSaveReceipt();
              }}
              className="inline-flex items-center gap-2 rounded-lg bg-amber-700 px-3 py-1.5 text-sm font-medium text-white transition hover:bg-amber-800 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {savingReceipt ? (
                <span className="size-3.5 animate-spin rounded-full border-2 border-white/30 border-t-white" />
              ) : null}
              Захаваць
            </button>
          </div>
        </div>
      ) : null}

      <div className="relative max-w-md">
        <FiSearch
          className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-gray-400"
          aria-hidden
        />
        <input
          type="search"
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          placeholder="Пошук па назве, аўтары, пастаўшчыку…"
          className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-9 text-sm text-gray-900 outline-none ring-amber-500/30 focus:border-amber-500 focus:ring-2"
        />
        {searchQuery ? (
          <button
            type="button"
            onClick={() => setSearchQuery('')}
            className="absolute right-2 top-1/2 inline-flex size-6 -translate-y-1/2 items-center justify-center rounded text-gray-400 hover:text-gray-700"
            aria-label="Ачысціць пошук"
          >
            <FiX className="size-4" aria-hidden />
          </button>
        ) : null}
      </div>

      {loading ? (
        <div className="flex justify-center py-16">
          <LoadingSpinner />
        </div>
      ) : error ? (
        <p className="rounded-lg bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </p>
      ) : visibleRows.length === 0 ? (
        <p className="rounded-lg border border-dashed border-gray-200 bg-white px-4 py-10 text-center text-sm text-gray-500">
          Пакуль няма прапаноў.
        </p>
      ) : (
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <table className="w-full table-fixed divide-y divide-gray-100 text-left text-sm">
            <thead className="bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
              <tr>
                <th className="w-[36%] px-4 py-3">Прадукт</th>
                <th className="w-[10%] px-4 py-3 text-right">Колькасць</th>
                <th className="w-[12%] px-4 py-3 text-right">Кошт брута</th>
                <th className="w-[12%] px-4 py-3">Пастаўшчык</th>
                <th className="w-[12%] px-4 py-3">Дата</th>
                <th className="w-[18%] px-4 py-3 text-right">Дзеянне</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {visibleRows.map((row) => {
                const href = offerOpenUrl(row);
                const author = row.productAuthor.trim();
                const busy = busyId === row.id;
                const draft = drafts[row.id];
                return (
                  <tr
                    key={row.id}
                    className={`align-top ${
                      draft ? 'bg-emerald-50/70' : 'hover:bg-amber-50/60'
                    }`}
                  >
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
                          {draft ? (
                            <span className="mt-1 inline-flex max-w-full rounded-full bg-emerald-100 px-2 py-0.5 text-[11px] font-medium text-emerald-900 ring-1 ring-inset ring-emerald-600/20">
                              <span className="truncate">
                                Прынята · {draft.odooProductName}
                              </span>
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
                    <td className="px-4 py-3 text-right">
                      {receiptMode ? (
                        <div className="inline-flex flex-wrap items-center justify-end gap-1.5">
                          {draft ? (
                            <>
                              <button
                                type="button"
                                disabled={savingReceipt}
                                onClick={() => {
                                  setAcceptError(null);
                                  setAcceptOffer(row);
                                }}
                                className="inline-flex items-center rounded-lg border border-emerald-200 bg-white px-2.5 py-1.5 text-xs font-medium text-emerald-800 transition hover:bg-emerald-50 disabled:opacity-50"
                              >
                                Змяніць
                              </button>
                              <button
                                type="button"
                                disabled={savingReceipt}
                                onClick={() => removeDraft(row.id)}
                                className="inline-flex items-center rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-50"
                              >
                                Прыбраць
                              </button>
                            </>
                          ) : (
                            <button
                              type="button"
                              disabled={savingReceipt}
                              onClick={() => {
                                setAcceptError(null);
                                setAcceptOffer(row);
                              }}
                              className="inline-flex items-center rounded-lg border border-emerald-200 bg-white px-2.5 py-1.5 text-xs font-medium text-emerald-800 transition hover:bg-emerald-50 disabled:opacity-50"
                            >
                              Прыняць
                            </button>
                          )}
                        </div>
                      ) : (
                        <div className="inline-flex flex-wrap items-center justify-end gap-1.5">
                          <button
                            type="button"
                            disabled
                            title="Спачатку стварыце прыёмку"
                            className="inline-flex cursor-not-allowed items-center rounded-lg border border-emerald-100 bg-white px-2.5 py-1.5 text-xs font-medium text-emerald-800/40"
                          >
                            Прыняць
                          </button>
                          <button
                            type="button"
                            disabled={busy}
                            onClick={() => {
                              void handleReject(row);
                            }}
                            className="inline-flex items-center rounded-lg border border-red-200 bg-white px-2.5 py-1.5 text-xs font-medium text-red-700 transition hover:bg-red-50 disabled:opacity-50"
                          >
                            {busy ? '…' : 'Адхіліць'}
                          </button>
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <AcceptBukinistkaOfferModal
        open={acceptOffer != null}
        offer={acceptOffer}
        submitting={false}
        error={acceptError}
        onClose={closeAccept}
        onSubmit={submitDraftLink}
      />
    </div>
  );
}
