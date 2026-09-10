'use client';

import AcceptBukinistkaOfferModal from '@/components/bukinistka/AcceptBukinistkaOfferModal';
import ProposeToBukinistkaModal, {
  type ProposeToBukinistkaDraft,
} from '@/components/products/ProposeToBukinistkaModal';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import {
  applyOfferPriceChangeByBukinistka,
  cancelBukinistkaSentOffer,
  deleteBukinistkaReceiptDraft,
  fetchBukinistkaReceiptDraft,
  fetchBukinistkaSentOffers,
  fetchKirmaBukinistkaOffers,
  rejectKirmaBukinistkaOffer,
  saveBukinistkaOfferReceipt,
  updateBukinistkaSentOffer,
  upsertBukinistkaReceiptDraft,
  type BukinistkaOfferReceiptLineInput,
  type BukinistkaReceiptDraft,
  type KirmaBukinistkaOffer,
} from '@/lib/api/bukinistka-offers';
import { useEffect, useMemo, useState } from 'react';
import { FiEdit2, FiExternalLink, FiSearch, FiX } from 'react-icons/fi';

type OffersTabId = 'inbox' | 'sent';

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

function isPendingOffer(row: KirmaBukinistkaOffer): boolean {
  const status = (row.status || 'Pending').trim().toLowerCase();
  return status === 'pending' || status === '';
}

function isAcceptedOffer(row: KirmaBukinistkaOffer): boolean {
  return (row.status || '').trim().toLowerCase() === 'accepted';
}

function canUpdateGrossCost(row: KirmaBukinistkaOffer): boolean {
  if (isPendingOffer(row)) return true;
  return isAcceptedOffer(row) && row.remainingQuantity > 0;
}

function draftsFromServer(
  draft: BukinistkaReceiptDraft
): Record<number, ReceiptDraft> {
  const map: Record<number, ReceiptDraft> = {};
  for (const line of draft.lines) {
    map[line.offerId] = {
      odooProductId: line.odooProductId,
      odooProductName:
        line.odooProductName.trim() || `Odoo #${line.odooProductId}`,
      listPrice: line.listPrice,
      applyKirmaCostPrice: line.applyKirmaCostPrice,
    };
  }
  return map;
}

function linesFromDrafts(
  drafts: Record<number, ReceiptDraft>
): BukinistkaOfferReceiptLineInput[] {
  return Object.entries(drafts).map(([offerId, draft]) => ({
    offerId: Number(offerId),
    odooProductId: draft.odooProductId,
    odooProductName: draft.odooProductName,
    listPrice: draft.listPrice,
    applyKirmaCostPrice: draft.applyKirmaCostPrice,
  }));
}

export default function BukinistkaOffersClient() {
  const [tab, setTab] = useState<OffersTabId>('inbox');
  const [rows, setRows] = useState<KirmaBukinistkaOffer[]>([]);
  const [sentRows, setSentRows] = useState<KirmaBukinistkaOffer[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [busyId, setBusyId] = useState<number | null>(null);
  const [receiptMode, setReceiptMode] = useState(false);
  const [drafts, setDrafts] = useState<Record<number, ReceiptDraft>>({});
  const [draftMeta, setDraftMeta] = useState<{
    id: number;
    status: string;
    lastError: string | null;
  } | null>(null);
  const [acceptOffer, setAcceptOffer] = useState<KirmaBukinistkaOffer | null>(
    null
  );
  const [acceptError, setAcceptError] = useState<string | null>(null);
  const [savingReceipt, setSavingReceipt] = useState(false);
  const [savingDraft, setSavingDraft] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  const [editRow, setEditRow] = useState<KirmaBukinistkaOffer | null>(null);
  const [editDraft, setEditDraft] = useState<ProposeToBukinistkaDraft | null>(
    null
  );
  const [editSubmitting, setEditSubmitting] = useState(false);
  const [editError, setEditError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    Promise.all([
      fetchKirmaBukinistkaOffers(),
      fetchBukinistkaSentOffers(),
      fetchBukinistkaReceiptDraft(),
    ])
      .then(([offers, sent, draft]) => {
        if (cancelled) return;
        setRows(offers);
        setSentRows(sent);
        if (draft && (draft.status === 'Open' || draft.status === 'Failed')) {
          setDraftMeta({
            id: draft.id,
            status: draft.status,
            lastError: draft.lastError,
          });
          setDrafts(draftsFromServer(draft));
          setReceiptMode(true);
          if (draft.status === 'Failed' && draft.lastError) {
            setError(draft.lastError);
          }
        }
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
    let source = tab === 'inbox' ? rows : sentRows;
    if (tab === 'inbox' && receiptMode) {
      source = source.filter((row) => isPendingOffer(row));
    }
    const search = searchQuery.trim().toLowerCase();
    if (!search) return source;
    return source.filter((row) => {
      const haystack = [
        row.productName,
        row.productAuthor,
        row.supplierName ?? '',
      ]
        .join(' ')
        .toLowerCase();
      return haystack.includes(search);
    });
  }, [rows, sentRows, searchQuery, tab, receiptMode]);

  const inboxPriceChangeCount = useMemo(
    () => rows.filter((row) => row.peerPriceChangePending).length,
    [rows]
  );

  const handleCancelSent = async (row: KirmaBukinistkaOffer) => {
    const ok = window.confirm(`Адмяніць прапанову «${row.productName}»?`);
    if (!ok) return;
    setBusyId(row.id);
    try {
      await cancelBukinistkaSentOffer(row.id);
      setSentRows((prev) => prev.filter((item) => item.id !== row.id));
    } catch (err) {
      setError(
        err instanceof Error ? err.message : 'Не ўдалося адмяніць прапанову.'
      );
    } finally {
      setBusyId(null);
    }
  };

  const openEditSent = (row: KirmaBukinistkaOffer) => {
    if (!canUpdateGrossCost(row)) return;
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

  const closeEditSent = () => {
    if (editSubmitting) return;
    setEditOpen(false);
    setEditRow(null);
    setEditDraft(null);
    setEditError(null);
  };

  const submitEditSent = async (quantity: number, grossUnitCost: number) => {
    if (!editRow) return;
    setEditSubmitting(true);
    setEditError(null);
    try {
      const updated = await updateBukinistkaSentOffer(editRow.id, {
        quantity: isAcceptedOffer(editRow) ? editRow.quantity : quantity,
        grossUnitCost,
      });
      setSentRows((prev) =>
        prev.map((row) => (row.id === updated.id ? updated : row))
      );
      setEditOpen(false);
      setEditRow(null);
      setEditDraft(null);
      window.dispatchEvent(new Event('bukinistka-offers-changed'));
    } catch (err: unknown) {
      setEditError(
        err instanceof Error ? err.message : 'Не ўдалося абнавіць прапанову.'
      );
    } finally {
      setEditSubmitting(false);
    }
  };

  const handleApplyPriceChange = async (row: KirmaBukinistkaOffer) => {
    setBusyId(row.id);
    setError(null);
    try {
      const updated = await applyOfferPriceChangeByBukinistka(row.id);
      setRows((prev) =>
        prev
          .map((item) => (item.id === updated.id ? updated : item))
          .filter(
            (item) =>
              isPendingOffer(item) || Boolean(item.peerPriceChangePending)
          )
      );
      window.dispatchEvent(new Event('bukinistka-offers-changed'));
    } catch (err: unknown) {
      setError(
        err instanceof Error ? err.message : 'Не ўдалося абнавіць кошт у Odoo.'
      );
    } finally {
      setBusyId(null);
    }
  };

  const draftCount = Object.keys(drafts).length;
  const canSaveReceipt = receiptMode && draftCount > 0 && !savingReceipt;
  const hasSavedDraft = draftMeta != null;

  const persistDrafts = async (nextDrafts: Record<number, ReceiptDraft>) => {
    setSavingDraft(true);
    try {
      const saved = await upsertBukinistkaReceiptDraft(
        linesFromDrafts(nextDrafts)
      );
      setDraftMeta({
        id: saved.id,
        status: saved.status,
        lastError: saved.lastError,
      });
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : 'Не ўдалося захаваць чарнавік прыёмкі.'
      );
    } finally {
      setSavingDraft(false);
    }
  };

  const startReceipt = () => {
    setReceiptMode(true);
    setError(null);
    if (draftMeta?.status === 'Failed') {
      setDraftMeta((prev) =>
        prev ? { ...prev, status: 'Open', lastError: null } : prev
      );
    }
  };

  const openSavedReceipt = () => {
    setReceiptMode(true);
    if (draftMeta?.status === 'Failed' && draftMeta.lastError) {
      setError(draftMeta.lastError);
    } else {
      setError(null);
    }
  };

  const cancelReceipt = async () => {
    if (savingReceipt || savingDraft) return;
    setSavingDraft(true);
    setError(null);
    try {
      await deleteBukinistkaReceiptDraft();
      setDraftMeta(null);
      setReceiptMode(false);
      setDrafts({});
      setAcceptOffer(null);
      setAcceptError(null);
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : 'Не ўдалося скасаваць чарнавік прыёмкі.'
      );
    } finally {
      setSavingDraft(false);
    }
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
    const offerId = acceptOffer.id;
    const nextDrafts = {
      ...drafts,
      [offerId]: {
        odooProductId: input.odooProductId,
        odooProductName: input.odooProductName,
        listPrice: input.listPrice,
        applyKirmaCostPrice: input.applyKirmaCostPrice,
      },
    };
    setDrafts(nextDrafts);
    setAcceptOffer(null);
    setAcceptError(null);
    setError(null);
    void persistDrafts(nextDrafts);
  };

  const removeDraft = (offerId: number) => {
    const nextDrafts = { ...drafts };
    delete nextDrafts[offerId];
    setDrafts(nextDrafts);
    void persistDrafts(nextDrafts);
  };

  const handleSaveReceipt = async () => {
    if (!canSaveReceipt) return;
    const lines = linesFromDrafts(drafts);
    if (lines.length === 0) return;

    setSavingReceipt(true);
    setError(null);
    try {
      const result = await saveBukinistkaOfferReceipt(lines);
      const acceptedIds = new Set(lines.map((l) => l.offerId));
      setRows((prev) => prev.filter((row) => !acceptedIds.has(row.id)));
      setDrafts({});
      setDraftMeta(null);
      setReceiptMode(false);
      window.dispatchEvent(new Event('bukinistka-offers-changed'));
      window.alert(
        result.pickingName
          ? `Прыёмка «${result.pickingName}» створаная ў Odoo.`
          : 'Прыёмка створаная ў Odoo.'
      );
    } catch (err: unknown) {
      const message =
        err instanceof Error ? err.message : 'Не ўдалося захаваць прыёмку.';
      setError(message);
      setDraftMeta((prev) =>
        prev
          ? { ...prev, status: 'Failed', lastError: message }
          : { id: 0, status: 'Failed', lastError: message }
      );
      try {
        const refreshed = await fetchBukinistkaReceiptDraft();
        if (refreshed) {
          setDraftMeta({
            id: refreshed.id,
            status: refreshed.status,
            lastError: refreshed.lastError ?? message,
          });
          if (refreshed.lines.length > 0) {
            setDrafts(draftsFromServer(refreshed));
          }
        }
      } catch {
        /* keep local drafts */
      }
    } finally {
      setSavingReceipt(false);
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          onClick={() => {
            setTab('inbox');
          }}
          className={`inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium transition ${
            tab === 'inbox'
              ? 'bg-amber-700 text-white'
              : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
          }`}
        >
          Ад Кірмаша
          {inboxPriceChangeCount > 0 ? (
            <span
              className={`inline-flex min-w-5 items-center justify-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${
                tab === 'inbox'
                  ? 'bg-white/25 text-white'
                  : 'bg-amber-500 text-white'
              }`}
            >
              {inboxPriceChangeCount}
            </span>
          ) : null}
        </button>
        <button
          type="button"
          onClick={() => {
            setTab('sent');
            setReceiptMode(false);
          }}
          className={`rounded-lg px-3 py-1.5 text-sm font-medium transition ${
            tab === 'sent'
              ? 'bg-amber-700 text-white'
              : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
          }`}
        >
          Высланыя Кірмашу
        </button>
      </div>

      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-sm text-gray-500">
            {tab === 'inbox'
              ? 'Прадукты ад Кірмаша. Калі Кірмаш змяніў кошт брута пасля прыёмкі — з’явіцца паведамленне і кнопка «У Odoo».'
              : 'Прапановы Кірмашу. Для чакаючых і прынятых (яшчэ не прададзеных) можна абнавіць кошт брута.'}
          </p>
        </div>
        {tab === 'inbox' && !receiptMode ? (
          <div className="flex flex-wrap items-center gap-2">
            {hasSavedDraft ? (
              <button
                type="button"
                onClick={openSavedReceipt}
                disabled={loading}
                className="inline-flex items-center rounded-lg border border-amber-300 bg-white px-3.5 py-2 text-sm font-medium text-amber-900 transition hover:bg-amber-50 disabled:opacity-50"
              >
                Адкрыць захаваную прыёмку
                {draftCount > 0 ? ` (${draftCount})` : ''}
              </button>
            ) : null}
            <button
              type="button"
              onClick={startReceipt}
              disabled={loading || rows.length === 0}
              className="inline-flex items-center rounded-lg bg-amber-700 px-3.5 py-2 text-sm font-medium text-white transition hover:bg-amber-800 disabled:opacity-50"
            >
              Стварыць новую прыёмку
            </button>
          </div>
        ) : null}
      </div>

      {receiptMode ? (
        <div className="space-y-2">
          {draftMeta?.status === 'Failed' ? (
            <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
              <p className="font-medium">
                Не ўдалося захаваць прыёмку ў Odoo. Чарнавік захаваны — можаце
                паспрабаваць зноў.
              </p>
              {draftMeta.lastError || error ? (
                <p className="mt-1 text-red-700">
                  {draftMeta.lastError || error}
                </p>
              ) : null}
            </div>
          ) : null}
          <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3">
            <p className="text-sm font-medium text-amber-950">
              Абярыце кнігі для прыёмкі ад Kirma.sh
              {draftCount > 0 ? (
                <span className="ml-2 font-normal text-amber-800">
                  ({draftCount})
                </span>
              ) : null}
              {savingDraft ? (
                <span className="ml-2 font-normal text-amber-700">
                  · захоўваем чарнавік…
                </span>
              ) : draftMeta ? (
                <span className="ml-2 font-normal text-amber-700">
                  · чарнавік захаваны
                </span>
              ) : null}
            </p>
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={() => {
                  void cancelReceipt();
                }}
                disabled={savingReceipt || savingDraft}
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
                {draftMeta?.status === 'Failed'
                  ? 'Паўтарыць захаванне ў Odoo'
                  : 'Захаваць'}
              </button>
            </div>
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
      ) : error && !receiptMode ? (
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
                const priceChange = Boolean(row.peerPriceChangePending);
                const accepted = isAcceptedOffer(row);
                const pending = isPendingOffer(row);
                const canEditCost = canUpdateGrossCost(row);
                return (
                  <tr
                    key={row.id}
                    className={`align-top ${
                      draft
                        ? 'bg-emerald-50/70'
                        : priceChange
                          ? 'bg-amber-50/70'
                          : 'hover:bg-amber-50/60'
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
                          {row.isAssignment ? (
                            <span className="mt-1 inline-flex rounded-full bg-violet-50 px-2 py-0.5 text-[11px] font-medium text-violet-800 ring-1 ring-inset ring-violet-600/20">
                              Назначэнне
                            </span>
                          ) : null}
                          {priceChange ? (
                            <span className="mt-1 ml-1 inline-flex rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-medium text-amber-900 ring-1 ring-inset ring-amber-600/20">
                              Новы кошт брута
                            </span>
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
                                disabled={savingReceipt || savingDraft}
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
                                disabled={savingReceipt || savingDraft}
                                onClick={() => removeDraft(row.id)}
                                className="inline-flex items-center rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-50"
                              >
                                Прыбраць
                              </button>
                            </>
                          ) : (
                            <button
                              type="button"
                              disabled={savingReceipt || savingDraft}
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
                      ) : tab === 'sent' ? (
                        <div className="inline-flex flex-wrap items-center justify-end gap-1.5">
                          <span className="text-xs text-gray-500">
                            {accepted
                              ? 'Прынята'
                              : (row.status || '').toLowerCase() === 'rejected'
                                ? 'Адхілена'
                                : 'Чакае'}
                          </span>
                          {canEditCost ? (
                            <button
                              type="button"
                              disabled={busy}
                              onClick={() => openEditSent(row)}
                              className="inline-flex size-8 items-center justify-center rounded-lg border border-gray-200 bg-white text-gray-700 transition hover:border-amber-300 hover:bg-amber-50 hover:text-amber-900 disabled:opacity-50"
                              aria-label="Абнавіць кошт брута"
                              title="Абнавіць кошт брута"
                            >
                              <FiEdit2 className="size-3.5" aria-hidden />
                            </button>
                          ) : null}
                          {!accepted ? (
                            <button
                              type="button"
                              disabled={busy}
                              onClick={() => {
                                void handleCancelSent(row);
                              }}
                              className="inline-flex items-center rounded-lg border border-red-200 bg-white px-2.5 py-1.5 text-xs font-medium text-red-700 transition hover:bg-red-50 disabled:opacity-50"
                            >
                              Адмяніць
                            </button>
                          ) : null}
                        </div>
                      ) : priceChange && accepted ? (
                        <div className="inline-flex flex-wrap items-center justify-end gap-1.5">
                          <button
                            type="button"
                            disabled={busy}
                            onClick={() => {
                              void handleApplyPriceChange(row);
                            }}
                            className="inline-flex items-center rounded-lg border border-amber-300 bg-amber-50 px-2.5 py-1.5 text-xs font-medium text-amber-950 transition hover:bg-amber-100 disabled:opacity-50"
                            title="Запісаць новы кошт брута ў Odoo (standard_price)"
                          >
                            {busy ? '…' : 'У Odoo'}
                          </button>
                        </div>
                      ) : pending ? (
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
                      ) : (
                        <span className="text-xs text-gray-400">—</span>
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

      <ProposeToBukinistkaModal
        open={editOpen}
        draft={editDraft}
        submitting={editSubmitting}
        error={editError}
        title={
          editRow && isAcceptedOffer(editRow)
            ? 'Абнавіць кошт брута'
            : 'Рэдагаваць прапанову'
        }
        submitLabel="Захаваць"
        showSyncOnSale={false}
        quantityLocked={Boolean(editRow && isAcceptedOffer(editRow))}
        onClose={closeEditSent}
        onSubmit={(quantity, grossUnitCost) => {
          void submitEditSent(quantity, grossUnitCost);
        }}
      />
    </div>
  );
}
