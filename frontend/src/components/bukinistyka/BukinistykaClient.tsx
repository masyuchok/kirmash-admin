'use client';

import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { FiEdit2, FiExternalLink, FiSearch, FiX } from 'react-icons/fi';
import ProposeToBukinistkaModal, {
  type ProposeToBukinistkaDraft,
} from '@/components/products/ProposeToBukinistkaModal';
import AcceptReceivedOfferModal from '@/components/bukinistyka/AcceptReceivedOfferModal';
import IssueBukinistkaInvoiceModal from '@/components/bukinistyka/IssueBukinistkaInvoiceModal';
import OfferShopifyPriceCell from '@/components/bukinistyka/OfferShopifyPriceCell';
import { useTopbar } from '@/components/topbar/TopbarContext';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { usePortalMenu } from '@/hooks/usePortalMenu';
import {
  acceptBukinistkaOfferByKirma,
  applyOfferPriceChangeByKirma,
  cancelKirmaBukinistkaOffer,
  fetchKirmaReceivedBukinistkaOffers,
  fetchKirmaSentBukinistkaOffers,
  notifyBukinistkaOffersChanged,
  rejectBukinistkaOfferByKirma,
  updateKirmaBukinistkaOffer,
  updateOfferShopifySalePriceByKirma,
  type KirmaBukinistkaOffer,
} from '@/lib/api/bukinistka-offers';
import { fetchProductsWithSuppliers } from '@/lib/api/products';
import {
  fetchBukinistkaPosSales,
  syncBukinistkaPosSales,
  type BukinistkaPosSale,
} from '@/lib/api/bukinistka-sales';

type MainTabId = 'offers' | 'sales';
type OffersSubTabId = 'sent' | 'received';
type ReceivedStatusFilter = 'all' | 'pending' | 'accepted';

const EMPTY_SUPPLIER = '__none__';

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

function supplierKey(name: string | null | undefined): string {
  const trimmed = name?.trim() ?? '';
  return trimmed || EMPTY_SUPPLIER;
}

function supplierLabel(key: string): string {
  return key === EMPTY_SUPPLIER ? '—' : key;
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

function canUpdateGrossCost(row: KirmaBukinistkaOffer): boolean {
  if (isPendingOffer(row)) return true;
  return isAcceptedOffer(row) && row.remainingQuantity > 0;
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
  onAccept,
  onReject,
  onApplyPriceChange,
  showShopifyPriceColumn,
  showBukinistkaSalePriceColumn,
  shopifySalePriceByProductId,
  pricingVatRate,
  onSaveShopifySalePrice,
}: {
  rows: KirmaBukinistkaOffer[];
  emptyText: string;
  busyId: number | null;
  onEdit?: (row: KirmaBukinistkaOffer) => void;
  onCancel?: (row: KirmaBukinistkaOffer) => void;
  onDeleteRejected?: (row: KirmaBukinistkaOffer) => void;
  onAccept?: (row: KirmaBukinistkaOffer) => void;
  onReject?: (row: KirmaBukinistkaOffer) => void;
  onApplyPriceChange?: (row: KirmaBukinistkaOffer) => void;
  showShopifyPriceColumn?: boolean;
  showBukinistkaSalePriceColumn?: boolean;
  shopifySalePriceByProductId?: Map<string, number>;
  pricingVatRate?: 5 | 23;
  onSaveShopifySalePrice?: (
    row: KirmaBukinistkaOffer,
    salePrice: number
  ) => Promise<void>;
}) {
  if (rows.length === 0) {
    return (
      <p className="rounded-lg border border-dashed border-gray-200 bg-gray-50 px-4 py-10 text-center text-sm text-gray-500">
        {emptyText}
      </p>
    );
  }

  const showActions = Boolean(
    onEdit ||
      onCancel ||
      onDeleteRejected ||
      onAccept ||
      onReject ||
      onApplyPriceChange
  );

  const productColClass =
    showShopifyPriceColumn || showBukinistkaSalePriceColumn
      ? showActions
        ? 'w-[24%]'
        : 'w-[28%]'
      : showActions
        ? 'w-[38%]'
        : 'w-[46%]';

  return (
    <div className="overflow-hidden rounded-xl border border-gray-200">
      <table className="w-full table-fixed divide-y divide-gray-100 text-left text-sm">
        <thead className="bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
          <tr>
            <th className={`${productColClass} px-4 py-3`}>Прадукт</th>
            <th className="w-[8%] px-4 py-3 text-right">Колькасць</th>
            <th className="w-[10%] px-4 py-3 text-right">Кошт брута</th>
            {showBukinistkaSalePriceColumn ? (
              <th className="w-[10%] px-4 py-3 text-right">Цана Букіністкі</th>
            ) : null}
            {showShopifyPriceColumn ? (
              <th className="w-[12%] px-4 py-3 text-right">Цана Shopify</th>
            ) : null}
            <th className="w-[10%] px-4 py-3">Выдавец</th>
            <th className="w-[9%] px-4 py-3">Дата</th>
            {showActions ? (
              <th className="w-[13%] px-4 py-3 text-right">Дзеянні</th>
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
            const priceChange = Boolean(row.peerPriceChangePending);
            const canEditCost = Boolean(onEdit && canUpdateGrossCost(row));
            return (
              <tr
                key={row.id}
                className={`align-top ${priceChange ? 'bg-amber-50/70' : ''}`}
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
                      {priceChange ? (
                        <span className="mt-1 ml-1 inline-flex rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-medium text-amber-900 ring-1 ring-inset ring-amber-600/20">
                          Новы кошт брута
                        </span>
                      ) : null}
                      {row.isAssignment ? (
                        <span className="mt-1 ml-1 inline-flex rounded-full bg-violet-50 px-2 py-0.5 text-[11px] font-medium text-violet-800 ring-1 ring-inset ring-violet-600/20">
                          Назначэнне
                        </span>
                      ) : null}
                      {row.syncOnSale ? (
                        <span className="mt-1 ml-1 inline-flex rounded-full bg-sky-50 px-2 py-0.5 text-[11px] font-medium text-sky-800 ring-1 ring-inset ring-sky-600/20">
                          Sync пры продажы
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
                {showBukinistkaSalePriceColumn ? (
                  <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums text-indigo-900">
                    {row.bukinistkaSalePrice != null &&
                    row.bukinistkaSalePrice > 0
                      ? formatPrice(row.bukinistkaSalePrice)
                      : '—'}
                  </td>
                ) : null}
                {showShopifyPriceColumn ? (
                  <td className="px-4 py-3">
                    {accepted &&
                    row.shopifyProductId.trim() &&
                    onSaveShopifySalePrice ? (
                      <OfferShopifyPriceCell
                        offer={row}
                        shopifySalePrice={
                          row.shopifySalePrice ??
                          shopifySalePriceByProductId?.get(
                            row.shopifyProductId.trim()
                          ) ??
                          0
                        }
                        vatRate={pricingVatRate ?? 5}
                        disabled={busy}
                        onSave={(salePrice) =>
                          onSaveShopifySalePrice(row, salePrice)
                        }
                      />
                    ) : (
                      <span className="text-xs text-gray-400">—</span>
                    )}
                  </td>
                ) : null}
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
                    {pending && onAccept ? (
                      <div className="inline-flex items-center justify-end gap-1.5">
                        <button
                          type="button"
                          disabled={busy}
                          onClick={() => onAccept(row)}
                          className="inline-flex items-center rounded-lg border border-emerald-200 bg-emerald-50 px-2.5 py-1.5 text-xs font-medium text-emerald-900 transition hover:bg-emerald-100 disabled:opacity-50"
                        >
                          Прыняць
                        </button>
                        <button
                          type="button"
                          disabled={busy}
                          onClick={() => onReject?.(row)}
                          className="inline-flex items-center rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-700 transition hover:border-red-300 hover:bg-red-50 hover:text-red-700 disabled:opacity-50"
                        >
                          Адхіліць
                        </button>
                      </div>
                    ) : pending ? (
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
                    ) : (
                      <div className="inline-flex flex-wrap items-center justify-end gap-1.5">
                        {priceChange && onApplyPriceChange ? (
                          <button
                            type="button"
                            disabled={busy}
                            onClick={() => onApplyPriceChange(row)}
                            className="inline-flex items-center rounded-lg border border-amber-300 bg-amber-50 px-2.5 py-1.5 text-xs font-medium text-amber-950 transition hover:bg-amber-100 disabled:opacity-50"
                            title="Запісаць новы кошт брута ў Shopify (InventoryItem.cost)"
                          >
                            {busy ? '…' : 'У Shopify'}
                          </button>
                        ) : null}
                        {canEditCost ? (
                          <button
                            type="button"
                            disabled={busy}
                            onClick={() => onEdit?.(row)}
                            className="inline-flex size-8 items-center justify-center rounded-lg border border-gray-200 bg-white text-gray-700 transition hover:border-primary/30 hover:bg-primary/5 hover:text-primary disabled:opacity-50"
                            aria-label="Абнавіць кошт брута"
                            title="Абнавіць кошт брута"
                          >
                            <FiEdit2 className="size-3.5" aria-hidden />
                          </button>
                        ) : !priceChange ? (
                          <span className="text-xs text-gray-400">—</span>
                        ) : null}
                      </div>
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
  const [receivedOffers, setReceivedOffers] = useState<KirmaBukinistkaOffer[]>(
    []
  );
  const [searchQuery, setSearchQuery] = useState('');
  const [receivedStatusFilter, setReceivedStatusFilter] =
    useState<ReceivedStatusFilter>('all');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<number | null>(null);
  const [editOpen, setEditOpen] = useState(false);
  const [editRow, setEditRow] = useState<KirmaBukinistkaOffer | null>(null);
  const [editDraft, setEditDraft] = useState<ProposeToBukinistkaDraft | null>(
    null
  );
  const [editSubmitting, setEditSubmitting] = useState(false);
  const [editError, setEditError] = useState<string | null>(null);
  const [acceptOffer, setAcceptOffer] = useState<KirmaBukinistkaOffer | null>(
    null
  );
  const [acceptSubmitting, setAcceptSubmitting] = useState(false);
  const [acceptError, setAcceptError] = useState<string | null>(null);
  const [sales, setSales] = useState<BukinistkaPosSale[]>([]);
  const [salesLoading, setSalesLoading] = useState(false);
  const [salesError, setSalesError] = useState<string | null>(null);
  const [salesSyncing, setSalesSyncing] = useState(false);
  const [invoiceOpen, setInvoiceOpen] = useState(false);
  const [salesSearchQuery, setSalesSearchQuery] = useState('');
  const [selectedSaleSuppliers, setSelectedSaleSuppliers] = useState<string[]>(
    []
  );
  const salesSupplierMenu = usePortalMenu({
    menuWidth: 260,
    estimatedMenuHeight: 320,
  });
  const [shopifySalePriceByProductId, setShopifySalePriceByProductId] =
    useState<Map<string, number>>(() => new Map());
  const [pricingVatRate, setPricingVatRate] = useState<5 | 23>(5);

  useEffect(() => {
    if (activeTab !== 'offers' || offersSubTab !== 'received') return;

    let cancelled = false;
    fetchProductsWithSuppliers()
      .then((rows) => {
        if (cancelled) return;
        const map = new Map<string, number>();
        for (const row of rows) {
          const id = row.shopifyProductId.trim();
          if (id && row.shopifySalePrice > 0) {
            map.set(id, row.shopifySalePrice);
          }
        }
        setShopifySalePriceByProductId(map);
      })
      .catch(() => {
        if (!cancelled) setShopifySalePriceByProductId(new Map());
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, offersSubTab, receivedOffers]);

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

  const reloadReceived = async () => {
    const rows = await fetchKirmaReceivedBukinistkaOffers();
    setReceivedOffers(rows);
  };

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    Promise.all([
      fetchKirmaSentBukinistkaOffers(),
      fetchKirmaReceivedBukinistkaOffers(),
    ])
      .then(([sent, received]) => {
        if (!cancelled) {
          setSentOffers(sent);
          setReceivedOffers(received);
        }
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
        quantity: isAcceptedOffer(editRow) ? editRow.quantity : quantity,
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

  const handleApplyPriceChange = async (row: KirmaBukinistkaOffer) => {
    setBusyId(row.id);
    setError(null);
    try {
      const updated = await applyOfferPriceChangeByKirma(row.id);
      setReceivedOffers((prev) =>
        prev.map((item) => (item.id === updated.id ? updated : item))
      );
      notifyBukinistkaOffersChanged();
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : 'Не ўдалося абнавіць кошт у Shopify.'
      );
    } finally {
      setBusyId(null);
    }
  };

  const handleSaveShopifySalePrice = async (
    row: KirmaBukinistkaOffer,
    salePrice: number
  ) => {
    setBusyId(row.id);
    setError(null);
    try {
      await updateOfferShopifySalePriceByKirma(row.id, salePrice);
      const productId = row.shopifyProductId.trim();
      setReceivedOffers((prev) =>
        prev.map((item) =>
          item.id === row.id ? { ...item, shopifySalePrice: salePrice } : item
        )
      );
      if (productId) {
        setShopifySalePriceByProductId((prev) => {
          const next = new Map(prev);
          next.set(productId, salePrice);
          return next;
        });
      }
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : 'Не ўдалося абнавіць цану ў Shopify.'
      );
      throw err;
    } finally {
      setBusyId(null);
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

  const filteredSentOffers = useMemo(() => {
    const search = searchQuery.trim().toLowerCase();
    if (!search) return sentOffers;
    return sentOffers.filter((row) => {
      const haystack = [
        row.productName,
        row.productAuthor,
        row.supplierName ?? '',
      ]
        .join(' ')
        .toLowerCase();
      return haystack.includes(search);
    });
  }, [sentOffers, searchQuery]);

  const filteredReceivedOffers = useMemo(() => {
    const search = searchQuery.trim().toLowerCase();
    let rows = receivedOffers;

    if (receivedStatusFilter === 'pending') {
      rows = rows.filter(isPendingOffer);
    } else if (receivedStatusFilter === 'accepted') {
      rows = rows.filter(isAcceptedOffer);
    }

    if (search) {
      rows = rows.filter((row) => {
        const haystack = [
          row.productName,
          row.productAuthor,
          row.supplierName ?? '',
        ]
          .join(' ')
          .toLowerCase();
        return haystack.includes(search);
      });
    }

    const statusRank = (row: KirmaBukinistkaOffer): number => {
      if (isPendingOffer(row)) return 0;
      if (isAcceptedOffer(row)) return 1;
      if (isRejectedOffer(row)) return 2;
      return 3;
    };

    return [...rows].sort((a, b) => {
      const byStatus = statusRank(a) - statusRank(b);
      if (byStatus !== 0) return byStatus;
      return (
        new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime()
      );
    });
  }, [receivedOffers, searchQuery, receivedStatusFilter]);

  const receivedStatusCounts = useMemo(
    () => ({
      all: receivedOffers.length,
      pending: receivedOffers.filter(isPendingOffer).length,
      accepted: receivedOffers.filter(isAcceptedOffer).length,
    }),
    [receivedOffers]
  );

  const saleSupplierOptions = useMemo(() => {
    const keys = new Set<string>();
    for (const row of sales) {
      keys.add(supplierKey(row.supplierName));
    }
    return Array.from(keys).sort((a, b) =>
      supplierLabel(a).localeCompare(supplierLabel(b), 'be')
    );
  }, [sales]);

  const filteredSales = useMemo(() => {
    const search = salesSearchQuery.trim().toLowerCase();
    return sales.filter((row) => {
      if (
        selectedSaleSuppliers.length > 0 &&
        !selectedSaleSuppliers.includes(supplierKey(row.supplierName))
      ) {
        return false;
      }
      if (!search) return true;
      return row.productName.toLowerCase().includes(search);
    });
  }, [sales, salesSearchQuery, selectedSaleSuppliers]);

  const salesTotals = useMemo(() => {
    let units = 0;
    let gross = 0;
    for (const row of filteredSales) {
      units += row.quantity;
      if (row.grossUnitCost != null && Number.isFinite(row.grossUnitCost)) {
        gross += row.grossUnitCost * row.quantity;
      }
    }
    return { units, gross, rows: filteredSales.length };
  }, [filteredSales]);

  const salesSupplierFilterActive = selectedSaleSuppliers.length > 0;

  const toggleSaleSupplierFilter = (supplier: string) => {
    setSelectedSaleSuppliers((prev) =>
      prev.includes(supplier)
        ? prev.filter((s) => s !== supplier)
        : [...prev, supplier]
    );
  };

  const handleRejectReceived = async (row: KirmaBukinistkaOffer) => {
    const ok = window.confirm(`Адхіліць прапанову «${row.productName}»?`);
    if (!ok) return;
    setBusyId(row.id);
    setError(null);
    try {
      await rejectBukinistkaOfferByKirma(row.id);
      setReceivedOffers((prev) =>
        prev.map((item) =>
          item.id === row.id ? { ...item, status: 'Rejected' } : item
        )
      );
      notifyBukinistkaOffersChanged();
    } catch (err: unknown) {
      setError(
        err instanceof Error ? err.message : 'Не ўдалося адхіліць прапанову.'
      );
      try {
        await reloadReceived();
      } catch {
        // ignore
      }
    } finally {
      setBusyId(null);
    }
  };

  const handleAcceptReceived = async (input: {
    shopifyProductId: string;
    shopifyVariantId?: string;
    salePrice?: number | null;
  }) => {
    if (!acceptOffer) return;
    setAcceptSubmitting(true);
    setAcceptError(null);
    try {
      const result = await acceptBukinistkaOfferByKirma(acceptOffer.id, input);
      setReceivedOffers((prev) =>
        prev.map((item) => (item.id === result.offer.id ? result.offer : item))
      );
      setAcceptOffer(null);
      setError(null);
      setNotice(result.shopifySyncWarning);
      notifyBukinistkaOffersChanged();
    } catch (err: unknown) {
      setAcceptError(
        err instanceof Error ? err.message : 'Не ўдалося прыняць прапанову.'
      );
    } finally {
      setAcceptSubmitting(false);
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
              {offersSubTabs.map((tab) => {
                const priceChangeCount =
                  tab.id === 'received'
                    ? receivedOffers.filter((r) => r.peerPriceChangePending)
                        .length
                    : 0;
                return (
                  <button
                    key={tab.id}
                    type="button"
                    onClick={() => {
                      setOffersSubTab(tab.id);
                      setError(null);
                    }}
                    className={`inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium transition ${
                      offersSubTab === tab.id
                        ? 'bg-primary/10 text-primary'
                        : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
                    }`}
                  >
                    {tab.label}
                    {priceChangeCount > 0 ? (
                      <span className="inline-flex min-w-5 items-center justify-center rounded-full bg-amber-500 px-1.5 py-0.5 text-[10px] font-semibold text-white">
                        {priceChangeCount}
                      </span>
                    ) : null}
                  </button>
                );
              })}
            </div>

            {notice ? (
              <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
                {notice}
              </div>
            ) : null}

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
                  Прапановы, дасланыя ў Букіністыку. Непрынятыя можна рэдагаваць
                  або адмяніць; для прынятых (яшчэ не прададзеных) — абнавіць
                  кошт брута; адхіленыя — выдаліць галочкай.
                </p>
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
                    className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-9 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2"
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
                <OffersTable
                  rows={filteredSentOffers}
                  emptyText={
                    searchQuery.trim()
                      ? 'Нічога не знойдзена па гэтым пошуку.'
                      : 'Пакуль няма высланых прапаноў.'
                  }
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
                  Прапановы і назначэнні ад Букіністкі. Для прынятых — усталюйце
                  цану продажу ў Shopify; маржа разлічваецца ад кошту брута.
                  Калі Букіністка змяніла кошт брута — кнопка «У Shopify» запіше
                  новы закупочны кошт.
                </p>
                <div className="flex flex-wrap items-center gap-3">
                  <div className="relative min-w-[200px] max-w-md flex-1">
                    <FiSearch
                      className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-gray-400"
                      aria-hidden
                    />
                    <input
                      type="search"
                      value={searchQuery}
                      onChange={(e) => setSearchQuery(e.target.value)}
                      placeholder="Пошук па назве, аўтары, выдавец…"
                      className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-9 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2"
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
                  <div
                    className="flex flex-wrap gap-1.5"
                    role="group"
                    aria-label="Фільтр статусу"
                  >
                    {(
                      [
                        {
                          id: 'all' as const,
                          label: 'Усе',
                          count: receivedStatusCounts.all,
                        },
                        {
                          id: 'pending' as const,
                          label: 'Непрынятыя',
                          count: receivedStatusCounts.pending,
                        },
                        {
                          id: 'accepted' as const,
                          label: 'Прынятыя',
                          count: receivedStatusCounts.accepted,
                        },
                      ] as const
                    ).map((option) => (
                      <button
                        key={option.id}
                        type="button"
                        onClick={() => setReceivedStatusFilter(option.id)}
                        className={`inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium transition ${
                          receivedStatusFilter === option.id
                            ? 'bg-primary/10 text-primary'
                            : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
                        }`}
                      >
                        {option.label}
                        <span
                          className={`inline-flex min-w-5 items-center justify-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${
                            receivedStatusFilter === option.id
                              ? 'bg-primary text-white'
                              : 'bg-gray-200 text-gray-700'
                          }`}
                        >
                          {option.count}
                        </span>
                      </button>
                    ))}
                  </div>
                  <label className="flex items-center gap-2 text-sm text-gray-600">
                    VAT для маржы
                    <select
                      value={pricingVatRate}
                      onChange={(e) =>
                        setPricingVatRate(
                          Number(e.target.value) === 23 ? 23 : 5
                        )
                      }
                      className="rounded-lg border border-gray-200 bg-white px-2 py-1 text-sm text-gray-900"
                    >
                      <option value={5}>5%</option>
                      <option value={23}>23%</option>
                    </select>
                  </label>
                </div>
                <OffersTable
                  rows={filteredReceivedOffers}
                  emptyText={
                    searchQuery.trim() || receivedStatusFilter !== 'all'
                      ? 'Нічога не знойдзена.'
                      : 'Пакуль няма атрыманых прапаноў.'
                  }
                  busyId={busyId}
                  showShopifyPriceColumn
                  showBukinistkaSalePriceColumn
                  shopifySalePriceByProductId={shopifySalePriceByProductId}
                  pricingVatRate={pricingVatRate}
                  onSaveShopifySalePrice={handleSaveShopifySalePrice}
                  onAccept={(row) => {
                    setAcceptError(null);
                    setAcceptOffer(row);
                  }}
                  onReject={(row) => {
                    void handleRejectReceived(row);
                  }}
                  onApplyPriceChange={(row) => {
                    void handleApplyPriceChange(row);
                  }}
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
                  Аплачаныя фактурай знікаюць са спісу.
                </p>
              </div>
              <div className="flex flex-wrap items-center gap-2">
                <button
                  type="button"
                  disabled={salesLoading || filteredSales.length === 0}
                  onClick={() => setInvoiceOpen(true)}
                  className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-800 transition hover:bg-gray-50 disabled:opacity-50"
                >
                  Выставіць фактуру
                </button>
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
              <div className="space-y-3">
                <div className="flex flex-wrap items-center gap-3">
                  <div className="relative min-w-[220px] flex-1">
                    <FiSearch
                      className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-gray-400"
                      aria-hidden
                    />
                    <input
                      type="search"
                      value={salesSearchQuery}
                      onChange={(e) => setSalesSearchQuery(e.target.value)}
                      placeholder="Пошук па назве…"
                      className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-3 text-sm text-gray-900 outline-none ring-primary/30 focus:border-primary focus:ring-2"
                    />
                  </div>
                  <p className="text-sm text-gray-700">
                    Радкоў: {salesTotals.rows} · Тавараў: {salesTotals.units} ·
                    Брута:{' '}
                    <span className="font-semibold tabular-nums text-gray-900">
                      {formatPrice(salesTotals.gross)}
                    </span>
                  </p>
                </div>

                <div className="overflow-hidden rounded-xl border border-gray-200">
                  <table className="w-full table-fixed divide-y divide-gray-100 text-left text-sm">
                    <thead className="bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                      <tr>
                        <th className="w-[28%] px-4 py-3">Прадукт</th>
                        <th className="w-[10%] px-4 py-3 text-right">
                          Колькасць
                        </th>
                        <th className="w-[12%] px-4 py-3 text-right">Брута</th>
                        <th className="w-[16%] px-4 py-3">
                          <button
                            type="button"
                            ref={salesSupplierMenu.triggerRef}
                            onClick={salesSupplierMenu.toggle}
                            className={`inline-flex items-center gap-1 uppercase tracking-wide transition ${
                              salesSupplierFilterActive ||
                              salesSupplierMenu.open
                                ? 'text-primary'
                                : 'text-gray-500 hover:text-gray-800'
                            }`}
                            aria-expanded={salesSupplierMenu.open}
                            aria-haspopup="listbox"
                          >
                            <span>Пастаўшчык</span>
                            <span aria-hidden>
                              {salesSupplierMenu.open ? '▴' : '▾'}
                            </span>
                          </button>
                        </th>
                        <th className="w-[16%] px-4 py-3">Заказ Odoo</th>
                        <th className="w-[18%] px-4 py-3">Дата продажу</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-100 bg-white">
                      {filteredSales.length === 0 ? (
                        <tr>
                          <td
                            colSpan={6}
                            className="px-4 py-8 text-center text-sm text-gray-500"
                          >
                            Нічога не знойдзена.
                          </td>
                        </tr>
                      ) : (
                        filteredSales.map((row) => (
                          <tr key={row.id} className="align-top">
                            <td className="px-4 py-3 font-medium text-gray-900">
                              <span className="break-words [overflow-wrap:anywhere]">
                                {row.productName}
                              </span>
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums text-gray-700">
                              {row.quantity}
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-right tabular-nums text-gray-700">
                              {row.grossUnitCost != null
                                ? formatPrice(row.grossUnitCost)
                                : '—'}
                            </td>
                            <td className="px-4 py-3 text-gray-600">
                              <span className="break-words [overflow-wrap:anywhere]">
                                {row.supplierName?.trim() || '—'}
                              </span>
                            </td>
                            <td className="px-4 py-3 text-gray-600">
                              {row.odooPosOrderName?.trim() ||
                                `#${row.odooPosOrderId}`}
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-gray-500">
                              {formatDate(row.soldAtUtc)}
                            </td>
                          </tr>
                        ))
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {salesSupplierMenu.mounted &&
        salesSupplierMenu.open &&
        createPortal(
          <div
            ref={salesSupplierMenu.menuRef}
            className="fixed z-[70] rounded-lg border border-gray-200 bg-white p-3 shadow-lg"
            style={{
              top: `${salesSupplierMenu.position.top}px`,
              left: `${salesSupplierMenu.position.left}px`,
              width: `${salesSupplierMenu.menuWidth}px`,
            }}
            role="listbox"
            aria-label="Фільтр пастаўшчыкоў"
          >
            <div className="mb-2 flex items-center justify-between gap-2">
              <p className="text-xs font-semibold uppercase tracking-wide text-gray-500">
                Пастаўшчыкі
              </p>
              {salesSupplierFilterActive ? (
                <button
                  type="button"
                  onClick={() => setSelectedSaleSuppliers([])}
                  className="text-xs font-medium text-primary hover:underline"
                >
                  Скінуць
                </button>
              ) : null}
            </div>
            <div className="max-h-64 space-y-2 overflow-auto pr-1">
              {saleSupplierOptions.length === 0 ? (
                <p className="text-xs text-gray-500">Няма пастаўшчыкоў</p>
              ) : (
                saleSupplierOptions.map((supplier) => (
                  <label
                    key={supplier}
                    className="flex cursor-pointer items-center gap-2 text-sm font-normal normal-case text-gray-700"
                  >
                    <input
                      type="checkbox"
                      className="size-4 rounded border-gray-300 accent-primary focus:ring-primary"
                      checked={selectedSaleSuppliers.includes(supplier)}
                      onChange={() => toggleSaleSupplierFilter(supplier)}
                    />
                    <span className="truncate" title={supplierLabel(supplier)}>
                      {supplierLabel(supplier)}
                    </span>
                  </label>
                ))
              )}
            </div>
          </div>,
          document.body
        )}

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
        onClose={closeEdit}
        onSubmit={(quantity, grossUnitCost) => {
          void submitEdit(quantity, grossUnitCost);
        }}
      />

      <AcceptReceivedOfferModal
        open={Boolean(acceptOffer)}
        offer={acceptOffer}
        submitting={acceptSubmitting}
        error={acceptError}
        onClose={() => {
          if (acceptSubmitting) return;
          setAcceptOffer(null);
          setAcceptError(null);
        }}
        onSubmit={(input) => {
          void handleAcceptReceived(input);
        }}
      />

      <IssueBukinistkaInvoiceModal
        open={invoiceOpen}
        sales={filteredSales}
        onClose={() => setInvoiceOpen(false)}
        onIssued={() => {
          void reloadSales();
        }}
      />
    </div>
  );
}
