'use client';

import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import { useTopbar } from '@/components/topbar/TopbarContext';
import {
  createVatReportRow,
  deleteVatReportRow,
  downloadVatReportRowInvoice,
  fetchVatReportDetails,
  fetchVatReports,
  fetchVatReportSourceOrders,
  regenerateVatReport,
  moveVatReportRowToForeign,
  uploadVatReportRowInvoice,
  updateVatReportRow,
  updateVatReportRowItemVat,
} from '@/lib/api/reports';
import { fetchInvoiceSettings } from '@/lib/api/settings';
import type { VatReportDetails, VatReportSourceOrderOption } from '@/types/report-details';
import { FiRefreshCw } from 'react-icons/fi';
import { FiChevronDown } from 'react-icons/fi';
import { FiCornerUpRight, FiDownload, FiEdit2, FiEye, FiPlus, FiTrash2, FiUpload, FiX } from 'react-icons/fi';

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

function escapeXml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
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
  const [foreignOrderRows, setForeignOrderRows] = useState<VatReportDetails['rows']>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedOrderId, setExpandedOrderId] = useState<string | null>(null);
  const [expandedForeignOrderId, setExpandedForeignOrderId] = useState<string | null>(null);
  const [expandedPolandRowId, setExpandedPolandRowId] = useState<number | null>(null);
  const [regeneratingRowKey, setRegeneratingRowKey] = useState<string | null>(null);
  const [pendingRegenerateRowKey, setPendingRegenerateRowKey] = useState<string | null>(null);
  const [editingRowKey, setEditingRowKey] = useState<string | null>(null);
  const [deletingRowKey, setDeletingRowKey] = useState<string | null>(null);
  const [updatingItemVatId, setUpdatingItemVatId] = useState<number | null>(null);
  const [updatingShippingVatRowId, setUpdatingShippingVatRowId] = useState<number | null>(null);
  const [pendingDeleteRow, setPendingDeleteRow] = useState<{ rowId: number; rowKey: string } | null>(null);
  const [pendingMoveToForeignRow, setPendingMoveToForeignRow] = useState<{ rowId: number; rowKey: string } | null>(
    null
  );
  const [moveToForeignName, setMoveToForeignName] = useState('');
  const [moveToForeignAddress, setMoveToForeignAddress] = useState('');
  const [movingToForeignRowKey, setMovingToForeignRowKey] = useState<string | null>(null);
  const [addModalOpen, setAddModalOpen] = useState(false);
  const [addMode, setAddMode] = useState<'select' | 'manual'>('select');
  const [sourceOrderOptions, setSourceOrderOptions] = useState<VatReportSourceOrderOption[]>([]);
  const [sourceOrdersLoading, setSourceOrdersLoading] = useState(false);
  const [selectedSourceKey, setSelectedSourceKey] = useState<string>('');
  const [addingRow, setAddingRow] = useState(false);
  const [addRowError, setAddRowError] = useState<string | null>(null);
  const [orderSearch, setOrderSearch] = useState('');
  const [foreignOrderSearch, setForeignOrderSearch] = useState('');
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
        shippingGrossAmount?: number;
        vatManualOverride?: boolean;
      }
    >
  >({});

  const loadCombinedDetails = async (
    baseReportId: number
  ): Promise<{
    details: VatReportDetails;
    foreignRows: VatReportDetails['rows'];
  }> => {
    const res = await fetchVatReportDetails(baseReportId);
    const siblingType = res.rows.some((r) => r.type === 'poland') ? 'foreign' : 'poland';
    const baseType = res.rows.some((r) => r.type === 'poland') ? 'poland' : 'foreign';
    try {
      const allReports = await fetchVatReports();
      const sibling = allReports.find(
        (r) => r.periodYear === res.periodYear && r.periodMonth === res.periodMonth && r.type === siblingType
      );
      if (!sibling) {
        return {
          details: res,
          foreignRows: res.rows.filter((r) => r.type === 'foreign'),
        };
      }
      const siblingDetails = await fetchVatReportDetails(sibling.id);
      const polandDetails = baseType === 'poland' ? res : siblingDetails;
      const foreignDetails = baseType === 'foreign' ? res : siblingDetails;
      const primaryRows = polandDetails.rows.filter((r) => r.type === 'poland');
      const foreignRows = foreignDetails.rows.filter((r) => r.type === 'foreign');
      const foreignSummaryVat = foreignRows.reduce((sum, row) => sum + row.vat, 0);
      const foreignSummaryNet = foreignRows.reduce((sum, row) => sum + (row.netAmount ?? 0), 0);
      const foreignSummaryGross = foreignRows.reduce((sum, row) => sum + (row.grossAmount ?? 0), 0);
      return {
        foreignRows,
        details: {
          ...polandDetails,
          vat: round2(polandDetails.vat + foreignDetails.vat),
          rows: [
            ...primaryRows,
            {
              type: 'foreign',
              name: 'Замежжа',
              shopifyOrderId: 'foreign-summary',
              vat: round2(foreignSummaryVat),
              netAmount: round2(foreignSummaryNet),
              grossAmount: round2(foreignSummaryGross),
              polandRows: [],
            },
          ],
        },
      };
    } catch {
      return {
        details: res,
        foreignRows: res.rows.filter((r) => r.type === 'foreign'),
      };
    }
  };

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
    setExpandedOrderId(null);
    setExpandedForeignOrderId(null);
    loadCombinedDetails(reportId)
      .then(({ details, foreignRows }) => {
        if (cancelled) return;
        setForeignOrderRows(foreignRows);
        setData(details);
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
  const isForeignReportOnly = useMemo(
    () =>
      (data?.rows.length ?? 0) > 0 &&
      (data?.rows.every((row) => row.type === 'foreign') ?? false),
    [data]
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

  const visibleForeignRows = useMemo(() => {
    const search = foreignOrderSearch.trim().toLowerCase();
    return [...foreignOrderRows]
      .filter((row) => (search ? row.name.toLowerCase().includes(search) : true))
      .sort((a, b) => {
        const aNum = normalizeOrderNumber(a.name);
        const bNum = normalizeOrderNumber(b.name);
        if (aNum !== bNum) return aNum - bNum;
        return a.name.localeCompare(b.name, 'ru');
      });
  }, [foreignOrderRows, foreignOrderSearch]);

  const handleRegenerate = async (rowKey: string) => {
    setRegeneratingRowKey(rowKey);
    setError(null);
    try {
      const targetType = rowKey.startsWith('foreign-') ? 'foreign' : 'poland';
      let targetReportId = reportId;
      if (data) {
        const allReports = await fetchVatReports();
        const match = allReports.find(
          (r) => r.periodYear === data.periodYear && r.periodMonth === data.periodMonth && r.type === targetType
        );
        if (match) {
          targetReportId = match.id;
        }
      }
      const updated = await regenerateVatReport(targetReportId);
      const { details, foreignRows } = await loadCombinedDetails(updated.id);
      setForeignOrderRows(foreignRows);
      setData(details);
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
      shippingGrossAmount?: number;
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
        shippingGrossAmount: row.shippingGrossAmount,
        vatManualOverride: false,
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
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
      setAddModalOpen(false);
      resetNewRow();
    } catch (err: unknown) {
      setAddRowError(err instanceof Error ? err.message : 'Памылка дадання радка справаздачы');
    } finally {
      setAddingRow(false);
    }
  };

  const openBlobInNewTab = (blob: Blob) => {
    const url = URL.createObjectURL(blob);
    window.open(url, '_blank', 'noopener,noreferrer');
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
  };

  const downloadBlobAsFile = (blob: Blob, fileName: string) => {
    const safeName = fileName.replace(/[\\/:*?"<>|]/g, '_').trim() || 'invoice.pdf';
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = safeName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
  };

  const getInvoiceNumberForFile = (raw: string | null | undefined, fallback: string): string => {
    const source = String(raw ?? '').trim();
    const compact = source.replace(/\s+/g, '');
    const withoutHash = compact.replace(/^#/, '');
    const digitsOnly = withoutHash.replace(/\D/g, '');
    if (digitsOnly) return digitsOnly;
    if (withoutHash) return withoutHash;
    return fallback;
  };

  const handleUploadInvoice = async (rowId: number) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = 'application/pdf';
    input.onchange = async () => {
      const file = input.files?.[0];
      if (!file) return;
      try {
        await uploadVatReportRowInvoice(rowId, file);
        const { details, foreignRows } = await loadCombinedDetails(reportId);
        setForeignOrderRows(foreignRows);
        setData(details);
      } catch (err: unknown) {
        setError(err instanceof Error ? err.message : 'Памылка загрузкі фактуры');
      }
    };
    input.click();
  };

  const handleOpenInvoice = async (rowId: number) => {
    try {
      const { blob } = await downloadVatReportRowInvoice(rowId);
      openBlobInNewTab(blob);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка загрузкі фактуры');
    }
  };

  const handleExportTableToPdf = () => {
    const table = detailsTableRef.current;
    if (!table) return;
    const ordersWithInvoice = new Set(
      (expandedRow?.polandRows ?? [])
        .filter((r) => Boolean(r.invoiceFileName))
        .map((r) => r.orderNumber.trim().toLowerCase())
    );
    const exportRows = visiblePolandRows.filter(
      (r) => !ordersWithInvoice.has(r.orderNumber.trim().toLowerCase())
    );
    if (exportRows.length === 0) {
      setError('Для экспарту няма радкоў: усе заказы маюць загружаныя фактуры.');
      return;
    }

    const printableTable = table.cloneNode(true) as HTMLTableElement;
    const body = printableTable.tBodies[0];
    if (body) {
      Array.from(body.rows).forEach((tr) => {
        const cells = tr.querySelectorAll('td');
        if (cells.length < 2) return;
        const orderNumber = cells[0]?.textContent?.trim().toLowerCase() ?? '';
        if (ordersWithInvoice.has(orderNumber)) {
          tr.remove();
        }
      });
    }
    // Remove action column cells and keep only data columns.
    printableTable.querySelectorAll('tr').forEach((row) => {
      const cells = Array.from(row.querySelectorAll('th,td'));
      if (cells.length >= 7) {
        cells[cells.length - 1]?.remove();
      }
    });
    // Remove VAT filter control from export header.
    printableTable.querySelectorAll('button[aria-label="Фільтр па стаўцы VAT"]').forEach((el) => el.remove());
    // Export should use Polish column labels while UI stays Belarusian.
    const exportHeaderCells = printableTable.querySelectorAll('thead th');
    if (exportHeaderCells.length >= 6) {
      exportHeaderCells[0].textContent = 'Numer zamowienia';
      exportHeaderCells[1].textContent = 'Data';
      exportHeaderCells[2].textContent = 'Stawka VAT';
      exportHeaderCells[3].textContent = 'Kwota brutto';
      exportHeaderCells[4].textContent = 'VAT';
      exportHeaderCells[5].textContent = 'Kwota netto';
    }

    const totals = exportRows.reduce(
      (acc, row) => ({
        grossAmount: acc.grossAmount + row.grossAmount,
        vatAmount: acc.vatAmount + row.vatAmount,
        netAmount: acc.netAmount + row.netAmount,
      }),
      { grossAmount: 0, vatAmount: 0, netAmount: 0 }
    );

    const printableBody = printableTable.tBodies[0] ?? printableTable.createTBody();
    const totalRow = printableBody.insertRow();
    totalRow.className = 'export-total-row';
    const labelCell = totalRow.insertCell();
    labelCell.colSpan = 3;
    labelCell.textContent = 'Razem';
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

  const handleExportForeignOrderToXml = async (row: VatReportDetails['rows'][number]) => {
    setError(null);
    const invoiceNumber = getInvoiceNumberForFile(row.name, `order-${row.shopifyOrderId || reportId}`);
    let invoiceSettings: {
      companyName: string;
      address: string;
      email: string;
      website: string;
      nip: string;
      currency: string;
    } | null = null;
    try {
      invoiceSettings = await fetchInvoiceSettings();
    } catch {
      invoiceSettings = null;
    }
    const currency = (invoiceSettings?.currency ?? 'PLN').trim() || 'PLN';
    const itemRows = row.polandRows.flatMap((group) =>
      group.items.map((item) => {
        const rate = item.assignedVatRatePercent / 100;
        const vatAmount = rate > 0 ? round2((item.grossAmount * rate) / (1 + rate)) : 0;
        const netAmount = round2(item.grossAmount - vatAmount);
        return {
          title: item.productTitle,
          quantity: item.quantity,
          netAmount,
          vatRatePercent: item.assignedVatRatePercent,
          vatAmount,
          grossAmount: item.grossAmount,
        };
      })
    );
    const shippingRows = row.polandRows
      .filter((group) => group.shippingGrossAmount > 0)
      .map((group) => ({
        vatRatePercent: group.vatRatePercent,
        netAmount: group.shippingNetAmount,
        vatAmount: round2(group.shippingGrossAmount - group.shippingNetAmount),
        grossAmount: group.shippingGrossAmount,
      }));

    const lineRows = [
      ...itemRows.map((item) => ({
        title: item.title || '—',
        quantity: item.quantity > 0 ? item.quantity : 1,
        netAmount: item.netAmount,
        vatRatePercent: item.vatRatePercent,
        vatAmount: item.vatAmount,
        grossAmount: item.grossAmount,
      })),
      ...shippingRows.map((shipping) => ({
        title: 'Shipping',
        quantity: 1,
        netAmount: shipping.netAmount,
        vatRatePercent: shipping.vatRatePercent,
        vatAmount: shipping.vatAmount,
        grossAmount: shipping.grossAmount,
      })),
    ];

    const formatKsefDate = (raw: string | null | undefined): string => {
      if (!raw) return new Date().toISOString().slice(0, 10);
      const d = new Date(raw);
      if (Number.isNaN(d.getTime())) return new Date().toISOString().slice(0, 10);
      return d.toISOString().slice(0, 10);
    };

    const decimal = (value: number) => round2(value).toFixed(2);
    const xmlItems = lineRows
      .map(
        (item, index) => `    <FaWiersz>
      <NrWierszaFa>${index + 1}</NrWierszaFa>
      <P_7>${escapeXml(item.title)}</P_7>
      <P_8A>szt.</P_8A>
      <P_8B>${decimal(item.quantity)}</P_8B>
      <P_9A>${decimal(item.netAmount / Math.max(item.quantity, 1))}</P_9A>
      <P_11>${decimal(item.vatAmount)}</P_11>
      <P_12>${decimal(item.vatRatePercent)}</P_12>
    </FaWiersz>`
      )
      .join('\n');
    const sellerNip = (invoiceSettings?.nip ?? '').replace(/\D/g, '');
    const sellerName = invoiceSettings?.companyName?.trim() || 'Seller';
    const buyerName = row.deliveryName?.trim() || row.name?.trim() || 'Buyer';
    const buyerAddress = row.shippingAddress || row.deliveryAddress || row.billingAddress || '';
    const issueDate = formatKsefDate(row.orderDateUtc);
    const netTotalValue = round2(row.netAmount ?? 0);
    const vatTotalValue = round2(row.vat);
    const grossTotalValue = round2(row.grossAmount ?? 0);
    const netTotal = decimal(netTotalValue);
    const vatTotal = decimal(vatTotalValue);
    const grossTotal = decimal(grossTotalValue);
    const linesNetTotal = round2(lineRows.reduce((sum, item) => sum + item.netAmount, 0));
    const linesVatTotal = round2(lineRows.reduce((sum, item) => sum + item.vatAmount, 0));
    const linesGrossTotal = round2(lineRows.reduce((sum, item) => sum + item.grossAmount, 0));

    if (!invoiceSettings?.companyName?.trim()) {
      setError('Для экспарту KSeF запоўніце назву прадаўца ў наладах фактуры.');
      return;
    }
    if (sellerNip.length !== 10) {
      setError('Для экспарту KSeF патрэбны карэктны NIP прадаўца (10 лічбаў) у наладах фактуры.');
      return;
    }
    if (!invoiceNumber.trim()) {
      setError('Для экспарту KSeF адсутнічае нумар фактуры.');
      return;
    }
    if (!issueDate.trim()) {
      setError('Для экспарту KSeF адсутнічае дата фактуры.');
      return;
    }
    if (lineRows.length === 0) {
      setError('Для экспарту KSeF патрэбна хаця б адна пазіцыя фактуры.');
      return;
    }
    if (!buyerName.trim()) {
      setError('Для экспарту KSeF адсутнічаюць даныя пакупніка.');
      return;
    }
    if (currency.length !== 3) {
      setError('Для экспарту KSeF валюта павінна быць у фармаце ISO (3 літары, напр. PLN).');
      return;
    }
    if (
      Math.abs(linesNetTotal - netTotalValue) > 0.01 ||
      Math.abs(linesVatTotal - vatTotalValue) > 0.01 ||
      Math.abs(linesGrossTotal - grossTotalValue) > 0.01
    ) {
      setError('Сумы радкоў не супадаюць з агульнымі сумамі. Праверце даныя перад экспартам у KSeF.');
      return;
    }

    const generationTimestamp = new Date().toISOString();
    const xml = `<?xml version="1.0" encoding="UTF-8"?>
<Faktura xmlns="http://crd.gov.pl/wzor/2024/10/31/10312/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Naglowek>
    <KodFormularza kodSystemowy="FA (3)" wersjaSchemy="1-0E">FA</KodFormularza>
    <WariantFormularza>3</WariantFormularza>
    <DataWytworzeniaFa>${escapeXml(generationTimestamp)}</DataWytworzeniaFa>
    <SystemInfo>Kirma.sh</SystemInfo>
  </Naglowek>
  <Podmiot1>
    <DaneIdentyfikacyjne>
      <NIP>${escapeXml(sellerNip || '0000000000')}</NIP>
      <Nazwa>${escapeXml(sellerName)}</Nazwa>
    </DaneIdentyfikacyjne>
    <Adres>
      <AdresL1>${escapeXml(invoiceSettings?.address ?? '')}</AdresL1>
    </Adres>
  </Podmiot1>
  <Podmiot2>
    <DaneIdentyfikacyjne>
      <Nazwa>${escapeXml(buyerName)}</Nazwa>
    </DaneIdentyfikacyjne>
    <Adres>
      <AdresL1>${escapeXml(buyerAddress)}</AdresL1>
    </Adres>
  </Podmiot2>
  <Fa>
    <KodWaluty>${escapeXml(currency)}</KodWaluty>
    <P_1>${escapeXml(issueDate)}</P_1>
    <P_2>${escapeXml(invoiceNumber)}</P_2>
    <P_6>${escapeXml(issueDate)}</P_6>
${xmlItems}
    <FaCtrl>
      <LiczbaWierszyFaktur>${lineRows.length}</LiczbaWierszyFaktur>
      <WartoscFaktury>${grossTotal}</WartoscFaktury>
      <WartoscNetto>${netTotal}</WartoscNetto>
      <WartoscVat>${vatTotal}</WartoscVat>
    </FaCtrl>
  </Fa>
</Faktura>`;
    const blob = new Blob([xml], { type: 'application/xml;charset=utf-8' });
    downloadBlobAsFile(blob, `${invoiceNumber}.xml`);
  };

  const confirmDeleteRow = async () => {
    if (!pendingDeleteRow) return;
    const { rowId, rowKey } = pendingDeleteRow;
    setDeletingRowKey(rowKey);
    setError(null);
    try {
      await deleteVatReportRow(rowId);
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
      if (editingRowKey === rowKey) setEditingRowKey(null);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка выдалення радка справаздачы');
    } finally {
      setDeletingRowKey(null);
      setPendingDeleteRow(null);
    }
  };

  const confirmMoveRowToForeign = async () => {
    if (!pendingMoveToForeignRow) return;
    const { rowId, rowKey } = pendingMoveToForeignRow;
    const name = moveToForeignName.trim();
    const address = moveToForeignAddress.trim();
    if (!name) {
      setError('Увядзіце імя атрымальніка для фактуры');
      return;
    }
    if (!address) {
      setError('Увядзіце адрас для пераносу ў замежныя');
      return;
    }

    setMovingToForeignRowKey(rowKey);
    setError(null);
    try {
      await moveVatReportRowToForeign({ rowId, deliveryName: name, deliveryAddress: address });
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
      if (editingRowKey === rowKey) setEditingRowKey(null);
      setPendingMoveToForeignRow(null);
      setMoveToForeignName('');
      setMoveToForeignAddress('');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка пераносу радка ў замежныя');
    } finally {
      setMovingToForeignRowKey(null);
    }
  };

  const handleUpdateForeignItemVat = async (itemId: number, vatRatePercent: number) => {
    setUpdatingItemVatId(itemId);
    setError(null);
    try {
      await updateVatReportRowItemVat({ itemId, vatRatePercent });
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка абнаўлення VAT па тавары');
    } finally {
      setUpdatingItemVatId(null);
    }
  };

  const handleUpdateForeignShippingVat = async (
    group: VatReportDetails['rows'][number]['polandRows'][number],
    vatRatePercent: number
  ) => {
    setUpdatingShippingVatRowId(group.id);
    setError(null);
    try {
      const itemsVat = round2(
        group.items.reduce((sum, item) => {
          const rate = item.assignedVatRatePercent / 100;
          if (rate <= 0) return sum;
          return sum + round2((item.grossAmount * rate) / (1 + rate));
        }, 0)
      );
      const shippingCalc = recalcVatAndNet(group.shippingGrossAmount, vatRatePercent);
      const vatAmount = round2(itemsVat + shippingCalc.vatAmount);
      const netAmount = round2(group.grossAmount - vatAmount);
      await updateVatReportRow({
        rowId: group.id,
        vatRatePercent,
        grossAmount: group.grossAmount,
        vatAmount,
        netAmount,
        shippingGrossAmount: group.shippingGrossAmount,
      });
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка абнаўлення VAT па дастаўцы');
    } finally {
      setUpdatingShippingVatRowId(null);
    }
  };

  useEffect(() => {
    if (!pendingDeleteRow && !pendingRegenerateRowKey && !addModalOpen && !pendingMoveToForeignRow) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      if (deletingRowKey || regeneratingRowKey || addingRow || movingToForeignRowKey) return;
      setPendingDeleteRow(null);
      setPendingRegenerateRowKey(null);
      setPendingMoveToForeignRow(null);
      setMoveToForeignName('');
      setMoveToForeignAddress('');
      setAddModalOpen(false);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [
    pendingDeleteRow,
    pendingRegenerateRowKey,
    addModalOpen,
    pendingMoveToForeignRow,
    deletingRowKey,
    regeneratingRowKey,
    addingRow,
    movingToForeignRowKey,
  ]);

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
                {isForeignReportOnly ? (
                  <>
                    <th className="px-4 py-2.5">Нумар замовы</th>
                    <th className="px-4 py-2.5">Дата</th>
                    <th className="px-4 py-2.5">Дастаўка</th>
                    <th className="px-4 py-2.5 text-right">Сума нета</th>
                    <th className="px-4 py-2.5 text-right">VAT</th>
                    <th className="px-4 py-2.5 text-right">Сума брута</th>
                    <th className="px-4 py-2.5 text-right">XML</th>
                  </>
                ) : (
                  <>
                    <th className="px-4 py-2.5">Тып</th>
                    <th className="px-4 py-2.5">Назва</th>
                    <th className="px-4 py-2.5 text-right">VAT</th>
                    <th className="px-4 py-2.5 text-right">Дзеянне</th>
                  </>
                )}
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {data.rows.map((row) => (
                <Fragment key={`${row.type}-${row.shopifyOrderId}`}>
                  <tr
                    className={`transition ${
                      row.type === 'poland' || row.type === 'foreign' ? 'cursor-pointer hover:bg-primary/10' : ''
                    } ${
                      row.type === 'foreign' &&
                      row.shopifyOrderId !== 'foreign-summary' &&
                      row.polandRows.some((group) => Boolean(group.invoiceFileName))
                        ? 'bg-emerald-200/60 font-medium'
                        : ''
                    }`}
                    onClick={() => {
                      if (row.type === 'poland' || row.type === 'foreign') {
                        setExpandedOrderId((prev) => {
                          const next = prev === row.shopifyOrderId ? null : row.shopifyOrderId;
                          if (row.type === 'foreign' && next === null) {
                            setExpandedForeignOrderId(null);
                          }
                          return next;
                        });
                      }
                    }}
                  >
                    {row.type === 'foreign' && row.shopifyOrderId !== 'foreign-summary' ? (
                      <>
                        <td className="px-4 py-3">
                          <div className="inline-flex items-center gap-2">
                            <span>{row.name}</span>
                            {row.polandRows.some((group) => Boolean(group.invoiceFileName)) && (
                              <span className="rounded-full bg-emerald-600 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-white">
                                Фактура загружана
                              </span>
                            )}
                          </div>
                        </td>
                        <td className="px-4 py-3">{row.orderDateUtc ? formatDate(row.orderDateUtc) : '—'}</td>
                        <td className="px-4 py-3">
                          <div>{row.deliveryName || '—'}</div>
                          <div className="text-xs text-gray-500">{row.deliveryAddress || '—'}</div>
                        </td>
                        <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.netAmount ?? 0)}</td>
                        <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.vat)}</td>
                        <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.grossAmount ?? 0)}</td>
                        <td className="px-4 py-3 text-right">
                          <div className="inline-flex items-center gap-2">
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                const targetRowId = row.polandRows[0]?.id;
                                if (targetRowId) void handleUploadInvoice(targetRowId);
                              }}
                              className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                              aria-label="Загрузіць фактуру"
                              title="Загрузіць фактуру"
                            >
                              <FiUpload className="size-4" aria-hidden />
                            </button>
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                handleExportForeignOrderToXml(row);
                              }}
                              className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                              aria-label="Экспарт у XML"
                              title="Экспарт у XML"
                            >
                              <FiDownload className="size-4" aria-hidden />
                            </button>
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                const targetRowId = row.polandRows[0]?.id;
                                if (!targetRowId) return;
                                setPendingDeleteRow({
                                  rowId: targetRowId,
                                  rowKey: `foreign-${targetRowId}`,
                                });
                              }}
                              disabled={!row.polandRows[0]?.id || deletingRowKey === `foreign-${row.polandRows[0]?.id}`}
                              className="inline-flex size-8 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:opacity-60"
                              aria-label="Выдаліць радок"
                              title="Выдаліць радок"
                            >
                              {deletingRowKey === `foreign-${row.polandRows[0]?.id}` ? (
                                <span className="size-3.5 animate-spin rounded-full border-2 border-red-300 border-t-red-600" />
                              ) : (
                                <FiTrash2 className="size-4" aria-hidden />
                              )}
                            </button>
                          </div>
                        </td>
                      </>
                    ) : (
                      <>
                  <td className="px-4 py-3">{row.type === 'poland' ? 'Польшча' : 'Замежжа'}</td>
                  <td className="px-4 py-3">{row.type === 'poland' ? 'Польшча' : 'Замежжа'}</td>
                        <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.vat)}</td>
                        <td className="px-4 py-3 text-right">
                          <button
                            type="button"
                            onClick={(e) => {
                              e.stopPropagation();
                              setPendingRegenerateRowKey(`${row.type}-${row.shopifyOrderId}`);
                            }}
                            disabled={regeneratingRowKey === `${row.type}-${row.shopifyOrderId}`}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:opacity-60"
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
                      </>
                    )}
                  </tr>
                    {row.type === 'foreign' && row.shopifyOrderId !== 'foreign-summary' && expandedOrderId === row.shopifyOrderId && (
                    <tr className="bg-gray-50/50">
                      <td className="px-4 py-3" colSpan={7}>
                        <table className="min-w-full border-collapse text-left text-xs">
                          <thead>
                            <tr className="border-b border-gray-200 text-[11px] font-semibold uppercase tracking-wide text-gray-500">
                              <th className="px-2 py-1.5">Назва</th>
                              <th className="px-2 py-1.5 text-right">Колькасць</th>
                              <th className="px-2 py-1.5 text-right">Сума нета</th>
                              <th className="px-2 py-1.5 text-right">Стаўка VAT</th>
                              <th className="px-2 py-1.5 text-right">Сума VAT</th>
                              <th className="px-2 py-1.5 text-right">Сума брута</th>
                            </tr>
                          </thead>
                          <tbody className="divide-y divide-gray-100">
                            {row.polandRows.flatMap((group) =>
                              group.items.map((item, idx) => {
                                const rate = item.assignedVatRatePercent / 100;
                                const vatAmount = rate > 0 ? round2((item.grossAmount * rate) / (1 + rate)) : 0;
                                const netAmount = round2(item.grossAmount - vatAmount);
                                return (
                                  <tr key={`${group.id}-${idx}`}>
                                    <td className="px-2 py-1.5">{item.productTitle}</td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">{item.quantity}</td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(netAmount)}</td>
                                    <td className="px-2 py-1.5 text-right">
                                      <div className="inline-flex items-center justify-end gap-2">
                                        <select
                                          value={String(item.assignedVatRatePercent)}
                                          onChange={(e) => {
                                            const nextVat = Number(e.currentTarget.value);
                                            if (!Number.isFinite(nextVat)) return;
                                            void handleUpdateForeignItemVat(item.id, nextVat);
                                          }}
                                          disabled={updatingItemVatId === item.id}
                                          className="w-20 rounded-md border border-gray-200 bg-white px-2 py-1 text-right text-xs focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25 disabled:opacity-60"
                                        >
                                          <option value="0">0%</option>
                                          <option value="5">5%</option>
                                          <option value="23">23%</option>
                                        </select>
                                        {updatingItemVatId === item.id && (
                                          <span className="size-3 animate-spin rounded-full border-2 border-primary/25 border-t-primary" />
                                        )}
                                      </div>
                                    </td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(vatAmount)}</td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(item.grossAmount)}</td>
                                  </tr>
                                );
                              })
                            )}
                            {row.polandRows
                              .filter((group) => group.shippingGrossAmount > 0)
                              .map((group) => (
                                <tr key={`shipping-${group.id}`} className="bg-white">
                                  <td className="px-2 py-1.5 font-medium">Дастаўка ({formatAmount(group.vatRatePercent)}%)</td>
                                  <td className="px-2 py-1.5 text-right tabular-nums">1</td>
                                  <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(group.shippingNetAmount)}</td>
                                  <td className="px-2 py-1.5 text-right">
                                    <div className="inline-flex items-center justify-end gap-2">
                                      <select
                                        value={String(group.vatRatePercent)}
                                        onChange={(e) => {
                                          const nextVat = Number(e.currentTarget.value);
                                          if (!Number.isFinite(nextVat)) return;
                                          void handleUpdateForeignShippingVat(group, nextVat);
                                        }}
                                        disabled={updatingShippingVatRowId === group.id}
                                        className="w-20 rounded-md border border-gray-200 bg-white px-2 py-1 text-right text-xs focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25 disabled:opacity-60"
                                      >
                                        <option value="0">0%</option>
                                        <option value="5">5%</option>
                                        <option value="23">23%</option>
                                      </select>
                                      {updatingShippingVatRowId === group.id && (
                                        <span className="size-3 animate-spin rounded-full border-2 border-primary/25 border-t-primary" />
                                      )}
                                    </div>
                                  </td>
                                  <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(group.shippingGrossAmount - group.shippingNetAmount)}</td>
                                  <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(group.shippingGrossAmount)}</td>
                                </tr>
                              ))}
                          </tbody>
                        </table>
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {!isForeignReportOnly && expandedRow && expandedRow.type === 'poland' && (
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
                    className="inline-flex size-9 shrink-0 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
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
                className="inline-flex size-9 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary active:scale-[0.99]"
                aria-label="Дадаць радок"
                title="Дадаць радок"
              >
                <FiPlus className="size-4" aria-hidden />
              </button>
              <button
                type="button"
                onClick={handleExportTableToPdf}
                className="inline-flex size-9 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary active:scale-[0.99]"
                aria-label="Экспарт у PDF"
                title="Экспарт у PDF"
              >
                <FiDownload className="size-4" aria-hidden />
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
                    <tr
                      className={`${row.invoiceFileName ? 'bg-emerald-200/60 font-medium' : ''} cursor-pointer hover:bg-primary/10`}
                      onClick={(e) => {
                        const target = e.target as HTMLElement;
                        if (target.closest('button, input, select, textarea, a, label')) return;
                        setExpandedPolandRowId((prev) => (prev === row.id ? null : row.id));
                      }}
                    >
                      <td className="px-4 py-3">
                        <div className="inline-flex items-center gap-2">
                          <span>{row.orderNumber}</span>
                          {row.invoiceFileName && (
                            <span className="rounded-full bg-emerald-600 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-white">
                              Фактура загружана
                            </span>
                          )}
                        </div>
                      </td>
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
                            onClick={() => handleUploadInvoice(row.id)}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                            aria-label="Загрузіць фактуру"
                            title="Загрузіць фактуру"
                          >
                            <FiUpload className="size-4" aria-hidden />
                          </button>
                          <button
                            type="button"
                            onClick={() => handleOpenInvoice(row.id)}
                            disabled={!row.invoiceFileName}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:cursor-not-allowed disabled:opacity-40"
                            aria-label="Праглядзець фактуру"
                            title={row.invoiceFileName ? 'Праглядзець фактуру' : 'Фактура не загружана'}
                          >
                            <FiEye className="size-4" aria-hidden />
                          </button>
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
                                      shippingGrossAmount: edited.shippingGrossAmount,
                                    });
                                    const { details, foreignRows } = await loadCombinedDetails(reportId);
                                    setForeignOrderRows(foreignRows);
                                    setData(details);
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
                            className={`inline-flex size-8 items-center justify-center rounded-full border text-gray-900 shadow-sm transition ${
                              isEditing
                                ? 'border-primary/40 bg-white text-primary hover:bg-primary/10'
                                : 'border-gray-200 bg-white hover:border-primary/40 hover:bg-primary/10 hover:text-primary'
                            }`}
                            aria-label={isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                            title={isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                          >
                            <FiEdit2 className="size-4" aria-hidden />
                          </button>
                          <button
                            type="button"
                            onClick={() => {
                              setPendingMoveToForeignRow({ rowId: row.id, rowKey });
                              setMoveToForeignName('');
                              setMoveToForeignAddress('');
                            }}
                            disabled={movingToForeignRowKey === rowKey}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:opacity-60"
                            aria-label="Перанесці ў замежныя"
                            title="Перанесці ў замежныя"
                          >
                            {movingToForeignRowKey === rowKey ? (
                              <span className="size-3.5 animate-spin rounded-full border-2 border-primary/25 border-t-primary" />
                            ) : (
                              <FiCornerUpRight className="size-4" aria-hidden />
                            )}
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
                    {expandedPolandRowId === row.id && row.items.length > 0 && (
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

      {foreignOrderRows.length > 0 &&
        (expandedRow?.type === 'foreign' || (isForeignReportOnly && expandedOrderId !== null)) && (
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="flex flex-wrap items-end justify-between gap-3 border-b border-gray-100 px-6 py-4">
            <h3 className="text-sm font-semibold text-gray-900">Дэталі па Замежжы</h3>
            <label className="w-full max-w-[11.5rem] space-y-1">
              <span className="text-xs font-medium uppercase tracking-wide text-gray-500">Пошук</span>
              <div className="flex items-center gap-2">
                <input
                  type="text"
                  value={foreignOrderSearch}
                  onChange={(e) => setForeignOrderSearch(e.currentTarget.value)}
                  placeholder="Нумар замовы"
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2.5 text-sm text-gray-800 transition placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
                <button
                  type="button"
                  onClick={() => setForeignOrderSearch('')}
                  className="inline-flex size-9 shrink-0 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                  aria-label="Скінуць пошук"
                  title="Скінуць пошук"
                >
                  <FiX className="size-4" aria-hidden />
                </button>
              </div>
            </label>
          </div>
          <div className="overflow-x-auto">
            <table className="min-w-full border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="px-4 py-2.5">Нумар замовы</th>
                  <th className="px-4 py-2.5">Дата</th>
                  <th className="px-4 py-2.5">Дастаўка</th>
                  <th className="px-4 py-2.5 text-right">Сума нета</th>
                  <th className="px-4 py-2.5 text-right">VAT</th>
                  <th className="px-4 py-2.5 text-right">Сума брута</th>
                  <th className="px-4 py-2.5 text-right">XML</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {visibleForeignRows.map((row) => (
                  <Fragment key={`foreign-${row.shopifyOrderId}`}>
                    <tr
                      className={`cursor-pointer transition hover:bg-primary/10 ${
                        row.polandRows.some((group) => Boolean(group.invoiceFileName))
                          ? 'bg-emerald-200/60 font-medium'
                          : ''
                      }`}
                      onClick={() =>
                        setExpandedForeignOrderId((prev) =>
                          prev === row.shopifyOrderId ? null : row.shopifyOrderId
                        )
                      }
                    >
                      <td className="px-4 py-3">
                        <div className="inline-flex items-center gap-2">
                          <span>{row.name}</span>
                          {row.polandRows.some((group) => Boolean(group.invoiceFileName)) && (
                            <span className="rounded-full bg-emerald-600 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-white">
                              Фактура загружана
                            </span>
                          )}
                        </div>
                      </td>
                      <td className="px-4 py-3">{row.orderDateUtc ? formatDate(row.orderDateUtc) : '—'}</td>
                      <td className="px-4 py-3">
                        <div>{row.deliveryName || '—'}</div>
                        <div className="text-xs text-gray-500">{row.deliveryAddress || '—'}</div>
                      </td>
                      <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.netAmount ?? 0)}</td>
                      <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.vat)}</td>
                      <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.grossAmount ?? 0)}</td>
                      <td className="px-4 py-3 text-right">
                        <div className="inline-flex items-center gap-2">
                          <button
                            type="button"
                            onClick={(e) => {
                              e.stopPropagation();
                              const targetRowId = row.polandRows[0]?.id;
                              if (targetRowId) void handleUploadInvoice(targetRowId);
                            }}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                            aria-label="Загрузіць фактуру"
                            title="Загрузіць фактуру"
                          >
                            <FiUpload className="size-4" aria-hidden />
                          </button>
                          <button
                            type="button"
                            onClick={(e) => {
                              e.stopPropagation();
                              handleExportForeignOrderToXml(row);
                            }}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                            aria-label="Экспарт у XML"
                            title="Экспарт у XML"
                          >
                            <FiDownload className="size-4" aria-hidden />
                          </button>
                          <button
                            type="button"
                            onClick={(e) => {
                              e.stopPropagation();
                              const targetRowId = row.polandRows[0]?.id;
                              if (!targetRowId) return;
                              setPendingDeleteRow({
                                rowId: targetRowId,
                                rowKey: `foreign-${targetRowId}`,
                              });
                            }}
                            disabled={!row.polandRows[0]?.id || deletingRowKey === `foreign-${row.polandRows[0]?.id}`}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:opacity-60"
                            aria-label="Выдаліць радок"
                            title="Выдаліць радок"
                          >
                            {deletingRowKey === `foreign-${row.polandRows[0]?.id}` ? (
                              <span className="size-3.5 animate-spin rounded-full border-2 border-red-300 border-t-red-600" />
                            ) : (
                              <FiTrash2 className="size-4" aria-hidden />
                            )}
                          </button>
                        </div>
                      </td>
                    </tr>
                    {expandedForeignOrderId === row.shopifyOrderId && (
                      <tr className="bg-gray-50/50">
                        <td className="px-4 py-3" colSpan={7}>
                          <div className="mb-3 overflow-x-auto rounded-lg border border-gray-200 bg-white">
                            <table className="min-w-full border-collapse text-left text-xs">
                              <thead>
                                <tr className="border-b border-gray-200 bg-gray-50 text-[11px] font-semibold uppercase tracking-wide text-gray-500">
                                  <th className="px-2 py-1.5 text-right">Стаўка VAT</th>
                                  <th className="px-2 py-1.5 text-right">Дастаўка (брута)</th>
                                  <th className="px-2 py-1.5 text-right">Сума брута</th>
                                  <th className="px-2 py-1.5 text-right">VAT</th>
                                  <th className="px-2 py-1.5 text-right">Сума нета</th>
                                  <th className="px-2 py-1.5 text-right">Дзеянне</th>
                                </tr>
                              </thead>
                              <tbody className="divide-y divide-gray-100">
                                {row.polandRows.map((group) => {
                                  const rowKey = String(group.id);
                                  const isEditing = editingRowKey === rowKey;
                                  const edited = editedRows[rowKey];
                                  const goodsGross = round2(group.grossAmount - group.shippingGrossAmount);
                                  const grossAmount = isEditing ? edited?.grossAmount ?? group.grossAmount : group.grossAmount;
                                  const vatAmount = isEditing ? edited?.vatAmount ?? group.vatAmount : group.vatAmount;
                                  const netAmount = isEditing ? edited?.netAmount ?? group.netAmount : group.netAmount;
                                  const shippingGrossAmount = isEditing
                                    ? edited?.shippingGrossAmount ?? group.shippingGrossAmount
                                    : group.shippingGrossAmount;

                                  return (
                                    <tr key={`foreign-group-${group.id}`}>
                                      <td className="px-2 py-1.5 text-right tabular-nums">
                                        {isEditing ? (
                                          <select
                                            value={edited?.vatRatePercent ?? group.vatRatePercent}
                                            onChange={(e) => {
                                              const value = Number(e.currentTarget.value) || 0;
                                              setEditedRows((prev) => {
                                                const base = prev[rowKey] ?? {
                                                  orderDateUtc: toDateInputValue(group.orderDateUtc),
                                                  vatRatePercent: group.vatRatePercent,
                                                  grossAmount: group.grossAmount,
                                                  vatAmount: group.vatAmount,
                                                  netAmount: group.netAmount,
                                                  shippingGrossAmount: group.shippingGrossAmount,
                                                  vatManualOverride: false,
                                                };
                                                const autoVat = recalcVatAndNet(base.grossAmount, value).vatAmount;
                                                const nextVat = base.vatManualOverride ? base.vatAmount : autoVat;
                                                return {
                                                  ...prev,
                                                  [rowKey]: {
                                                    ...base,
                                                    vatRatePercent: value,
                                                    vatAmount: nextVat,
                                                    netAmount: round2(base.grossAmount - nextVat),
                                                  },
                                                };
                                              });
                                            }}
                                            className="w-20 rounded-md border border-gray-200 px-2 py-1 text-right text-xs"
                                          >
                                            <option value={0}>0</option>
                                            <option value={5}>5</option>
                                            <option value={23}>23</option>
                                          </select>
                                        ) : (
                                          `${formatAmount(group.vatRatePercent)}%`
                                        )}
                                      </td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">
                                        {isEditing ? (
                                          <input
                                            type="number"
                                            step="0.01"
                                            value={shippingGrossAmount}
                                            onChange={(e) => {
                                              const value = Math.max(0, Number(e.currentTarget.value) || 0);
                                              setEditedRows((prev) => {
                                                const base = prev[rowKey] ?? {
                                                  orderDateUtc: toDateInputValue(group.orderDateUtc),
                                                  vatRatePercent: group.vatRatePercent,
                                                  grossAmount: group.grossAmount,
                                                  vatAmount: group.vatAmount,
                                                  netAmount: group.netAmount,
                                                  shippingGrossAmount: group.shippingGrossAmount,
                                                  vatManualOverride: false,
                                                };
                                                const nextGross = round2(goodsGross + value);
                                                const autoVat = recalcVatAndNet(nextGross, base.vatRatePercent).vatAmount;
                                                const nextVat = base.vatManualOverride ? base.vatAmount : autoVat;
                                                return {
                                                  ...prev,
                                                  [rowKey]: {
                                                    ...base,
                                                    shippingGrossAmount: value,
                                                    grossAmount: nextGross,
                                                    vatAmount: nextVat,
                                                    netAmount: round2(nextGross - nextVat),
                                                  },
                                                };
                                              });
                                            }}
                                            className="w-28 rounded-md border border-gray-200 px-2 py-1 text-right text-xs"
                                          />
                                        ) : (
                                          formatAmount(group.shippingGrossAmount)
                                        )}
                                      </td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(grossAmount)}</td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">
                                        {isEditing ? (
                                          <div className="inline-flex items-center justify-end gap-2">
                                            <label className="inline-flex items-center gap-1 text-[10px] font-medium uppercase tracking-wide text-gray-500">
                                              <input
                                                type="checkbox"
                                                checked={edited?.vatManualOverride ?? false}
                                                onChange={(e) => {
                                                  const checked = e.currentTarget.checked;
                                                  setEditedRows((prev) => {
                                                    const base = prev[rowKey] ?? {
                                                      orderDateUtc: toDateInputValue(group.orderDateUtc),
                                                      vatRatePercent: group.vatRatePercent,
                                                      grossAmount: group.grossAmount,
                                                      vatAmount: group.vatAmount,
                                                      netAmount: group.netAmount,
                                                      shippingGrossAmount: group.shippingGrossAmount,
                                                      vatManualOverride: false,
                                                    };
                                                    const autoVat = recalcVatAndNet(
                                                      base.grossAmount,
                                                      base.vatRatePercent
                                                    ).vatAmount;
                                                    const nextVat = checked ? base.vatAmount : autoVat;
                                                    return {
                                                      ...prev,
                                                      [rowKey]: {
                                                        ...base,
                                                        vatManualOverride: checked,
                                                        vatAmount: nextVat,
                                                        netAmount: round2(base.grossAmount - nextVat),
                                                      },
                                                    };
                                                  });
                                                }}
                                                className="size-3.5 rounded border-gray-300 accent-primary"
                                              />
                                              ручн.
                                            </label>
                                            <input
                                              type="number"
                                              step="0.01"
                                              value={vatAmount}
                                              onChange={(e) => {
                                                const value = Math.max(0, Number(e.currentTarget.value) || 0);
                                                setEditedRows((prev) => {
                                                  const base = prev[rowKey] ?? {
                                                    orderDateUtc: toDateInputValue(group.orderDateUtc),
                                                    vatRatePercent: group.vatRatePercent,
                                                    grossAmount: group.grossAmount,
                                                    vatAmount: group.vatAmount,
                                                    netAmount: group.netAmount,
                                                    shippingGrossAmount: group.shippingGrossAmount,
                                                    vatManualOverride: false,
                                                  };
                                                  return {
                                                    ...prev,
                                                    [rowKey]: {
                                                      ...base,
                                                      vatManualOverride: true,
                                                      vatAmount: value,
                                                      netAmount: round2(base.grossAmount - value),
                                                    },
                                                  };
                                                });
                                              }}
                                              className="w-24 rounded-md border border-gray-200 px-2 py-1 text-right text-xs"
                                            />
                                          </div>
                                        ) : (
                                          formatAmount(vatAmount)
                                        )}
                                      </td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(netAmount)}</td>
                                      <td className="px-2 py-1.5 text-right">
                                        <div className="inline-flex items-center gap-2">
                                          <button
                                            type="button"
                                            onClick={async () => {
                                              if (isEditing) {
                                                const changed = editedRows[rowKey];
                                                if (!changed) {
                                                  setEditingRowKey(null);
                                                  return;
                                                }
                                                try {
                                                  await updateVatReportRow({
                                                    rowId: group.id,
                                                    vatRatePercent: changed.vatRatePercent,
                                                    grossAmount: changed.grossAmount,
                                                    vatAmount: changed.vatAmount,
                                                    netAmount: changed.netAmount,
                                                    shippingGrossAmount:
                                                      changed.shippingGrossAmount ?? group.shippingGrossAmount,
                                                  });
                                                  const { details, foreignRows } = await loadCombinedDetails(reportId);
                                                  setForeignOrderRows(foreignRows);
                                                  setData(details);
                                                } catch (err: unknown) {
                                                  setError(
                                                    err instanceof Error
                                                      ? err.message
                                                      : 'Памылка захавання радка справаздачы'
                                                  );
                                                  return;
                                                }
                                                setEditingRowKey(null);
                                              } else {
                                                startEditRow(rowKey, {
                                                  orderDateUtc: group.orderDateUtc,
                                                  vatRatePercent: group.vatRatePercent,
                                                  grossAmount: group.grossAmount,
                                                  vatAmount: group.vatAmount,
                                                  netAmount: group.netAmount,
                                                  shippingGrossAmount: group.shippingGrossAmount,
                                                });
                                              }
                                            }}
                                            className={`inline-flex size-7 items-center justify-center rounded-full border text-gray-700 shadow-sm transition ${
                                              isEditing
                                                ? 'border-primary bg-primary text-white hover:bg-primary/90'
                                                : 'border-gray-200 bg-white hover:border-primary/40 hover:bg-primary/15 hover:text-primary'
                                            }`}
                                            aria-label={isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                                            title={isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                                          >
                                            <FiEdit2 className="size-3.5" aria-hidden />
                                          </button>
                                          <button
                                            type="button"
                                            onClick={() => setPendingDeleteRow({ rowId: group.id, rowKey })}
                                            disabled={deletingRowKey === rowKey}
                                            className="inline-flex size-7 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:opacity-60"
                                            aria-label="Выдаліць радок"
                                            title="Выдаліць радок"
                                          >
                                            {deletingRowKey === rowKey ? (
                                              <span className="size-3 animate-spin rounded-full border-2 border-red-300 border-t-red-600" />
                                            ) : (
                                              <FiTrash2 className="size-3.5" aria-hidden />
                                            )}
                                          </button>
                                        </div>
                                      </td>
                                    </tr>
                                  );
                                })}
                              </tbody>
                            </table>
                          </div>
                          <table className="min-w-full border-collapse text-left text-xs">
                            <thead>
                              <tr className="border-b border-gray-200 text-[11px] font-semibold uppercase tracking-wide text-gray-500">
                                <th className="px-2 py-1.5">Назва</th>
                                <th className="px-2 py-1.5 text-right">Колькасць</th>
                                <th className="px-2 py-1.5 text-right">Сума нета</th>
                                <th className="px-2 py-1.5 text-right">Стаўка VAT</th>
                                <th className="px-2 py-1.5 text-right">Сума VAT</th>
                                <th className="px-2 py-1.5 text-right">Сума брута</th>
                              </tr>
                            </thead>
                            <tbody className="divide-y divide-gray-100">
                              {row.polandRows.flatMap((group) =>
                                group.items.map((item, idx) => {
                                  const rate = item.assignedVatRatePercent / 100;
                                  const vatAmount = rate > 0 ? round2((item.grossAmount * rate) / (1 + rate)) : 0;
                                  const netAmount = round2(item.grossAmount - vatAmount);
                                  return (
                                    <tr key={`${group.id}-${idx}`}>
                                      <td className="px-2 py-1.5">{item.productTitle}</td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">{item.quantity}</td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(netAmount)}</td>
                                      <td className="px-2 py-1.5 text-right">
                                        <div className="inline-flex items-center justify-end gap-2">
                                          <select
                                            value={String(item.assignedVatRatePercent)}
                                            onChange={(e) => {
                                              const nextVat = Number(e.currentTarget.value);
                                              if (!Number.isFinite(nextVat)) return;
                                              void handleUpdateForeignItemVat(item.id, nextVat);
                                            }}
                                            disabled={updatingItemVatId === item.id}
                                            className="w-20 rounded-md border border-gray-200 bg-white px-2 py-1 text-right text-xs focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25 disabled:opacity-60"
                                          >
                                            <option value="0">0%</option>
                                            <option value="5">5%</option>
                                            <option value="23">23%</option>
                                          </select>
                                          {updatingItemVatId === item.id && (
                                            <span className="size-3 animate-spin rounded-full border-2 border-primary/25 border-t-primary" />
                                          )}
                                        </div>
                                      </td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(vatAmount)}</td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(item.grossAmount)}</td>
                                    </tr>
                                  );
                                })
                              )}
                              {row.polandRows
                                .filter((group) => group.shippingGrossAmount > 0)
                                .map((group) => (
                                  <tr key={`shipping-${group.id}`} className="bg-white">
                                    <td className="px-2 py-1.5 font-medium">Дастаўка ({formatAmount(group.vatRatePercent)}%)</td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">1</td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(group.shippingNetAmount)}</td>
                                    <td className="px-2 py-1.5 text-right">
                                      <div className="inline-flex items-center justify-end gap-2">
                                        <select
                                          value={String(group.vatRatePercent)}
                                          onChange={(e) => {
                                            const nextVat = Number(e.currentTarget.value);
                                            if (!Number.isFinite(nextVat)) return;
                                            void handleUpdateForeignShippingVat(group, nextVat);
                                          }}
                                          disabled={updatingShippingVatRowId === group.id}
                                          className="w-20 rounded-md border border-gray-200 bg-white px-2 py-1 text-right text-xs focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25 disabled:opacity-60"
                                        >
                                          <option value="0">0%</option>
                                          <option value="5">5%</option>
                                          <option value="23">23%</option>
                                        </select>
                                        {updatingShippingVatRowId === group.id && (
                                          <span className="size-3 animate-spin rounded-full border-2 border-primary/25 border-t-primary" />
                                        )}
                                      </div>
                                    </td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(group.shippingGrossAmount - group.shippingNetAmount)}</td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(group.shippingGrossAmount)}</td>
                                  </tr>
                                ))}
                            </tbody>
                          </table>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
                {visibleForeignRows.length === 0 && (
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
                    onChange={(e) => {
                      const orderNumber = e.currentTarget.value;
                      setNewRow((prev) => ({ ...prev, orderNumber }));
                    }}
                    disabled={addingRow}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Дата замовы</span>
                  <input
                    type="date"
                    value={newRow.orderDateUtc}
                    onChange={(e) => {
                      const orderDateUtc = e.currentTarget.value;
                      setNewRow((prev) => ({ ...prev, orderDateUtc }));
                    }}
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
                    onChange={(e) => {
                      const vatAmount = Number(e.currentTarget.value) || 0;
                      setNewRow((prev) => ({ ...prev, vatAmount }));
                    }}
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
                    onChange={(e) => {
                      const netAmount = Number(e.currentTarget.value) || 0;
                      setNewRow((prev) => ({ ...prev, netAmount }));
                    }}
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

      {pendingMoveToForeignRow && (
        <div
          className="fixed inset-0 z-[80] flex items-center justify-center bg-black/40 p-4"
          onClick={() => {
            if (movingToForeignRowKey) return;
            setPendingMoveToForeignRow(null);
            setMoveToForeignName('');
            setMoveToForeignAddress('');
          }}
        >
          <div
            className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="text-base font-semibold text-gray-900">Перанос у замежныя</div>
            <p className="mt-2 text-sm text-gray-600">
              Увядзіце даныя для фактуры. Радок будзе перанесены з польскага ў замежны справаздачу.
            </p>
            <label className="mt-4 block text-sm font-medium text-gray-700">
              Імя
              <input
                type="text"
                value={moveToForeignName}
                onChange={(e) => setMoveToForeignName(e.currentTarget.value)}
                placeholder="Увядзіце імя атрымальніка"
                disabled={!!movingToForeignRowKey}
                className="mt-1 w-full rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-800 transition placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20 disabled:opacity-60"
              />
            </label>
            <label className="mt-4 block text-sm font-medium text-gray-700">
              Адрас
              <textarea
                value={moveToForeignAddress}
                onChange={(e) => setMoveToForeignAddress(e.currentTarget.value)}
                placeholder="Увядзіце адрас"
                rows={3}
                disabled={!!movingToForeignRowKey}
                className="mt-1 w-full rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-800 transition placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20 disabled:opacity-60"
              />
            </label>
            <div className="mt-5 flex items-center justify-end gap-2">
              <button
                type="button"
                onClick={() => {
                  setPendingMoveToForeignRow(null);
                  setMoveToForeignName('');
                  setMoveToForeignAddress('');
                }}
                disabled={!!movingToForeignRowKey}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-60"
              >
                Адмена
              </button>
              <button
                type="button"
                onClick={confirmMoveRowToForeign}
                disabled={!!movingToForeignRowKey || !moveToForeignName.trim() || !moveToForeignAddress.trim()}
                className="inline-flex min-w-24 items-center justify-center rounded-lg border border-primary bg-primary px-3 py-1.5 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-60"
              >
                {movingToForeignRowKey ? (
                  <span className="size-4 animate-spin rounded-full border-2 border-primary/20 border-t-white" />
                ) : (
                  'Перанесці'
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
