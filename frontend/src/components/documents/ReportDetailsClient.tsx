'use client';

import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { useTopbar } from '@/components/topbar/TopbarContext';
import {
  createVatReportRow,
  deleteVatReportRow,
  fetchVatReportDetails,
  fetchVatReportSourceOrders,
  regenerateVatReport,
  updateVatReportRow,
} from '@/lib/api/reports';
import type { VatReportDetails, VatReportSourceOrderOption } from '@/types/report-details';
import { FiRefreshCw } from 'react-icons/fi';
import { FiChevronDown } from 'react-icons/fi';
import { FiEdit2, FiPlus, FiPrinter, FiTrash2, FiX } from 'react-icons/fi';

function formatAmount(value: number): string {
  return value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function formatDate(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleDateString('ru-RU');
}

function normalizeOrderNumber(value: string): number {
  const digits = value.replace(/\D/g, '');
  if (!digits) return Number.MAX_SAFE_INTEGER;
  const n = Number(digits);
  return Number.isFinite(n) ? n : Number.MAX_SAFE_INTEGER;
}

function formatMonthYearBe(month: number, year: number): string {
  const months = [
    'Студзень',
    'Люты',
    'Сакавік',
    'Красавік',
    'Май',
    'Чэрвень',
    'Ліпень',
    'Жнівень',
    'Верасень',
    'Кастрычнік',
    'Лістапад',
    'Снежань',
  ];
  const name = month >= 1 && month <= 12 ? months[month - 1] : `Месяц ${month}`;
  return `${name} ${year}`;
}

function round2(value: number): number {
  return Math.round(value * 100) / 100;
}

function recalcVatAndNet(grossAmount: number, vatRatePercent: number): { vatAmount: number; netAmount: number } {
  const rate = vatRatePercent / 100;
  if (!Number.isFinite(grossAmount) || grossAmount <= 0 || !Number.isFinite(rate) || rate <= 0) {
    return { vatAmount: 0, netAmount: Math.max(0, round2(grossAmount || 0)) };
  }
  const vatAmount = round2((grossAmount * rate) / (1 + rate));
  const netAmount = round2(grossAmount - vatAmount);
  return { vatAmount, netAmount };
}

export default function ReportDetailsClient({ reportId }: { reportId: number }) {
  const { setTopbarButtons, setTopbarPage } = useTopbar();
  const [data, setData] = useState<VatReportDetails | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedOrderId, setExpandedOrderId] = useState<string | null>(null);
  const [showAuditDetails, setShowAuditDetails] = useState(false);
  const [regeneratingRowKey, setRegeneratingRowKey] = useState<string | null>(null);
  const [pendingRegenerateRowKey, setPendingRegenerateRowKey] = useState<string | null>(null);
  const [editingRowKey, setEditingRowKey] = useState<string | null>(null);
  const [deletingRowKey, setDeletingRowKey] = useState<string | null>(null);
  const [pendingDeleteRow, setPendingDeleteRow] = useState<{ rowId: number; rowKey: string } | null>(null);
  const [addModalOpen, setAddModalOpen] = useState(false);
  const [addMode, setAddMode] = useState<'select' | 'manual'>('select');
  const [sourceOrderOptions, setSourceOrderOptions] = useState<VatReportSourceOrderOption[]>([]);
  const [sourceOrdersLoading, setSourceOrdersLoading] = useState(false);
  const [selectedSourceKey, setSelectedSourceKey] = useState<string>('');
  const [addingRow, setAddingRow] = useState(false);
  const [addRowError, setAddRowError] = useState<string | null>(null);
  const [orderSearch, setOrderSearch] = useState('');
  const [vatFilterOpen, setVatFilterOpen] = useState(false);
  const [vatFilter5, setVatFilter5] = useState(true);
  const [vatFilter23, setVatFilter23] = useState(true);
  const [newRow, setNewRow] = useState({
    orderNumber: '',
    orderDateUtc: '',
    vatRatePercent: 23,
    grossAmount: 0,
    vatAmount: 0,
    netAmount: 0,
  });
  const detailsTableRef = useRef<HTMLTableElement | null>(null);
  const [editedRows, setEditedRows] = useState<
    Record<
      string,
      {
        orderDateUtc: string;
        vatRatePercent: number;
        grossAmount: number;
        vatAmount: number;
        netAmount: number;
      }
    >
  >({});

  useEffect(() => {
    const monthYearTitle = data ? formatMonthYearBe(data.periodMonth, data.periodYear) : 'Справаздача';
    setTopbarPage({ title: monthYearTitle });
    setTopbarButtons([]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [data, setTopbarButtons, setTopbarPage]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchVatReportDetails(reportId)
      .then((res) => {
        if (!cancelled) setData(res);
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err instanceof Error ? err.message : 'Памылка загрузкі справаздачы');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [reportId]);

  const expandedRow = useMemo(
    () => data?.rows.find((row) => row.shopifyOrderId === expandedOrderId) ?? null,
    [data, expandedOrderId]
  );

  const displayTotalVat = useMemo(() => {
    if (!data) return 0;
    if (!expandedRow) return data.vat;

    let delta = 0;
    expandedRow.polandRows.forEach((row) => {
      const rowKey = String(row.id);
      const edited = editedRows[rowKey];
      if (!edited) return;
      delta += edited.vatAmount - row.vatAmount;
    });
    return round2(data.vat + delta);
  }, [data, expandedRow, editedRows]);

  const visiblePolandRows = useMemo(() => {
    if (!expandedRow) return [];
    const search = orderSearch.trim().toLowerCase();
    const byVat = (rate: number) => (rate === 5 ? vatFilter5 : vatFilter23);

    return [...expandedRow.polandRows]
      .filter((row) => {
        if (!byVat(row.vatRatePercent)) return false;
        if (!search) return true;
        return row.orderNumber.toLowerCase().includes(search);
      })
      .sort((a, b) => {
        const aNum = normalizeOrderNumber(a.orderNumber);
        const bNum = normalizeOrderNumber(b.orderNumber);
        if (aNum !== bNum) return aNum - bNum;
        if (a.orderNumber !== b.orderNumber) return a.orderNumber.localeCompare(b.orderNumber, 'ru');
        return a.vatRatePercent - b.vatRatePercent;
      });
  }, [expandedRow, orderSearch, vatFilter5, vatFilter23]);

  const isVatFilterCustomized = !(vatFilter5 && vatFilter23);

  const handleRegenerate = async (rowKey: string) => {
    setRegeneratingRowKey(rowKey);
    setError(null);
    try {
      const updated = await regenerateVatReport(reportId);
      const refreshed = await fetchVatReportDetails(updated.id);
      setData(refreshed);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка перегенерацыі справаздачы');
    } finally {
      setRegeneratingRowKey(null);
      setPendingRegenerateRowKey(null);
    }
  };

  const toDateInputValue = (iso: string) => {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return '';
    const y = date.getFullYear();
    const m = String(date.getMonth() + 1).padStart(2, '0');
    const d = String(date.getDate()).padStart(2, '0');
    return `${y}-${m}-${d}`;
  };

  const startEditRow = (
    rowKey: string,
    row: {
      orderDateUtc: string;
      vatRatePercent: number;
      grossAmount: number;
      vatAmount: number;
      netAmount: number;
    }
  ) => {
    setEditedRows((prev) => ({
      ...prev,
      [rowKey]: {
        orderDateUtc: toDateInputValue(row.orderDateUtc),
        vatRatePercent: row.vatRatePercent,
        grossAmount: row.grossAmount,
        vatAmount: row.vatAmount,
        netAmount: row.netAmount,
      },
    }));
    setEditingRowKey(rowKey);
  };

  const toSourceKey = (option: VatReportSourceOrderOption) =>
    `${option.shopifyOrderId}|${option.vatRatePercent}|${option.orderNumber}`;

  const resetNewRow = () => {
    setNewRow({
      orderNumber: '',
      orderDateUtc: '',
      vatRatePercent: 23,
      grossAmount: 0,
      vatAmount: 0,
      netAmount: 0,
    });
    setSelectedSourceKey('');
    setAddRowError(null);
  };

  const openAddModal = async () => {
    setAddModalOpen(true);
    setAddMode('select');
    resetNewRow();
    setSourceOrdersLoading(true);
    try {
      const options = await fetchVatReportSourceOrders(reportId);
      setSourceOrderOptions(options);
    } catch (err: unknown) {
      setAddRowError(err instanceof Error ? err.message : 'Памылка загрузкі спісу замоў');
      setSourceOrderOptions([]);
    } finally {
      setSourceOrdersLoading(false);
    }
  };

  const submitAddRow = async () => {
    setAddRowError(null);
    const payload = {
      orderNumber: newRow.orderNumber.trim(),
      orderDateUtc: newRow.orderDateUtc.trim(),
      vatRatePercent: Number(newRow.vatRatePercent) || 0,
      grossAmount: Number(newRow.grossAmount) || 0,
      vatAmount: Number(newRow.vatAmount) || 0,
      netAmount: Number(newRow.netAmount) || 0,
    };
    if (!payload.orderNumber) {
      setAddRowError('Нумар замовы абавязковы.');
      return;
    }
    if (!payload.orderDateUtc) {
      setAddRowError('Дата замовы абавязковая.');
      return;
    }
    if (payload.vatRatePercent !== 5 && payload.vatRatePercent !== 23) {
      setAddRowError('Стаўка VAT павінна быць 5 або 23.');
      return;
    }

    setAddingRow(true);
    try {
      await createVatReportRow(reportId, payload);
      const refreshed = await fetchVatReportDetails(reportId);
      setData(refreshed);
      setAddModalOpen(false);
      resetNewRow();
    } catch (err: unknown) {
      setAddRowError(err instanceof Error ? err.message : 'Памылка дадання радка справаздачы');
    } finally {
      setAddingRow(false);
    }
  };

  const handleExportTableToPdf = () => {
    const table = detailsTableRef.current;
    if (!table) return;

    const printableTable = table.cloneNode(true) as HTMLTableElement;
    // Remove action column cells and keep only data columns.
    printableTable.querySelectorAll('tr').forEach((row) => {
      const cells = Array.from(row.querySelectorAll('th,td'));
      if (cells.length >= 7) {
        cells[cells.length - 1]?.remove();
      }
    });
    // Remove VAT filter control from export header.
    printableTable.querySelectorAll('button[aria-label="Фільтр па стаўцы VAT"]').forEach((el) => el.remove());

    const totals = visiblePolandRows.reduce(
      (acc, row) => ({
        grossAmount: acc.grossAmount + row.grossAmount,
        vatAmount: acc.vatAmount + row.vatAmount,
        netAmount: acc.netAmount + row.netAmount,
      }),
      { grossAmount: 0, vatAmount: 0, netAmount: 0 }
    );

    const body = printableTable.tBodies[0] ?? printableTable.createTBody();
    const totalRow = body.insertRow();
    totalRow.className = 'export-total-row';
    const labelCell = totalRow.insertCell();
    labelCell.colSpan = 3;
    labelCell.textContent = 'Усяго';
    labelCell.style.fontWeight = '700';
    const grossCell = totalRow.insertCell();
    grossCell.textContent = formatAmount(totals.grossAmount);
    grossCell.style.textAlign = 'right';
    grossCell.style.fontWeight = '700';
    const vatCell = totalRow.insertCell();
    vatCell.textContent = formatAmount(totals.vatAmount);
    vatCell.style.textAlign = 'right';
    vatCell.style.fontWeight = '700';
    const netCell = totalRow.insertCell();
    netCell.textContent = formatAmount(totals.netAmount);
    netCell.style.textAlign = 'right';
    netCell.style.fontWeight = '700';
    const html = `<!doctype html>
<html>
  <head>
    <meta charset="utf-8" />
    <title>Польшча</title>
    <style>
      @page { size: A4 portrait; margin: 12mm; }
      body { font-family: Arial, sans-serif; margin: 0; color: #111827; }
      .wrap { width: 100%; }
      table { width: 100%; border-collapse: collapse; font-size: 12px; }
      th, td { border: 1px solid #d1d5db; padding: 6px 8px; text-align: left; vertical-align: top; }
      th { background: #f9fafb; font-weight: 700; }
      .export-total-row td { font-weight: 700; }
      .export-total-row { break-inside: avoid; page-break-inside: avoid; }
    </style>
  </head>
  <body>
    <div class="wrap">${printableTable.outerHTML}</div>
  </body>
</html>`;

    const iframe = document.createElement('iframe');
    iframe.style.position = 'fixed';
    iframe.style.right = '0';
    iframe.style.bottom = '0';
    iframe.style.width = '0';
    iframe.style.height = '0';
    iframe.style.border = '0';
    iframe.setAttribute('aria-hidden', 'true');
    document.body.appendChild(iframe);

    const iframeDoc = iframe.contentDocument ?? iframe.contentWindow?.document;
    if (!iframeDoc) {
      iframe.remove();
      return;
    }

    iframeDoc.open();
    iframeDoc.write(html);
    iframeDoc.close();

    let printed = false;
    const printFromIframe = () => {
      if (printed) return;
      printed = true;
      const iframeWindow = iframe.contentWindow;
      if (!iframeWindow) {
        iframe.remove();
        return;
      }
      iframeWindow.focus();
      iframeWindow.print();
      window.setTimeout(() => {
        iframe.remove();
      }, 500);
    };

    // Fallback timeout is needed because some browsers don't fire iframe onload reliably for document.write.
    iframe.onload = printFromIframe;
    window.setTimeout(printFromIframe, 250);
  };

  const confirmDeleteRow = async () => {
    if (!pendingDeleteRow) return;
    const { rowId, rowKey } = pendingDeleteRow;
    setDeletingRowKey(rowKey);
    setError(null);
    try {
      await deleteVatReportRow(rowId);
      const refreshed = await fetchVatReportDetails(reportId);
      setData(refreshed);
      if (editingRowKey === rowKey) setEditingRowKey(null);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка выдалення радка справаздачы');
    } finally {
      setDeletingRowKey(null);
      setPendingDeleteRow(null);
    }
  };

  useEffect(() => {
    if (!pendingDeleteRow && !pendingRegenerateRowKey && !addModalOpen) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      if (deletingRowKey || regeneratingRowKey || addingRow) return;
      setPendingDeleteRow(null);
      setPendingRegenerateRowKey(null);
      setAddModalOpen(false);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [pendingDeleteRow, pendingRegenerateRowKey, addModalOpen, deletingRowKey, regeneratingRowKey, addingRow]);

  useEffect(() => {
    if (!addModalOpen || addMode !== 'select' || !selectedSourceKey) return;
    const option = sourceOrderOptions.find((item) => toSourceKey(item) === selectedSourceKey);
    if (!option) return;
    setNewRow({
      orderNumber: option.orderNumber,
      orderDateUtc: toDateInputValue(option.orderDateUtc),
      vatRatePercent: option.vatRatePercent,
      grossAmount: option.grossAmount,
      vatAmount: option.vatAmount,
      netAmount: option.netAmount,
    });
  }, [addModalOpen, addMode, selectedSourceKey, sourceOrderOptions]);

  useEffect(() => {
    if (!vatFilterOpen) return;
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as HTMLElement | null;
      if (target?.closest('[data-vat-filter-container="true"]')) return;
      setVatFilterOpen(false);
    };
    window.addEventListener('mousedown', onPointerDown);
    return () => {
      window.removeEventListener('mousedown', onPointerDown);
    };
  }, [vatFilterOpen]);

  if (loading) return <LoadingSpinner label="Загрузка справаздачы..." />;
  if (error) {
    return (
      <div className="mx-auto w-full max-w-6xl rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
        {error}
      </div>
    );
  }
  if (!data) return null;

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 px-6 py-4 text-sm text-gray-600">
          Усяго VAT: <span className="font-semibold text-gray-900">{formatAmount(displayTotalVat)}</span>
        </div>
        <div className="overflow-x-auto">
          <table className="min-w-full border-collapse text-left text-sm">
            <thead>
              <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                <th className="px-4 py-2.5">Тып</th>
                <th className="px-4 py-2.5">Назва</th>
                <th className="px-4 py-2.5 text-right">VAT</th>
                <th className="px-4 py-2.5 text-right">Дзеянне</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {data.rows.map((row) => (
                <tr
                  key={`${row.type}-${row.shopifyOrderId}`}
                  className={`transition ${row.type === 'poland' ? 'cursor-pointer hover:bg-primary/10' : ''}`}
                  onClick={() => {
                    if (row.type !== 'poland') return;
                    setExpandedOrderId((prev) => (prev === row.shopifyOrderId ? null : row.shopifyOrderId));
                  }}
                >
                  <td className="px-4 py-3">{row.type === 'poland' ? 'Польшча' : 'Не Польшча'}</td>
                  <td className="px-4 py-3">{row.type === 'poland' ? 'Польшча' : row.name}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.vat)}</td>
                  <td className="px-4 py-3 text-right">
                    <button
                      type="button"
                      onClick={(e) => {
                        e.stopPropagation();
                        setPendingRegenerateRowKey(`${row.type}-${row.shopifyOrderId}`);
                      }}
                      disabled={regeneratingRowKey === `${row.type}-${row.shopifyOrderId}`}
                      className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-700 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:opacity-60"
                      aria-label="Перегенераваць справаздачу"
                      title="Перегенераваць справаздачу"
                    >
                      {regeneratingRowKey === `${row.type}-${row.shopifyOrderId}` ? (
                        <span className="size-3.5 animate-spin rounded-full border-2 border-gray-300 border-t-gray-700" />
                      ) : (
                        <FiRefreshCw className="size-4" aria-hidden />
                      )}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {expandedRow && (
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="flex items-center justify-between border-b border-gray-100 px-6 py-4">
            <h3 className="text-sm font-semibold text-gray-900">Дэталі па Польшчы</h3>
            <div className="flex flex-wrap items-end gap-2">
              <label className="w-full max-w-[11.5rem] space-y-1">
                <span className="text-xs font-medium uppercase tracking-wide text-gray-500">Пошук</span>
                <div className="flex items-center gap-2">
                  <input
                    type="text"
                    value={orderSearch}
                    onChange={(e) => setOrderSearch(e.currentTarget.value)}
                    placeholder="Нумар замовы"
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2.5 text-sm text-gray-800 transition placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                  <button
                    type="button"
                    onClick={() => {
                      setOrderSearch('');
                      setVatFilter5(true);
                      setVatFilter23(true);
                    }}
                    className="inline-flex size-9 shrink-0 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-500 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                    aria-label="Скінуць фільтры"
                    title="Скінуць фільтры"
                  >
                    <FiX className="size-4" aria-hidden />
                  </button>
                </div>
              </label>
              <button
                type="button"
                onClick={openAddModal}
                className="inline-flex items-center gap-2 rounded-lg border border-primary bg-primary px-3 py-1.5 text-sm font-medium text-white shadow-sm transition hover:bg-primary/90 active:scale-[0.99]"
              >
                <FiPlus className="size-4" aria-hidden />
                Дадаць радок
              </button>
              <button
                type="button"
                onClick={() => setShowAuditDetails((prev) => !prev)}
                className="rounded-lg border border-primary bg-primary px-3 py-1.5 text-sm font-medium text-white shadow-sm transition hover:bg-primary/90 active:scale-[0.99]"
              >
                {showAuditDetails ? 'Схаваць дэталізацыю' : 'Дэталізацыя'}
              </button>
              <button
                type="button"
                onClick={handleExportTableToPdf}
                className="inline-flex size-9 items-center justify-center rounded-lg border border-primary bg-primary text-white shadow-sm transition hover:bg-primary/90 active:scale-[0.99]"
                aria-label="Экспарт у PDF"
                title="Экспарт у PDF"
              >
                <FiPrinter className="size-4" aria-hidden />
              </button>
            </div>
          </div>
          <div className="overflow-x-auto">
            <table ref={detailsTableRef} className="min-w-full border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="px-4 py-2.5">Нумар замовы</th>
                  <th className="px-4 py-2.5">Дата</th>
                  <th className="relative px-4 py-2.5 text-right">
                    <div
                      className="flex items-center justify-end gap-2"
                      data-vat-filter-container="true"
                    >
                      <span>Стаўка VAT</span>
                      <button
                        type="button"
                        onClick={() => setVatFilterOpen((prev) => !prev)}
                        className={`relative inline-flex items-center justify-center rounded-md border bg-white p-1 transition ${
                          isVatFilterCustomized
                            ? 'border-primary/50 text-primary'
                            : 'border-gray-200 text-gray-600 hover:border-primary/40 hover:bg-primary/10 hover:text-primary'
                        }`}
                        aria-label="Фільтр па стаўцы VAT"
                        title="Фільтр па стаўцы VAT"
                      >
                        <FiChevronDown className="size-3.5" aria-hidden />
                        {isVatFilterCustomized && (
                          <span className="absolute -right-0.5 -top-0.5 size-1.5 rounded-full bg-primary" />
                        )}
                      </button>
                      {vatFilterOpen && (
                        <div className="absolute right-0 top-full z-20 mt-1.5 w-36 rounded-lg border border-gray-200 bg-white p-2 text-left shadow-lg">
                          <label className="flex items-center gap-2 rounded-md px-2 py-1.5 text-xs font-medium normal-case tracking-normal text-gray-700 hover:bg-gray-50">
                            <input
                              type="checkbox"
                              checked={vatFilter5}
                              onChange={(e) => setVatFilter5(e.currentTarget.checked)}
                              className="size-3.5 rounded border-gray-300 accent-primary"
                            />
                            5%
                          </label>
                          <label className="mt-1 flex items-center gap-2 rounded-md px-2 py-1.5 text-xs font-medium normal-case tracking-normal text-gray-700 hover:bg-gray-50">
                            <input
                              type="checkbox"
                              checked={vatFilter23}
                              onChange={(e) => setVatFilter23(e.currentTarget.checked)}
                              className="size-3.5 rounded border-gray-300 accent-primary"
                            />
                            23%
                          </label>
                        </div>
                      )}
                    </div>
                  </th>
                  <th className="px-4 py-2.5 text-right">Сума брута</th>
                  <th className="px-4 py-2.5 text-right">VAT</th>
                  <th className="px-4 py-2.5 text-right">Сума нета</th>
                  <th className="px-4 py-2.5 text-right">Дзеянне</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {visiblePolandRows.map((row) => (
                  <Fragment key={row.id}>
                    {(() => {
                      const rowKey = String(row.id);
                      const isEditing = editingRowKey === rowKey;
                      const edited = editedRows[rowKey];
                      return (
                    <tr>
                      <td className="px-4 py-3">{row.orderNumber}</td>
                      <td className="px-4 py-3">
                        {formatDate(row.orderDateUtc)}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums">
                        {isEditing ? (
                          <select
                            value={edited?.vatRatePercent ?? row.vatRatePercent}
                            onChange={(e) => {
                              const value = Number(e.currentTarget.value) || 0;
                              setEditedRows((prev) => ({
                                ...(() => {
                                  const base = prev[rowKey] ?? {
                                    orderDateUtc: toDateInputValue(row.orderDateUtc),
                                    vatRatePercent: row.vatRatePercent,
                                    grossAmount: row.grossAmount,
                                    vatAmount: row.vatAmount,
                                    netAmount: row.netAmount,
                                  };
                                  const recalculated = recalcVatAndNet(base.grossAmount, value);
                                  return {
                                    ...prev,
                                    [rowKey]: {
                                      ...base,
                                      vatRatePercent: value,
                                      vatAmount: recalculated.vatAmount,
                                      netAmount: recalculated.netAmount,
                                    },
                                  };
                                })(),
                              }));
                            }}
                            className="w-24 rounded-md border border-gray-200 px-2 py-1 text-right text-sm"
                          >
                            <option value={5}>5</option>
                            <option value={23}>23</option>
                          </select>
                        ) : (
                          `${formatAmount(row.vatRatePercent)}%`
                        )}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums">
                        {isEditing ? (
                          <input
                            type="number"
                            step="0.01"
                            value={edited?.grossAmount ?? row.grossAmount}
                            onChange={(e) => {
                              const value = Number(e.currentTarget.value) || 0;
                              setEditedRows((prev) => ({
                                ...(() => {
                                  const base = prev[rowKey] ?? {
                                    orderDateUtc: toDateInputValue(row.orderDateUtc),
                                    vatRatePercent: row.vatRatePercent,
                                    grossAmount: row.grossAmount,
                                    vatAmount: row.vatAmount,
                                    netAmount: row.netAmount,
                                  };
                                  const recalculated = recalcVatAndNet(value, base.vatRatePercent);
                                  return {
                                    ...prev,
                                    [rowKey]: {
                                      ...base,
                                      grossAmount: value,
                                      vatAmount: recalculated.vatAmount,
                                      netAmount: recalculated.netAmount,
                                    },
                                  };
                                })(),
                              }));
                            }}
                            className="w-28 rounded-md border border-gray-200 px-2 py-1 text-right text-sm"
                          />
                        ) : (
                          formatAmount(row.grossAmount)
                        )}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums">
                        {isEditing ? (
                          <input
                            type="number"
                            step="0.01"
                            value={edited?.vatAmount ?? row.vatAmount}
                            onChange={(e) => {
                              const value = Number(e.currentTarget.value) || 0;
                              setEditedRows((prev) => ({
                                ...prev,
                                [rowKey]: {
                                  ...(prev[rowKey] ?? {
                                    orderDateUtc: toDateInputValue(row.orderDateUtc),
                                    vatRatePercent: row.vatRatePercent,
                                    grossAmount: row.grossAmount,
                                    vatAmount: row.vatAmount,
                                    netAmount: row.netAmount,
                                  }),
                                  vatAmount: value,
                                },
                              }));
                            }}
                            className="w-28 rounded-md border border-gray-200 px-2 py-1 text-right text-sm"
                          />
                        ) : (
                          formatAmount(row.vatAmount)
                        )}
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums">
                        {isEditing ? (
                          <input
                            type="number"
                            step="0.01"
                            value={edited?.netAmount ?? row.netAmount}
                            onChange={(e) => {
                              const value = Number(e.currentTarget.value) || 0;
                              setEditedRows((prev) => ({
                                ...prev,
                                [rowKey]: {
                                  ...(prev[rowKey] ?? {
                                    orderDateUtc: toDateInputValue(row.orderDateUtc),
                                    vatRatePercent: row.vatRatePercent,
                                    grossAmount: row.grossAmount,
                                    vatAmount: row.vatAmount,
                                    netAmount: row.netAmount,
                                  }),
                                  netAmount: value,
                                },
                              }));
                            }}
                            className="w-28 rounded-md border border-gray-200 px-2 py-1 text-right text-sm"
                          />
                        ) : (
                          formatAmount(row.netAmount)
                        )}
                      </td>
                      <td className="px-4 py-3 text-right">
                        <div className="inline-flex items-center gap-2">
                          <button
                            type="button"
                            onClick={async () => {
                              if (isEditing) {
                                const edited = editedRows[rowKey];
                                if (edited) {
                                  try {
                                    await updateVatReportRow({
                                      rowId: row.id,
                                      vatRatePercent: edited.vatRatePercent,
                                      grossAmount: edited.grossAmount,
                                      vatAmount: edited.vatAmount,
                                      netAmount: edited.netAmount,
                                    });
                                    const refreshed = await fetchVatReportDetails(reportId);
                                    setData(refreshed);
                                  } catch (err: unknown) {
                                    setError(
                                      err instanceof Error ? err.message : 'Памылка захавання радка справаздачы'
                                    );
                                    return;
                                  }
                                }
                                setEditingRowKey(null);
                              } else {
                                startEditRow(rowKey, {
                                  orderDateUtc: row.orderDateUtc,
                                  vatRatePercent: row.vatRatePercent,
                                  grossAmount: row.grossAmount,
                                  vatAmount: row.vatAmount,
                                  netAmount: row.netAmount,
                                });
                              }
                            }}
                            className={`inline-flex size-8 items-center justify-center rounded-full border text-gray-700 shadow-sm transition ${
                              isEditing
                                ? 'border-primary bg-primary text-white hover:bg-primary/90'
                                : 'border-gray-200 bg-white hover:border-primary/40 hover:bg-primary/15 hover:text-primary'
                            }`}
                            aria-label={isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                            title={isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                          >
                            <FiEdit2 className="size-4" aria-hidden />
                          </button>
                          <button
                            type="button"
                            onClick={() => setPendingDeleteRow({ rowId: row.id, rowKey })}
                            disabled={deletingRowKey === rowKey}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:opacity-60"
                            aria-label="Выдаліць радок"
                            title="Выдаліць радок"
                          >
                            {deletingRowKey === rowKey ? (
                              <span className="size-3.5 animate-spin rounded-full border-2 border-red-300 border-t-red-600" />
                            ) : (
                              <FiTrash2 className="size-4" aria-hidden />
                            )}
                          </button>
                        </div>
                      </td>
                    </tr>
                      );
                    })()}
                    {showAuditDetails && row.items.length > 0 && (
                      <tr className="bg-gray-50/50">
                        <td className="px-4 py-2 text-xs text-gray-500" colSpan={7}>
                          {row.items.map((item, itemIdx) => (
                            <div key={`${item.productTitle}-${itemIdx}`} className="py-0.5">
                              {item.productTitle} · qty {item.quantity} · type: {item.productType || '—'} · VAT{' '}
                              {formatAmount(item.assignedVatRatePercent)}% · reason: {item.assignmentReason}
                            </div>
                          ))}
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
                {visiblePolandRows.length === 0 && (
                  <tr>
                    <td colSpan={7} className="px-4 py-6 text-center text-sm text-gray-500">
                      Няма радкоў па выбраных фільтрах.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {addModalOpen && (
        <div
          className="fixed inset-0 z-[80] flex items-center justify-center bg-black/40 p-4"
          onClick={() => {
            if (addingRow) return;
            setAddModalOpen(false);
          }}
        >
          <div
            className="w-full max-w-2xl rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="flex items-center justify-between gap-3">
              <div className="text-base font-semibold text-gray-900">Дадаць радок справаздачы</div>
              <div className="inline-flex rounded-lg border border-gray-200 p-1 text-sm">
                <button
                  type="button"
                  onClick={() => {
                    setAddMode('select');
                    setAddRowError(null);
                  }}
                  className={`rounded-md px-3 py-1 ${addMode === 'select' ? 'bg-primary text-white' : 'text-gray-700 hover:bg-gray-50'}`}
                >
                  Выбраць заказ
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setAddMode('manual');
                    setSelectedSourceKey('');
                    setAddRowError(null);
                  }}
                  className={`rounded-md px-3 py-1 ${addMode === 'manual' ? 'bg-primary text-white' : 'text-gray-700 hover:bg-gray-50'}`}
                >
                  Увесці ўручную
                </button>
              </div>
            </div>

            <div className="mt-4 space-y-3">
              {addRowError && (
                <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">{addRowError}</div>
              )}

              {addMode === 'select' && (
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Нумар замовы (за гэты месяц)</span>
                  <select
                    value={selectedSourceKey}
                    onChange={(e) => setSelectedSourceKey(e.currentTarget.value)}
                    disabled={sourceOrdersLoading || addingRow}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  >
                    <option value="">Выберыце заказ</option>
                    {sourceOrderOptions.map((option) => (
                      <option key={toSourceKey(option)} value={toSourceKey(option)}>
                        {option.orderNumber} · {formatDate(option.orderDateUtc)} · VAT {formatAmount(option.vatRatePercent)}%
                      </option>
                    ))}
                  </select>
                </label>
              )}

              <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Нумар замовы</span>
                  <input
                    type="text"
                    value={newRow.orderNumber}
                    onChange={(e) => setNewRow((prev) => ({ ...prev, orderNumber: e.currentTarget.value }))}
                    disabled={addingRow}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Дата замовы</span>
                  <input
                    type="date"
                    value={newRow.orderDateUtc}
                    onChange={(e) => setNewRow((prev) => ({ ...prev, orderDateUtc: e.currentTarget.value }))}
                    disabled={addingRow}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Стаўка VAT</span>
                  <select
                    value={newRow.vatRatePercent}
                    onChange={(e) => {
                      const vatRatePercent = Number(e.currentTarget.value) || 0;
                      setNewRow((prev) => {
                        const recalculated = recalcVatAndNet(prev.grossAmount, vatRatePercent);
                        return { ...prev, vatRatePercent, vatAmount: recalculated.vatAmount, netAmount: recalculated.netAmount };
                      });
                    }}
                    disabled={addingRow}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  >
                    <option value={5}>5</option>
                    <option value={23}>23</option>
                  </select>
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Сума брута</span>
                  <input
                    type="number"
                    step="0.01"
                    value={newRow.grossAmount}
                    onChange={(e) => {
                      const grossAmount = Number(e.currentTarget.value) || 0;
                      setNewRow((prev) => {
                        const recalculated = recalcVatAndNet(grossAmount, prev.vatRatePercent);
                        return { ...prev, grossAmount, vatAmount: recalculated.vatAmount, netAmount: recalculated.netAmount };
                      });
                    }}
                    disabled={addingRow}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">VAT</span>
                  <input
                    type="number"
                    step="0.01"
                    value={newRow.vatAmount}
                    onChange={(e) => setNewRow((prev) => ({ ...prev, vatAmount: Number(e.currentTarget.value) || 0 }))}
                    disabled={addingRow}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Сума нета</span>
                  <input
                    type="number"
                    step="0.01"
                    value={newRow.netAmount}
                    onChange={(e) => setNewRow((prev) => ({ ...prev, netAmount: Number(e.currentTarget.value) || 0 }))}
                    disabled={addingRow}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
              </div>
            </div>

            <div className="mt-5 flex items-center justify-end gap-2">
              <button
                type="button"
                onClick={() => setAddModalOpen(false)}
                disabled={addingRow}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-60"
              >
                Адмена
              </button>
              <button
                type="button"
                onClick={submitAddRow}
                disabled={addingRow || (addMode === 'select' && !selectedSourceKey) || sourceOrdersLoading}
                className="inline-flex min-w-24 items-center justify-center rounded-lg border border-primary bg-primary px-3 py-1.5 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-60"
              >
                {addingRow ? (
                  <span className="size-4 animate-spin rounded-full border-2 border-primary/20 border-t-white" />
                ) : (
                  'Дадаць'
                )}
              </button>
            </div>
          </div>
        </div>
      )}

      {pendingDeleteRow && (
        <div
          className="fixed inset-0 z-[80] flex items-center justify-center bg-black/40 p-4"
          onClick={() => {
            if (deletingRowKey) return;
            setPendingDeleteRow(null);
          }}
        >
          <div
            className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="text-base font-semibold text-gray-900">Пацвердзіце выдаленне</div>
            <p className="mt-2 text-sm text-gray-600">Вы сапраўды хочаце выдаліць гэты радок справаздачы?</p>
            <div className="mt-5 flex items-center justify-end gap-2">
              <button
                type="button"
                onClick={() => setPendingDeleteRow(null)}
                disabled={!!deletingRowKey}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-60"
              >
                Адмена
              </button>
              <button
                type="button"
                onClick={confirmDeleteRow}
                disabled={!!deletingRowKey}
                className="inline-flex min-w-24 items-center justify-center rounded-lg border border-red-600 bg-red-600 px-3 py-1.5 text-sm font-medium text-white transition hover:bg-red-700 disabled:opacity-60"
              >
                {deletingRowKey ? (
                  <span className="size-4 animate-spin rounded-full border-2 border-red-200 border-t-white" />
                ) : (
                  'Выдаліць'
                )}
              </button>
            </div>
          </div>
        </div>
      )}

      {pendingRegenerateRowKey && (
        <div
          className="fixed inset-0 z-[80] flex items-center justify-center bg-black/40 p-4"
          onClick={() => {
            if (regeneratingRowKey) return;
            setPendingRegenerateRowKey(null);
          }}
        >
          <div
            className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="text-base font-semibold text-gray-900">Пацвердзіце перегенерацыю</div>
            <p className="mt-2 text-sm text-gray-600">Вы сапраўды хочаце перегенераваць справаздачу?</p>
            <div className="mt-5 flex items-center justify-end gap-2">
              <button
                type="button"
                onClick={() => setPendingRegenerateRowKey(null)}
                disabled={!!regeneratingRowKey}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-60"
              >
                Адмена
              </button>
              <button
                type="button"
                onClick={() => handleRegenerate(pendingRegenerateRowKey)}
                disabled={!!regeneratingRowKey}
                className="inline-flex min-w-24 items-center justify-center rounded-lg border border-primary bg-primary px-3 py-1.5 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-60"
              >
                {regeneratingRowKey ? (
                  <span className="size-4 animate-spin rounded-full border-2 border-primary/20 border-t-white" />
                ) : (
                  'Перагенераваць'
                )}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
