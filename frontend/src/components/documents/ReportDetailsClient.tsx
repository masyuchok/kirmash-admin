'use client';

import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import LoadingSpinner from '@/components/ui/LoadingSpinner';
import OverpaidExpenseProductSearchSelect from '@/components/ui/OverpaidExpenseProductSearchSelect';
import { useTopbar } from '@/components/topbar/TopbarContext';
import {
  createVatReportCashSale,
  createVatReportExpense,
  createVatReportForeignRow,
  createVatReportRow,
  deleteVatReportCashSale,
  deleteVatReportExpense,
  deleteVatReportRow,
  downloadVatReportExpenseInvoice,
  downloadVatReportRowInvoice,
  fetchVatReportCombinedDetails,
  fetchVatReportDetails,
  fetchVatReportPeriods,
  fetchVatReports,
  fetchVatReportSourceOrders,
  fetchUnpaidLinkOptions,
  linkUnpaidProduct,
  regenerateVatReport,
  setVatReportLocked,
  moveVatReportRowToForeign,
  updateVatReportExpense,
  uploadVatReportExpenseInvoice,
  uploadVatReportRowInvoice,
  updateVatReportRow,
  updateVatReportRowItemVat,
} from '@/lib/api/reports';
import { fetchExpenseInvoiceTypes, fetchInvoiceSettings, type ExpenseInvoiceType } from '@/lib/api/settings';
import { fetchProductsWithSuppliers } from '@/lib/api/products';
import { fetchSupplyCatalogProducts } from '@/lib/api/supplies';
import { fetchSuppliers } from '@/lib/api/suppliers';
import { formatProductNameWithAuthor } from '@/lib/supply-draft';
import { makeSupplyLineKey } from '@/lib/supply-line-key';
import type { SupplyCatalogProduct } from '@/lib/api/supplies';
import type {
  VatReportDetails,
  VatReportExpenseRow,
  VatReportSourceOrderOption,
  VatReportUnpaidLinkOptions,
  VatReportUnpaidProductRow,
} from '@/types/report-details';
import type { ProductVariant, ProductWithSuppliers } from '@/types/product';
import type { Supplier } from '@/types/supplier';
import type { VatReportPeriod } from '@/types/report';
import { FiRefreshCw } from 'react-icons/fi';
import { FiChevronDown } from 'react-icons/fi';
import {
  FiArrowLeft,
  FiChevronLeft,
  FiChevronRight,
  FiCornerUpRight,
  FiDownload,
  FiEdit2,
  FiEye,
  FiLock,
  FiPlus,
  FiPrinter,
  FiTrash2,
  FiUnlock,
  FiUpload,
  FiX,
} from 'react-icons/fi';
import { useRouter } from 'next/navigation';

function formatAmount(value: number): string {
  return value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function formatDate(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleDateString('ru-RU', { timeZone: 'Europe/Warsaw' });
}

function formatSupplierExpenseOptionLabel(option: {
  expenseDateUtc: string;
  invoiceNumber: string;
  comment: string;
  grossAmount: number;
  totalProductUnits: number;
}): string {
  const title = option.invoiceNumber || option.comment || 'Аплата';
  const units =
    option.totalProductUnits > 0 ? `${option.totalProductUnits} ад.` : 'без тавараў';
  return [formatDate(option.expenseDateUtc), title, formatAmount(option.grossAmount), units].join(' · ');
}

function formatDatePl(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleDateString('pl-PL');
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function formatItemTitleForPolishInvoice(titleRaw: string, productTypeRaw: string): string {
  const title = (titleRaw ?? '').trim().replace(/^"+|"+$/g, '');
  const productType = (productTypeRaw ?? '').trim();
  if (!title) return '—';
  const source = `${productType} ${title}`.toLowerCase();
  const isBookmark =
    source.includes('bookmark') ||
    source.includes('zakladk') ||
    source.includes('zakładk') ||
    source.includes('закладк');
  const isJournal =
    source.includes('journal') ||
    source.includes('czasopis') ||
    source.includes('часоп') ||
    source.includes('журнал');
  const isBook =
    !isBookmark &&
    !isJournal &&
    (source.includes('book') ||
      source.includes('ksiaz') ||
      source.includes('książ') ||
      source.includes('кніг') ||
      source.includes('книг'));

  if (isBook) return `Książka "${title}"`;
  if (isJournal) return `Czasopismo "${title}"`;
  if (isBookmark) {
    const cleanedBookmarkTitle = title
      .replace(/^(zak[łl]adka do ksi[aą]?[żz]ek|zak[łl]adka do ksi[aą]?[żz]ki)\s*/i, '')
      .replace(/^(закладка для кн[иі]г)\s*/i, '')
      .trim()
      .replace(/^"+|"+$/g, '');
    return `Zakładka do książek "${cleanedBookmarkTitle || title}"`;
  }
  if (productType) return `${productType} "${title}"`;
  return title;
}

function extractCountryFromAddress(address: string): string {
  const parts = address
    .split(',')
    .map((part) => part.trim())
    .filter(Boolean);
  if (parts.length === 0) return '';

  const last = parts[parts.length - 1];
  if (/^[A-Z]{2}$/i.test(last)) return last.toUpperCase();

  const alias = INVOICE_COUNTRY_NAME_ALIASES[last.toLowerCase()];
  if (alias) return alias;

  const polishLabel = Object.values(INVOICE_COUNTRY_LABELS).find(
    (label) => label.toLowerCase() === last.toLowerCase(),
  );
  if (polishLabel) return polishLabel;

  return '';
}

function inferCountryFromCity(address: string): string {
  const lower = address.toLowerCase();
  for (const { pattern, country } of CITY_COUNTRY_LABELS) {
    if (pattern.test(lower)) return country;
  }
  return '';
}

const INVOICE_COUNTRY_LABELS: Record<string, string> = {
  PL: 'Polska',
  LT: 'Litwa',
  LV: 'Łotwa',
  EE: 'Estonia',
  NL: 'Holandia',
  DE: 'Niemcy',
  FR: 'Francja',
  BE: 'Belgia',
  CZ: 'Czechy',
  SK: 'Słowacja',
  UA: 'Ukraina',
  BY: 'Białoruś',
  US: 'Stany Zjednoczone',
  CA: 'Kanada',
  GB: 'Wielka Brytania',
  IE: 'Irlandia',
  AT: 'Austria',
  IT: 'Włochy',
  ES: 'Hiszpania',
  SE: 'Szwecja',
  NO: 'Norwegia',
  DK: 'Dania',
  FI: 'Finlandia',
  CH: 'Szwajcaria',
  HU: 'Węgry',
  RO: 'Rumunia',
  BG: 'Bułgaria',
  HR: 'Chorwacja',
  SI: 'Słowenia',
  GR: 'Grecja',
  PT: 'Portugalia',
};

const INVOICE_COUNTRY_NAME_ALIASES: Record<string, string> = {
  poland: 'Polska',
  austria: 'Austria',
  germany: 'Niemcy',
  netherlands: 'Holandia',
  belgium: 'Belgia',
  lithuania: 'Litwa',
  latvia: 'Łotwa',
  estonia: 'Estonia',
  czechia: 'Czechy',
  'czech republic': 'Czechy',
  slovakia: 'Słowacja',
  hungary: 'Węgry',
  romania: 'Rumunia',
  bulgaria: 'Bułgaria',
  croatia: 'Chorwacja',
  slovenia: 'Słowenia',
  france: 'Francja',
  italy: 'Włochy',
  spain: 'Hiszpania',
  portugal: 'Portugalia',
  sweden: 'Szwecja',
  norway: 'Norwegia',
  denmark: 'Dania',
  finland: 'Finlandia',
  ireland: 'Irlandia',
  'united kingdom': 'Wielka Brytania',
  'united states': 'Stany Zjednoczone',
  canada: 'Kanada',
  switzerland: 'Szwajcaria',
  ukraine: 'Ukraina',
  belarus: 'Białoruś',
  greece: 'Grecja',
};

const CITY_COUNTRY_LABELS: Array<{ pattern: RegExp; country: string }> = [
  { pattern: /wrocław|wroclaw|warszawa|kraków|krakow|gdańsk|gdansk|poznań|poznan|łódź|lodz/i, country: 'Polska' },
  { pattern: /vilnius|wilno|kaunas|kowno|klaipėda|klaipeda|šiauliai|siauliai/i, country: 'Litwa' },
  { pattern: /riga|ryga|daugavpils/i, country: 'Łotwa' },
  { pattern: /tallinn|tallin|tartu/i, country: 'Estonia' },
  { pattern: /amsterdam|rotterdam|haga|den haag|'s-gravenhage|gravenhage|utrecht/i, country: 'Holandia' },
  { pattern: /berlin|münchen|munich|hamburg|frankfurt|köln|cologne|dresden|leipzig/i, country: 'Niemcy' },
  { pattern: /paris|lyon|marseille|toulouse|nice/i, country: 'Francja' },
  { pattern: /prague|praha|brno|ostrava/i, country: 'Czechy' },
  { pattern: /bratislava|košice|kosice/i, country: 'Słowacja' },
  { pattern: /wien|vienna|salzburg|innsbruck|graz|linz|unterweitersdorf/i, country: 'Austria' },
  { pattern: /bruxelles|brussels|antwerp|antwerpen|ghent|gent|liège|liege/i, country: 'Belgia' },
  { pattern: /zürich|zurich|bern|geneva|genève|geneve|basel/i, country: 'Szwajcaria' },
  { pattern: /rome|roma|milan|milano|naples|napoli|turin|torino/i, country: 'Włochy' },
  { pattern: /madrid|barcelona|valencia|seville|sevilla/i, country: 'Hiszpania' },
  { pattern: /stockholm|göteborg|goteborg|malmö|malmo/i, country: 'Szwecja' },
  { pattern: /copenhagen|københavn|kobenhavn|aarhus/i, country: 'Dania' },
  { pattern: /oslo|bergen|trondheim/i, country: 'Norwegia' },
  { pattern: /helsinki|tampere|turku/i, country: 'Finlandia' },
  { pattern: /dublin|cork|galway/i, country: 'Irlandia' },
  { pattern: /london|manchester|birmingham|edinburgh|glasgow/i, country: 'Wielka Brytania' },
  { pattern: /budapest|debrecen|szeged/i, country: 'Węgry' },
  { pattern: /bucharest|bucurești|bucuresti|cluj/i, country: 'Rumunia' },
  { pattern: /athens|athina|thessaloniki/i, country: 'Grecja' },
];

function normalizeInvoiceAddress(address: string): string {
  return address.replace(/,\s*$/, '').trim();
}

function inferUsCountryFromAddress(address: string): string {
  const normalized = normalizeInvoiceAddress(address);
  if (!normalized) return '';
  if (
    /\b(los angeles|new york|san francisco|chicago|houston|miami|seattle|boston|philadelphia|atlanta|denver|portland|phoenix|dallas|austin|orlando|san diego|las vegas)\b/i.test(
      normalized,
    )
  ) {
    return 'Stany Zjednoczone';
  }
  if (/,\s*[A-Z]{2}(\s+\d{2,5})?\s*$/i.test(normalized)) {
    return 'Stany Zjednoczone';
  }
  return '';
}

function invoiceCountryLabel(codeOrName?: string, address?: string): string {
  const hint = (codeOrName ?? '').trim();
  if (hint) {
    if (hint.length === 2) {
      return INVOICE_COUNTRY_LABELS[hint.toUpperCase()] ?? hint.toUpperCase();
    }
    const alias = INVOICE_COUNTRY_NAME_ALIASES[hint.toLowerCase()];
    if (alias) return alias;
    const polishLabel = Object.values(INVOICE_COUNTRY_LABELS).find(
      (label) => label.toLowerCase() === hint.toLowerCase(),
    );
    if (polishLabel) return polishLabel;
    return hint;
  }

  const normalizedAddress = normalizeInvoiceAddress(address ?? '');

  const fromCity = inferCountryFromCity(normalizedAddress);
  if (fromCity) return fromCity;

  const fromUsAddress = inferUsCountryFromAddress(normalizedAddress);
  if (fromUsAddress) return fromUsAddress;

  const fromAddress = extractCountryFromAddress(normalizedAddress);
  if (fromAddress) {
    if (fromAddress.length === 2) {
      return INVOICE_COUNTRY_LABELS[fromAddress.toUpperCase()] ?? fromAddress.toUpperCase();
    }
    return fromAddress;
  }

  return '';
}

function addressIncludesCountry(address: string, country: string): boolean {
  const normalizedAddress = address.toLowerCase();
  const normalizedCountry = country.toLowerCase();
  if (normalizedAddress.includes(normalizedCountry)) return true;
  const codeEntry = Object.entries(INVOICE_COUNTRY_LABELS).find(([, label]) => label.toLowerCase() === normalizedCountry);
  if (codeEntry && normalizedAddress.includes(codeEntry[0].toLowerCase())) return true;
  const englishEntry = Object.entries(INVOICE_COUNTRY_NAME_ALIASES).find(([, label]) => label.toLowerCase() === normalizedCountry);
  if (englishEntry && normalizedAddress.includes(englishEntry[0])) return true;
  return false;
}

function ensureCountryInAddress(address: string, countryHint?: string): string {
  const trimmed = normalizeInvoiceAddress(address);
  const country = invoiceCountryLabel(countryHint, trimmed);
  if (!trimmed) return country || '—';
  if (!country) return trimmed;
  if (addressIncludesCountry(trimmed, country)) return trimmed;
  return `${trimmed}, ${country}`;
}

function formatSellerAddressForInvoice(address: string): string {
  return ensureCountryInAddress(address, 'Polska');
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

function shiftReportPeriod(month: number, year: number, deltaMonths: number): { month: number; year: number } {
  const date = new Date(year, month - 1 + deltaMonths, 1);
  return { month: date.getMonth() + 1, year: date.getFullYear() };
}

function findPrimaryReportId(
  periods: VatReportPeriod[],
  year: number,
  month: number
): number | null {
  const match = periods.find((period) => period.periodYear === year && period.periodMonth === month);
  return match?.primaryReportId ?? null;
}

function round2(value: number): number {
  return Math.round(value * 100) / 100;
}

function sanitizeDownloadFileName(fileName: string): string {
  return fileName.replace(/[\\/:*?"<>|]/g, '_').trim() || 'invoice.pdf';
}

function downloadBlobAsFile(blob: Blob, fileName: string): void {
  const safeName = sanitizeDownloadFileName(fileName);
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = safeName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

async function saveBlobWithDialog(blob: Blob, fileName: string): Promise<void> {
  const safeName = sanitizeDownloadFileName(fileName);
  const showSaveFilePicker = (
    window as Window & {
      showSaveFilePicker?: (options: {
        suggestedName?: string;
        types?: { description: string; accept: Record<string, string[]> }[];
      }) => Promise<FileSystemFileHandle>;
    }
  ).showSaveFilePicker;

  if (typeof showSaveFilePicker === 'function') {
    try {
      const handle = await showSaveFilePicker.call(window, {
        suggestedName: safeName,
        types: [{ description: 'PDF', accept: { 'application/pdf': ['.pdf'] } }],
      });
      const writable = await handle.createWritable();
      await writable.write(blob);
      await writable.close();
      return;
    } catch (err: unknown) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
    }
  }
  downloadBlobAsFile(blob, safeName);
}

function printHtmlAsPdf(html: string): void {
  const iframe = document.createElement('iframe');
  iframe.style.position = 'fixed';
  iframe.style.right = '0';
  iframe.style.bottom = '0';
  iframe.style.width = '0';
  iframe.style.height = '0';
  iframe.style.border = '0';
  iframe.setAttribute('aria-hidden', 'true');
  document.body.appendChild(iframe);

  const doc = iframe.contentDocument ?? iframe.contentWindow?.document;
  if (!doc) {
    iframe.remove();
    return;
  }

  doc.open();
  doc.write(html);
  doc.close();

  let printed = false;
  const printFromIframe = () => {
    if (printed) return;
    printed = true;
    iframe.contentWindow?.focus();
    iframe.contentWindow?.print();
    window.setTimeout(() => iframe.remove(), 500);
  };
  iframe.onload = printFromIframe;
  window.setTimeout(printFromIframe, 250);
}

function getInvoiceNumberForFile(raw: string | null | undefined, fallback: string): string {
  const source = String(raw ?? '').trim();
  const compact = source.replace(/\s+/g, '');
  const withoutHash = compact.replace(/^#/, '');
  const digitsOnly = withoutHash.replace(/\D/g, '');
  if (digitsOnly) return digitsOnly;
  if (withoutHash) return withoutHash;
  return fallback;
}

type ExpenseAmountField = 'gross' | 'vat' | 'net';

const SUPPLIER_PAYMENT_TYPE_NAME = 'Аплата пастаўшчыку';

const FOREIGN_COUNTRY_OPTIONS: Array<{ code: string; label: string }> = [
  { code: 'AT', label: 'Austria' },
  { code: 'BE', label: 'Belgium' },
  { code: 'BG', label: 'Bulgaria' },
  { code: 'HR', label: 'Croatia' },
  { code: 'CZ', label: 'Czechia' },
  { code: 'DK', label: 'Denmark' },
  { code: 'EE', label: 'Estonia' },
  { code: 'FI', label: 'Finland' },
  { code: 'FR', label: 'France' },
  { code: 'DE', label: 'Germany' },
  { code: 'GR', label: 'Greece' },
  { code: 'HU', label: 'Hungary' },
  { code: 'IE', label: 'Ireland' },
  { code: 'IT', label: 'Italy' },
  { code: 'LV', label: 'Latvia' },
  { code: 'LT', label: 'Lithuania' },
  { code: 'LU', label: 'Luxembourg' },
  { code: 'NL', label: 'Netherlands' },
  { code: 'PL', label: 'Poland' },
  { code: 'PT', label: 'Portugal' },
  { code: 'RO', label: 'Romania' },
  { code: 'SK', label: 'Slovakia' },
  { code: 'SI', label: 'Slovenia' },
  { code: 'ES', label: 'Spain' },
  { code: 'SE', label: 'Sweden' },
  { code: 'GB', label: 'United Kingdom' },
  { code: 'CH', label: 'Switzerland' },
  { code: 'NO', label: 'Norway' },
  { code: 'US', label: 'United States' },
  { code: 'CA', label: 'Canada' },
  { code: 'UA', label: 'Ukraine' },
  { code: 'BY', label: 'Belarus' },
];

type CatalogPickerLine = {
  lineKey: string;
  shopifyProductId: string;
  shopifyVariantId: string;
  productName: string;
  productAuthor: string;
  variantName: string;
  mainImageUrl: string | null;
};

type ExpensePickerLine = CatalogPickerLine & {
  vatRatePercent: number;
  supplierPrice: number;
};

type CashPickerLine = CatalogPickerLine;

type ExpenseProductLineDraft = {
  lineKey: string;
  shopifyProductId: string;
  shopifyVariantId: string;
  productTitle: string;
  productType: string;
  quantity: number;
  unitGrossPrice: number;
  vatRatePercent: number;
};

function visibleExpenseProductVariants(product: ProductWithSuppliers): ProductVariant[] {
  return (product.variants ?? []).filter(
    (variant) =>
      (variant.variantId?.trim() || variant.variantName?.trim()) &&
      variant.variantName !== 'Default Title'
  );
}

function buildCatalogPickerLines(catalogProducts: ProductWithSuppliers[]): CatalogPickerLine[] {
  const lines: CatalogPickerLine[] = [];
  for (const product of catalogProducts) {
    if (!product.shopifyProductId.trim()) continue;
    const variants = visibleExpenseProductVariants(product);
    const variantEntries =
      variants.length > 1
        ? variants
        : [
            {
              variantId: variants[0]?.variantId ?? '',
              variantName: variants[0]?.variantName ?? '',
              quantityInStock: variants[0]?.quantityInStock ?? 0,
            },
          ];

    for (const variant of variantEntries) {
      const variantId = variant.variantId?.trim() ?? '';
      lines.push({
        lineKey: makeSupplyLineKey(product.shopifyProductId, variantId),
        shopifyProductId: product.shopifyProductId,
        shopifyVariantId: variantId,
        productName: product.productName,
        productAuthor: product.productAuthor,
        variantName: variant.variantName,
        mainImageUrl: product.mainImageUrl,
      });
    }
  }

  return lines.sort((a, b) =>
    buildExpensePickerLineTitle(a).localeCompare(buildExpensePickerLineTitle(b), 'be')
  );
}

function normalizeShopifyNumericId(id: string): string {
  const trimmed = id.trim();
  if (!trimmed) return '';
  const gidMatch = trimmed.match(/\/(\d+)$/);
  if (gidMatch) return gidMatch[1];
  return trimmed;
}

function findCatalogProduct(
  catalogProducts: ProductWithSuppliers[],
  shopifyProductId: string
): ProductWithSuppliers | undefined {
  const target = normalizeShopifyNumericId(shopifyProductId);
  return catalogProducts.find(
    (product) => normalizeShopifyNumericId(product.shopifyProductId) === target
  );
}

function stripSuffixFromTitle(title: string, suffix?: string | null): string {
  const trimmedSuffix = suffix?.trim();
  if (!trimmedSuffix) return title.trim();
  const escaped = trimmedSuffix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return title.trim().replace(new RegExp(`[,\\s]+${escaped}$`, 'iu'), '').trim();
}

function buildExpensePickerLineTitle(line: {
  productName: string;
  productAuthor: string;
  variantName: string;
}): string {
  const base = formatProductNameWithAuthor(line.productName, line.productAuthor);
  const variantName = line.variantName.trim();
  return variantName ? `${base} — ${variantName}` : base;
}

function extractPinVariantFromTitle(productTitle: string): string {
  const trimmed = productTitle.trim();
  const dotSep = trimmed.lastIndexOf(' · ');
  if (dotSep < 0) return '';
  const variant = trimmed.slice(dotSep + 3).trim();
  return variant && variant !== 'Default Title' ? variant : '';
}

function resolveUnpaidVariantName(
  item: Pick<VatReportUnpaidProductRow, 'shopifyProductId' | 'shopifyVariantId' | 'shopifyVariantTitle' | 'productTitle'>,
  catalogProducts: ProductWithSuppliers[]
): string {
  const fromApi = item.shopifyVariantTitle?.trim() ?? '';
  if (fromApi && fromApi !== 'Default Title') return fromApi;

  const fromTitle = extractPinVariantFromTitle(item.productTitle);
  if (fromTitle) return fromTitle;

  const variantId = item.shopifyVariantId?.trim() ?? '';
  if (!variantId) return '';

  const product = findCatalogProduct(catalogProducts, item.shopifyProductId);
  const targetVariantId = normalizeShopifyNumericId(variantId);
  const variantName =
    product?.variants
      ?.find((variant) => normalizeShopifyNumericId(variant.variantId) === targetVariantId)
      ?.variantName?.trim() ?? '';
  return variantName && variantName !== 'Default Title' ? variantName : '';
}

function formatUnpaidProductLabel(
  item: VatReportUnpaidProductRow,
  catalogProducts: ProductWithSuppliers[] = []
): { title: string; variantLabel: string | null } {
  const variantName = resolveUnpaidVariantName(item, catalogProducts);
  const product = findCatalogProduct(catalogProducts, item.shopifyProductId);
  const title =
    product?.productName?.trim() ||
    stripSuffixFromTitle(
      stripSuffixFromTitle(item.productTitle, item.supplierName),
      product?.productAuthor
    ) ||
    item.shopifyProductId;

  return { title, variantLabel: variantName || null };
}

function formatOrderItemLabel(item: {
  productTitle: string;
  variantTitle?: string;
  shopifyVariantId?: string;
}): { title: string; variantLabel: string | null } {
  const fromApi = item.variantTitle?.trim() ?? '';
  if (fromApi && fromApi !== 'Default Title') {
    const title = item.productTitle.includes(' — ')
      ? item.productTitle.split(' — ')[0]?.trim() || item.productTitle
      : item.productTitle;
    return { title, variantLabel: fromApi };
  }

  const dashIndex = item.productTitle.lastIndexOf(' — ');
  if (dashIndex >= 0) {
    const title = item.productTitle.slice(0, dashIndex).trim();
    const variantLabel = item.productTitle.slice(dashIndex + 3).trim();
    if (title && variantLabel) {
      return { title, variantLabel };
    }
  }

  return { title: item.productTitle, variantLabel: null };
}

function buildExpensePickerLinesFromSupply(
  catalogProducts: ProductWithSuppliers[],
  supplyRows: SupplyCatalogProduct[]
): ExpensePickerLine[] {
  const catalogById = new Map(catalogProducts.map((product) => [product.shopifyProductId, product]));

  return supplyRows
    .map((row) => {
      const product = catalogById.get(row.shopifyProductId);
      const variantId = row.shopifyVariantId?.trim() ?? '';
      const variantName =
        variantId && product
          ? (product.variants?.find((variant) => variant.variantId === variantId)?.variantName ?? '')
          : '';
      return {
        lineKey: makeSupplyLineKey(row.shopifyProductId, variantId),
        shopifyProductId: row.shopifyProductId,
        shopifyVariantId: variantId,
        productName: product?.productName || row.productName || row.shopifyProductId,
        productAuthor: product?.productAuthor ?? '',
        variantName,
        mainImageUrl: product?.mainImageUrl ?? null,
        vatRatePercent: row.vatRatePercent,
        supplierPrice: row.supplierPrice,
      };
    })
    .sort((a, b) =>
      buildExpensePickerLineTitle(a).localeCompare(buildExpensePickerLineTitle(b), 'be')
    );
}

function buildExpensePickerLinesFromCatalog(
  catalogProducts: ProductWithSuppliers[],
  supplyRows: SupplyCatalogProduct[]
): ExpensePickerLine[] {
  const supplyByLineKey = new Map(
    supplyRows.map((row) => [
      makeSupplyLineKey(row.shopifyProductId, row.shopifyVariantId),
      { vatRatePercent: row.vatRatePercent, supplierPrice: row.supplierPrice },
    ])
  );

  return buildCatalogPickerLines(catalogProducts).map((line) => {
    const supply = supplyByLineKey.get(line.lineKey);
    return {
      ...line,
      vatRatePercent: supply?.vatRatePercent ?? 23,
      supplierPrice: supply?.supplierPrice ?? 0,
    };
  });
}

function defaultExpenseDateInput(): string {
  return new Date().toISOString().slice(0, 10);
}

function syncExpenseAmounts(
  values: { grossAmount: number; vatAmount: number; netAmount: number },
  edited: ExpenseAmountField
): { grossAmount: number; vatAmount: number; netAmount: number } {
  const gross = values.grossAmount;
  const vat = values.vatAmount;
  const net = values.netAmount;
  if (edited === 'gross') {
    if (vat > 0) return { grossAmount: gross, vatAmount: vat, netAmount: round2(gross - vat) };
    if (net > 0) return { grossAmount: gross, vatAmount: round2(gross - net), netAmount: net };
    return values;
  }
  if (edited === 'vat') {
    if (gross > 0) return { grossAmount: gross, vatAmount: vat, netAmount: round2(gross - vat) };
    if (net > 0) return { grossAmount: round2(vat + net), vatAmount: vat, netAmount: net };
    return values;
  }
  if (gross > 0) return { grossAmount: gross, vatAmount: round2(gross - net), netAmount: net };
  if (vat > 0) return { grossAmount: round2(vat + net), vatAmount: vat, netAmount: net };
  return values;
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

function calcExpenseProductGrossTotal(lines: ExpenseProductLineDraft[]): number {
  return round2(lines.reduce((sum, line) => sum + line.quantity * line.unitGrossPrice, 0));
}

function calcExpenseProductVatTotal(
  lines: ExpenseProductLineDraft[],
  supplierId: number,
  supplierIsVatPayer: boolean
): number {
  if (supplierId > 0 && !supplierIsVatPayer) {
    return 0;
  }

  return round2(
    lines.reduce((sum, line) => {
      const lineGross = line.quantity * line.unitGrossPrice;
      if (lineGross <= 0) return sum;
      const rate = line.vatRatePercent;
      if (!Number.isFinite(rate) || rate <= 0) return sum;
      return sum + round2((lineGross * (rate / 100)) / (1 + rate / 100));
    }, 0)
  );
}

function buildSupplierPaymentAmounts(
  grossAmount: number,
  vatAmount: number
): { grossAmount: number; vatAmount: number; netAmount: number } {
  const gross = round2(grossAmount);
  const vat = round2(Math.max(0, Math.min(vatAmount, gross)));
  const net = round2(gross - vat);
  return { grossAmount: gross, vatAmount: vat, netAmount: net };
}

export default function ReportDetailsClient({ reportId }: { reportId: number }) {
  const router = useRouter();
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
  const [expenseModalOpen, setExpenseModalOpen] = useState(false);
  const [expenseTypes, setExpenseTypes] = useState<ExpenseInvoiceType[]>([]);
  const [expenseSaving, setExpenseSaving] = useState(false);
  const [deletingExpenseId, setDeletingExpenseId] = useState<number | null>(null);
  const [newExpense, setNewExpense] = useState({
    grossAmount: 0,
    vatAmount: 0,
    netAmount: 0,
    expenseDateUtc: defaultExpenseDateInput(),
    comment: '',
    invoiceNumber: '',
    isPaid: false,
    isByProsvet: false,
    includeVatInTotal: true,
    expenseInvoiceTypeId: 0,
  });
  const [expenseInvoiceFile, setExpenseInvoiceFile] = useState<File | null>(null);
  const [expenseSuppliers, setExpenseSuppliers] = useState<Supplier[]>([]);
  const [expenseSupplierId, setExpenseSupplierId] = useState(0);
  const [expenseProductLines, setExpenseProductLines] = useState<ExpenseProductLineDraft[]>([]);
  const [supplierProducts, setSupplierProducts] = useState<ExpensePickerLine[]>([]);
  const [supplierProductsLoading, setSupplierProductsLoading] = useState(false);
  const [expenseProductSearch, setExpenseProductSearch] = useState('');
  const [expenseGrossOverride, setExpenseGrossOverride] = useState<number | null>(null);
  const [expenseVatOverride, setExpenseVatOverride] = useState<number | null>(null);
  const [editingExpenseId, setEditingExpenseId] = useState<number | null>(null);
  const [editingExpenseInvoiceFileName, setEditingExpenseInvoiceFileName] = useState<string | null>(null);
  const [addMode, setAddMode] = useState<'select' | 'manual'>('select');
  const [sourceOrderOptions, setSourceOrderOptions] = useState<VatReportSourceOrderOption[]>([]);
  const [sourceOrdersLoading, setSourceOrdersLoading] = useState(false);
  const [selectedSourceIndex, setSelectedSourceIndex] = useState('');
  const [addingRow, setAddingRow] = useState(false);
  const [addRowError, setAddRowError] = useState<string | null>(null);
  const [orderSearch, setOrderSearch] = useState('');
  const [foreignOrderSearch, setForeignOrderSearch] = useState('');
  const [foreignAddModalOpen, setForeignAddModalOpen] = useState(false);
  const [foreignAddSaving, setForeignAddSaving] = useState(false);
  const [foreignAddError, setForeignAddError] = useState<string | null>(null);
  const [foreignProductLines, setForeignProductLines] = useState<ExpenseProductLineDraft[]>([]);
  const [foreignShopifyProducts, setForeignShopifyProducts] = useState<ProductWithSuppliers[]>([]);
  const [foreignShopifyProductsLoading, setForeignShopifyProductsLoading] = useState(false);
  const [foreignProductSearch, setForeignProductSearch] = useState('');
  const [newForeignRow, setNewForeignRow] = useState({
    orderNumber: '',
    orderDateUtc: defaultExpenseDateInput(),
    deliveryName: '',
    deliveryAddress: '',
    countryCode: 'AT',
    shippingGrossAmount: 0,
  });
  const [expenseSearch, setExpenseSearch] = useState('');
  const [cashModalOpen, setCashModalOpen] = useState(false);
  const [cashSaving, setCashSaving] = useState(false);
  const [deletingCashSaleId, setDeletingCashSaleId] = useState<number | null>(null);
  const [cashProducts, setCashProducts] = useState<ProductWithSuppliers[]>([]);
  const [cashProductsLoading, setCashProductsLoading] = useState(false);
  const [unpaidCatalogProducts, setUnpaidCatalogProducts] = useState<ProductWithSuppliers[]>([]);
  const [cashProductSearch, setCashProductSearch] = useState('');
  const [unpaidLinkModalOpen, setUnpaidLinkModalOpen] = useState(false);
  const [unpaidLinkTarget, setUnpaidLinkTarget] = useState<VatReportUnpaidProductRow | null>(null);
  const [unpaidLinkMode, setUnpaidLinkMode] = useState<'replace' | 'link'>('replace');
  const [unpaidLinkOptions, setUnpaidLinkOptions] = useState<VatReportUnpaidLinkOptions | null>(null);
  const [unpaidLinkOptionsLoading, setUnpaidLinkOptionsLoading] = useState(false);
  const [unpaidLinkError, setUnpaidLinkError] = useState<string | null>(null);
  const [unpaidLinkSaving, setUnpaidLinkSaving] = useState(false);
  const [selectedOverpaidProductId, setSelectedOverpaidProductId] = useState('');
  const [unpaidLinkSource, setUnpaidLinkSource] = useState<'invoice' | 'payment'>('invoice');
  const [selectedInvoiceExpenseId, setSelectedInvoiceExpenseId] = useState('');
  const [selectedPaymentRecordExpenseId, setSelectedPaymentRecordExpenseId] = useState('');
  const [newCashSale, setNewCashSale] = useState({
    lineKey: '',
    shopifyProductId: '',
    shopifyVariantId: '',
    productTitle: '',
    quantity: 1,
    unitPrice: 0,
  });
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
  const detailsPanelRef = useRef<HTMLDivElement | null>(null);
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

  const loadCombinedDetails = (baseReportId: number) => fetchVatReportCombinedDetails(baseReportId);
  const [reportPeriods, setReportPeriods] = useState<VatReportPeriod[]>([]);
  const [lockingReport, setLockingReport] = useState(false);

  const handleToggleLock = useCallback(async () => {
    if (!data || lockingReport) return;

    const nextLocked = !data.isLocked;
    setLockingReport(true);
    setError(null);
    try {
      await setVatReportLocked(reportId, nextLocked);
      setData((prev) => (prev ? { ...prev, isLocked: nextLocked } : prev));
      setReportPeriods((prev) =>
        prev.map((period) =>
          period.periodYear === data.periodYear && period.periodMonth === data.periodMonth
            ? {
                ...period,
                isLocked: nextLocked,
                reports: period.reports.map((report) => ({ ...report, isLocked: nextLocked })),
              }
            : period
        )
      );
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка змены блакавання');
    } finally {
      setLockingReport(false);
    }
  }, [data, lockingReport, reportId]);

  const adjacentReportIds = useMemo(() => {
    if (!data) {
      return { prev: null as number | null, next: null as number | null };
    }

    const previous = shiftReportPeriod(data.periodMonth, data.periodYear, -1);
    const next = shiftReportPeriod(data.periodMonth, data.periodYear, 1);
    return {
      prev: findPrimaryReportId(reportPeriods, previous.year, previous.month),
      next: findPrimaryReportId(reportPeriods, next.year, next.month),
    };
  }, [data, reportPeriods]);

  useEffect(() => {
    let cancelled = false;
    fetchVatReportPeriods()
      .then((rows) => {
        if (!cancelled) setReportPeriods(rows);
      })
      .catch(() => {
        if (!cancelled) setReportPeriods([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const monthYearTitle = data ? formatMonthYearBe(data.periodMonth, data.periodYear) : 'Справаздача';
    setTopbarPage({ title: monthYearTitle });
    setTopbarButtons([
      {
        label: 'Да справаздач',
        icon: <FiArrowLeft />,
        onClick: () => router.push('/documents'),
        variant: 'secondary',
        iconOnly: true,
        position: 'left',
      },
      {
        label: 'Папярэдні месяц',
        icon: <FiChevronLeft />,
        onClick: adjacentReportIds.prev
          ? () => router.push(`/documents/reports/${adjacentReportIds.prev}`)
          : undefined,
        variant: 'secondary',
        iconOnly: true,
        position: 'left',
        disabled: !adjacentReportIds.prev,
      },
      {
        label: 'Наступны месяц',
        icon: <FiChevronRight />,
        onClick: adjacentReportIds.next
          ? () => router.push(`/documents/reports/${adjacentReportIds.next}`)
          : undefined,
        variant: 'secondary',
        iconOnly: true,
        position: 'left',
        disabled: !adjacentReportIds.next,
      },
    ]);
    return () => {
      setTopbarButtons([]);
      setTopbarPage(null);
    };
  }, [adjacentReportIds.next, adjacentReportIds.prev, data, router, setTopbarButtons, setTopbarPage]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setExpandedOrderId(null);
    setExpandedForeignOrderId(null);
    setExpenseSearch('');
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

  useEffect(() => {
    let cancelled = false;
    fetchExpenseInvoiceTypes()
      .then((rows) => {
        if (cancelled) return;
        setExpenseTypes(rows);
        setNewExpense((prev) => ({
          ...prev,
          expenseInvoiceTypeId: prev.expenseInvoiceTypeId || rows[0]?.id || 0,
        }));
      })
      .catch(() => {
        if (!cancelled) setExpenseTypes([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const isSupplierPaymentExpense = useMemo(() => {
    const selectedType = expenseTypes.find((t) => t.id === newExpense.expenseInvoiceTypeId);
    return selectedType?.name === SUPPLIER_PAYMENT_TYPE_NAME;
  }, [expenseTypes, newExpense.expenseInvoiceTypeId]);

  const expenseProductGrossTotal = useMemo(
    () => calcExpenseProductGrossTotal(expenseProductLines),
    [expenseProductLines]
  );

  const expenseSupplierIsVatPayer = useMemo(() => {
    if (expenseSupplierId <= 0) return false;
    return expenseSuppliers.find((supplier) => supplier.id === expenseSupplierId)?.isVatPayer ?? false;
  }, [expenseSupplierId, expenseSuppliers]);

  const expenseProductVatTotal = useMemo(
    () => calcExpenseProductVatTotal(expenseProductLines, expenseSupplierId, expenseSupplierIsVatPayer),
    [expenseProductLines, expenseSupplierId, expenseSupplierIsVatPayer]
  );

  const supplierPaymentComputedAmounts = useMemo(() => {
    if (!isSupplierPaymentExpense) return null;
    const gross = round2(Math.max(expenseProductGrossTotal, expenseGrossOverride ?? expenseProductGrossTotal));
    const vat = round2(
      Math.max(0, Math.min(gross, expenseVatOverride ?? expenseProductVatTotal))
    );
    return buildSupplierPaymentAmounts(gross, vat);
  }, [
    isSupplierPaymentExpense,
    expenseProductGrossTotal,
    expenseGrossOverride,
    expenseProductVatTotal,
    expenseVatOverride,
  ]);

  useEffect(() => {
    if (!expenseModalOpen || !isSupplierPaymentExpense) return;
    let cancelled = false;
    fetchSuppliers()
      .then((rows) => {
        if (!cancelled) setExpenseSuppliers(rows);
      })
      .catch(() => {
        if (!cancelled) setExpenseSuppliers([]);
      });
    return () => {
      cancelled = true;
    };
  }, [expenseModalOpen, isSupplierPaymentExpense]);

  useEffect(() => {
    if (!expenseModalOpen || !isSupplierPaymentExpense) return;
    let cancelled = false;
    setSupplierProductsLoading(true);

    const loadExpensePickerProducts = async (): Promise<ExpensePickerLine[]> => {
      const catalogProducts = await fetchProductsWithSuppliers();
      let supplyRows: SupplyCatalogProduct[] = [];
      try {
        supplyRows = await fetchSupplyCatalogProducts(
          expenseSupplierId > 0 ? expenseSupplierId : undefined
        );
      } catch {
        supplyRows = [];
      }

      if (expenseSupplierId > 0) {
        return buildExpensePickerLinesFromSupply(catalogProducts, supplyRows);
      }

      return buildExpensePickerLinesFromCatalog(catalogProducts, supplyRows);
    };

    loadExpensePickerProducts()
      .then((rows) => {
        if (!cancelled) setSupplierProducts(rows);
      })
      .catch(() => {
        if (!cancelled) setSupplierProducts([]);
      })
      .finally(() => {
        if (!cancelled) setSupplierProductsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [expenseModalOpen, isSupplierPaymentExpense, expenseSupplierId]);

  useEffect(() => {
    if (!foreignAddModalOpen) return;
    let cancelled = false;
    setForeignShopifyProductsLoading(true);
    fetchProductsWithSuppliers()
      .then((rows) => {
        if (!cancelled) setForeignShopifyProducts(rows);
      })
      .catch(() => {
        if (!cancelled) setForeignShopifyProducts([]);
      })
      .finally(() => {
        if (!cancelled) setForeignShopifyProductsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [foreignAddModalOpen]);

  useEffect(() => {
    if (expenseGrossOverride !== null && expenseGrossOverride < expenseProductGrossTotal) {
      setExpenseGrossOverride(null);
    }
  }, [expenseProductGrossTotal, expenseGrossOverride]);

  useEffect(() => {
    if (!isSupplierPaymentExpense || !supplierPaymentComputedAmounts) return;
    setNewExpense((prev) => ({
      ...prev,
      ...supplierPaymentComputedAmounts,
    }));
  }, [isSupplierPaymentExpense, supplierPaymentComputedAmounts]);

  useEffect(() => {
    if (!cashModalOpen) return;
    let cancelled = false;
    setCashProductsLoading(true);
    fetchProductsWithSuppliers()
      .then((rows) => {
        if (!cancelled) setCashProducts(rows);
      })
      .catch(() => {
        if (!cancelled) setCashProducts([]);
      })
      .finally(() => {
        if (!cancelled) setCashProductsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [cashModalOpen]);

  const cashPickerLines = useMemo(() => buildCatalogPickerLines(cashProducts), [cashProducts]);

  const visibleCashPickerLines = useMemo(() => {
    const search = cashProductSearch.trim().toLowerCase();
    if (!search) return cashPickerLines;
    return cashPickerLines.filter((line) => {
      const haystack = [
        line.productName,
        line.productAuthor,
        line.variantName,
        buildExpensePickerLineTitle(line),
      ]
        .join(' ')
        .toLowerCase();
      return haystack.includes(search);
    });
  }, [cashPickerLines, cashProductSearch]);

  const visibleSupplierProducts = useMemo(() => {
    const search = expenseProductSearch.trim().toLowerCase();
    if (!search) return supplierProducts;
    return supplierProducts.filter((line) => {
      const haystack = [
        line.productName,
        line.productAuthor,
        line.variantName,
        buildExpensePickerLineTitle(line),
      ]
        .join(' ')
        .toLowerCase();
      return haystack.includes(search);
    });
  }, [supplierProducts, expenseProductSearch]);

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

  const activeDetailsPanel = useMemo<'poland' | 'foreign' | 'expense' | 'cash' | 'unpaid' | null>(() => {
    if (isForeignReportOnly) {
      return expandedOrderId ? 'foreign' : null;
    }
    if (!expandedRow) return null;
    if (expandedRow.type === 'poland') return 'poland';
    if (expandedRow.type === 'foreign' && foreignOrderRows.length > 0) return 'foreign';
    if (expandedRow.type === 'cash') return 'cash';
    if (expandedRow.type === 'expense') return 'expense';
    if (expandedRow.type === 'unpaid') return 'unpaid';
    return null;
  }, [expandedRow, expandedOrderId, foreignOrderRows.length, isForeignReportOnly]);

  const visibleCashRows = useMemo(() => {
    if (!expandedRow || expandedRow.type !== 'cash') return [];
    return expandedRow.cashSaleRows ?? [];
  }, [expandedRow]);

  const visibleUnpaidRows = useMemo(() => {
    if (!expandedRow || expandedRow.type !== 'unpaid') return [];
    return expandedRow.unpaidProductRows ?? [];
  }, [expandedRow]);

  const unpaidCatalogKey = useMemo(
    () =>
      visibleUnpaidRows
        .map((row) => `${row.shopifyProductId}:${row.shopifyVariantId ?? ''}`)
        .join('|'),
    [visibleUnpaidRows]
  );

  useEffect(() => {
    if (visibleUnpaidRows.length === 0) return;
    let cancelled = false;
    fetchProductsWithSuppliers()
      .then((rows) => {
        if (!cancelled) setUnpaidCatalogProducts(rows);
      })
      .catch(() => {
        if (!cancelled) setUnpaidCatalogProducts([]);
      });
    return () => {
      cancelled = true;
    };
  }, [unpaidCatalogKey, visibleUnpaidRows.length]);

  const expenseVatForTotal = useMemo(() => {
    const expenseRow = data?.rows.find((r) => r.type === 'expense');
    if (!expenseRow) return 0;
    const expenseRows = expenseRow.expenseRows ?? [];
    if (expenseRows.length > 0) {
      return round2(
        expenseRows
          .filter((row) => row.includeVatInTotal)
          .reduce((sum, row) => sum + row.vatAmount, 0)
      );
    }
    return expenseRow.vat ?? 0;
  }, [data]);

  const baseTotalVat = useMemo(() => {
    if (!data?.rows.length) return data?.vat ?? 0;
    const vatOf = (type: 'poland' | 'foreign') =>
      data.rows.find((r) => r.type === type)?.vat ?? 0;
    return round2(vatOf('poland') + vatOf('foreign') - expenseVatForTotal);
  }, [data, expenseVatForTotal]);

  const displayTotalVat = useMemo(() => {
    if (!data) return 0;
    if (expandedRow?.type !== 'poland') return baseTotalVat;

    let delta = 0;
    expandedRow.polandRows.forEach((row) => {
      const rowKey = String(row.id);
      const edited = editedRows[rowKey];
      if (!edited) return;
      delta += edited.vatAmount - row.vatAmount;
    });
    return round2(baseTotalVat + delta);
  }, [data, expandedRow, editedRows, baseTotalVat]);

  const displayProfit = useMemo(() => data?.profit ?? 0, [data?.profit]);

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

  const visibleExpenseRows = useMemo(() => {
    if (!expandedRow || expandedRow.type !== 'expense') return [];
    const search = expenseSearch.trim().toLowerCase();
    return (expandedRow.expenseRows ?? []).filter((expense) => {
      if (!search) return true;
      return (
        expense.expenseInvoiceTypeName.toLowerCase().includes(search) ||
        expense.comment.toLowerCase().includes(search) ||
        expense.supplierName.toLowerCase().includes(search) ||
        expense.products.some((product) => product.productTitle.toLowerCase().includes(search))
      );
    });
  }, [expandedRow, expenseSearch]);

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

  const foreignPickerLines = useMemo(
    () => buildCatalogPickerLines(foreignShopifyProducts),
    [foreignShopifyProducts]
  );

  const visibleForeignPickerLines = useMemo(() => {
    const search = foreignProductSearch.trim().toLowerCase();
    if (!search) return foreignPickerLines;
    return foreignPickerLines.filter((line) => {
      const haystack = [
        line.productName,
        line.productAuthor,
        line.variantName,
        buildExpensePickerLineTitle(line),
      ]
        .join(' ')
        .toLowerCase();
      return haystack.includes(search);
    });
  }, [foreignPickerLines, foreignProductSearch]);

  const foreignProductGrossTotal = useMemo(
    () => calcExpenseProductGrossTotal(foreignProductLines),
    [foreignProductLines]
  );

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

  const formatSourceOrderLabel = (option: VatReportSourceOrderOption) => {
    const baseNumber = option.orderNumber.split(' || ')[0]?.trim() || option.orderNumber;
    return `${baseNumber} · ${formatDate(option.orderDateUtc)} · VAT ${formatAmount(option.vatRatePercent)}%`;
  };

  const resetNewRow = () => {
    setNewRow({
      orderNumber: '',
      orderDateUtc: '',
      vatRatePercent: 23,
      grossAmount: 0,
      vatAmount: 0,
      netAmount: 0,
    });
    setSelectedSourceIndex('');
    setAddRowError(null);
  };

  const openAddModal = async () => {
    if (data?.isLocked) return;
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

  const resetForeignAddForm = () => {
    setNewForeignRow({
      orderNumber: '',
      orderDateUtc: defaultExpenseDateInput(),
      deliveryName: '',
      deliveryAddress: '',
      countryCode: 'AT',
      shippingGrossAmount: 0,
    });
    setForeignProductLines([]);
    setForeignProductSearch('');
    setForeignAddError(null);
  };

  const openForeignAddModal = () => {
    if (data?.isLocked) return;
    resetForeignAddForm();
    setForeignAddModalOpen(true);
  };

  const toggleForeignPickerLine = (line: CatalogPickerLine, selected: boolean) => {
    const product = foreignShopifyProducts.find((p) => p.shopifyProductId === line.shopifyProductId);
    if (selected) {
      setForeignProductLines((prev) => {
        if (prev.some((item) => item.lineKey === line.lineKey)) return prev;
        return [
          ...prev,
          {
            lineKey: line.lineKey,
            shopifyProductId: line.shopifyProductId,
            shopifyVariantId: line.shopifyVariantId,
            productTitle: buildExpensePickerLineTitle(line),
            productType: product?.productType ?? '',
            quantity: 1,
            unitGrossPrice: 0,
            vatRatePercent: 23,
          },
        ];
      });
      return;
    }
    setForeignProductLines((prev) => prev.filter((item) => item.lineKey !== line.lineKey));
  };

  const updateForeignProductQuantity = (lineKey: string, quantity: number) => {
    const safeQuantity = Math.max(1, Math.trunc(quantity) || 1);
    setForeignProductLines((prev) =>
      prev.map((line) => (line.lineKey === lineKey ? { ...line, quantity: safeQuantity } : line))
    );
  };

  const updateForeignProductUnitPrice = (lineKey: string, unitGrossPrice: number) => {
    const safePrice = Math.max(0, unitGrossPrice);
    setForeignProductLines((prev) =>
      prev.map((line) => (line.lineKey === lineKey ? { ...line, unitGrossPrice: safePrice } : line))
    );
  };

  const submitForeignRow = async () => {
    setForeignAddError(null);
    const orderNumber = newForeignRow.orderNumber.trim();
    const orderDateUtc = newForeignRow.orderDateUtc.trim();
    const deliveryName = newForeignRow.deliveryName.trim();
    const deliveryAddress = newForeignRow.deliveryAddress.trim();
    if (!orderNumber) {
      setForeignAddError('Нумар замовы абавязковы.');
      return;
    }
    if (!orderDateUtc) {
      setForeignAddError('Дата замовы абавязковая.');
      return;
    }
    if (!deliveryName) {
      setForeignAddError('Увядзіце імя атрымальніка.');
      return;
    }
    if (!deliveryAddress) {
      setForeignAddError('Увядзіце адрас дастаўкі.');
      return;
    }
    if (foreignProductLines.length === 0) {
      setForeignAddError('Дадайце хаця б адзін тавар.');
      return;
    }
    if (foreignProductLines.some((line) => !Number.isFinite(line.unitGrossPrice) || line.unitGrossPrice <= 0)) {
      setForeignAddError('Укажыце брута-цэну для кожнага тавару.');
      return;
    }

    setForeignAddSaving(true);
    try {
      const shopifyOrderId = await createVatReportForeignRow(reportId, {
        orderNumber,
        orderDateUtc: new Date(`${orderDateUtc}T12:00:00`).toISOString(),
        deliveryName,
        deliveryAddress,
        countryCode: newForeignRow.countryCode,
        shippingGrossAmount: Math.max(0, Number(newForeignRow.shippingGrossAmount) || 0),
        items: foreignProductLines.map((line) => ({
          shopifyProductId: line.shopifyProductId,
          shopifyVariantId: line.shopifyVariantId,
          variantTitle: line.productTitle.includes(' — ')
            ? line.productTitle.split(' — ').slice(1).join(' — ').trim()
            : '',
          productTitle: line.productTitle,
          productType: line.productType,
          quantity: line.quantity,
          unitPrice: line.unitGrossPrice,
        })),
      });
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
      setForeignAddModalOpen(false);
      resetForeignAddForm();
      if (isForeignReportOnly) {
        setExpandedOrderId(shopifyOrderId);
      } else {
        setExpandedOrderId('foreign-summary');
        setExpandedForeignOrderId(shopifyOrderId);
      }
    } catch (err: unknown) {
      setForeignAddError(err instanceof Error ? err.message : 'Памылка дадання замежнага радка');
    } finally {
      setForeignAddSaving(false);
    }
  };

  const resetNewExpenseForm = () => {
    setNewExpense({
      grossAmount: 0,
      vatAmount: 0,
      netAmount: 0,
      expenseDateUtc: defaultExpenseDateInput(),
      comment: '',
      invoiceNumber: '',
    isPaid: false,
    isByProsvet: false,
    includeVatInTotal: true,
    expenseInvoiceTypeId: expenseTypes[0]?.id || 0,
  });
    setExpenseInvoiceFile(null);
    setExpenseSupplierId(0);
    setExpenseProductLines([]);
    setExpenseProductSearch('');
    setSupplierProducts([]);
    setExpenseGrossOverride(null);
    setExpenseVatOverride(null);
    setEditingExpenseId(null);
    setEditingExpenseInvoiceFileName(null);
  };

  const openExpenseForEdit = async (expense: VatReportExpenseRow) => {
    if (isLocked) return;

    let suppliers = expenseSuppliers;
    if (suppliers.length === 0) {
      try {
        suppliers = await fetchSuppliers();
        setExpenseSuppliers(suppliers);
      } catch {
        suppliers = [];
      }
    }

    const supplierId = expense.supplierId ?? 0;
    const supplierIsVatPayer = suppliers.find((supplier) => supplier.id === supplierId)?.isVatPayer ?? false;
    const lines: ExpenseProductLineDraft[] = expense.products.map((product) => ({
      lineKey: makeSupplyLineKey(product.shopifyProductId, product.shopifyVariantId),
      shopifyProductId: product.shopifyProductId,
      shopifyVariantId: product.shopifyVariantId ?? '',
      productTitle: product.productTitle,
      productType: '',
      quantity: product.quantity,
      unitGrossPrice: product.unitGrossPrice,
      vatRatePercent: 23,
    }));
    const productGross = calcExpenseProductGrossTotal(lines);
    const calcVat = calcExpenseProductVatTotal(lines, supplierId, supplierIsVatPayer);

    setEditingExpenseId(expense.id);
    setEditingExpenseInvoiceFileName(expense.invoiceFileName || null);
    setExpenseSupplierId(supplierId);
    setExpenseProductLines(lines);
    setExpenseProductSearch('');
    setExpenseGrossOverride(expense.grossAmount > productGross + 0.009 ? expense.grossAmount : null);
    setExpenseVatOverride(Math.abs(expense.vatAmount - calcVat) > 0.009 ? expense.vatAmount : null);
    setNewExpense({
      grossAmount: expense.grossAmount,
      vatAmount: expense.vatAmount,
      netAmount: expense.netAmount,
      expenseDateUtc: toDateInputValue(expense.expenseDateUtc),
      comment: expense.comment,
      invoiceNumber: expense.invoiceNumber,
      isPaid: expense.isPaid,
      isByProsvet: expense.isByProsvet,
      includeVatInTotal: expense.includeVatInTotal,
      expenseInvoiceTypeId: expense.expenseInvoiceTypeId,
    });
    setExpenseInvoiceFile(null);
    setExpenseModalOpen(true);
  };

  const toggleExpenseProduct = (pickerLine: ExpensePickerLine, selected: boolean) => {
    if (selected) {
      const defaultUnitGrossPrice =
        pickerLine.supplierPrice > 0 ? round2(pickerLine.supplierPrice) : 0;
      setExpenseProductLines((prev) => {
        if (prev.some((line) => line.lineKey === pickerLine.lineKey)) return prev;
        return [
          ...prev,
          {
            lineKey: pickerLine.lineKey,
            shopifyProductId: pickerLine.shopifyProductId,
            shopifyVariantId: pickerLine.shopifyVariantId,
            productTitle: buildExpensePickerLineTitle(pickerLine),
            productType: '',
            quantity: 1,
            unitGrossPrice: defaultUnitGrossPrice,
            vatRatePercent: pickerLine.vatRatePercent,
          },
        ];
      });
      return;
    }
    setExpenseProductLines((prev) => prev.filter((line) => line.lineKey !== pickerLine.lineKey));
  };

  const updateExpenseProductQuantity = (lineKey: string, quantity: number) => {
    const safeQuantity = Math.max(1, Math.trunc(quantity) || 1);
    setExpenseProductLines((prev) =>
      prev.map((line) => (line.lineKey === lineKey ? { ...line, quantity: safeQuantity } : line))
    );
  };

  const updateExpenseProductUnitGrossPrice = (lineKey: string, unitGrossPrice: number) => {
    const safePrice = Math.max(0, unitGrossPrice);
    setExpenseProductLines((prev) =>
      prev.map((line) => (line.lineKey === lineKey ? { ...line, unitGrossPrice: safePrice } : line))
    );
  };

  const submitExpense = async () => {
    if (!newExpense.expenseInvoiceTypeId) {
      setError('Выберыце тып расходу.');
      return;
    }
    if (newExpense.grossAmount < 0 || newExpense.vatAmount < 0 || newExpense.netAmount < 0) {
      setError('Сумы не могуць быць адмоўнымі.');
      return;
    }
    if (isSupplierPaymentExpense) {
      if (expenseProductLines.length === 0) {
        setError('Дадайце хаця б адзін тавар з колькасцю.');
        return;
      }
      if (expenseProductLines.some((line) => !Number.isFinite(line.unitGrossPrice) || line.unitGrossPrice <= 0)) {
        setError('Укажыце брута-цэну для кожнага тавару.');
        return;
      }
      if (newExpense.grossAmount < expenseProductGrossTotal) {
        setError('Сума брута не можа быць менш за суму па таварах.');
        return;
      }
      if (newExpense.vatAmount > newExpense.grossAmount) {
        setError('Сума VAT не можа перавышаць суму брута.');
        return;
      }
    } else if (newExpense.grossAmount <= 0 && newExpense.vatAmount <= 0 && newExpense.netAmount <= 0) {
      setError('Увядзіце хаця б адну суму.');
      return;
    }

    setExpenseSaving(true);
    setError(null);
    try {
      const payload = {
        grossAmount: newExpense.grossAmount,
        vatAmount: newExpense.vatAmount,
        netAmount: newExpense.netAmount,
        expenseDateUtc: new Date(`${newExpense.expenseDateUtc}T00:00:00.000Z`).toISOString(),
        comment: newExpense.comment.trim() || undefined,
        invoiceNumber: newExpense.invoiceNumber.trim() || undefined,
        isPaid: newExpense.isPaid,
        isByProsvet: newExpense.isByProsvet,
        includeVatInTotal: newExpense.includeVatInTotal,
        expenseInvoiceTypeId: newExpense.expenseInvoiceTypeId,
        supplierId: isSupplierPaymentExpense && expenseSupplierId > 0 ? expenseSupplierId : undefined,
        products: isSupplierPaymentExpense
          ? expenseProductLines.map((line) => ({
              shopifyProductId: line.shopifyProductId,
              shopifyVariantId: line.shopifyVariantId,
              productTitle: line.productTitle,
              quantity: line.quantity,
              unitGrossPrice: line.unitGrossPrice,
              vatRatePercent: line.vatRatePercent,
            }))
          : undefined,
      };

      if (editingExpenseId !== null) {
        await updateVatReportExpense(editingExpenseId, payload);
        if (expenseInvoiceFile) {
          await uploadVatReportExpenseInvoice(editingExpenseId, expenseInvoiceFile);
        }
      } else {
        const expenseId = await createVatReportExpense(reportId, payload);
        if (expenseInvoiceFile) {
          await uploadVatReportExpenseInvoice(expenseId, expenseInvoiceFile);
        }
      }

      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
      setExpenseModalOpen(false);
      resetNewExpenseForm();
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : editingExpenseId !== null
            ? 'Памылка змянення расходу'
            : 'Памылка дадання расходу'
      );
    } finally {
      setExpenseSaving(false);
    }
  };

  const downloadExpenseInvoice = async (expenseId: number) => {
    try {
      const { blob, fileName } = await downloadVatReportExpenseInvoice(expenseId);
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = fileName;
      anchor.click();
      URL.revokeObjectURL(url);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка загрузкі фактуры');
    }
  };

  const resetNewCashSaleForm = () => {
    setCashProductSearch('');
    setNewCashSale({
      lineKey: '',
      shopifyProductId: '',
      shopifyVariantId: '',
      productTitle: '',
      quantity: 1,
      unitPrice: 0,
    });
  };

  const selectCashPickerLine = (line: CashPickerLine) => {
    setNewCashSale((prev) => ({
      ...prev,
      lineKey: line.lineKey,
      shopifyProductId: line.shopifyProductId,
      shopifyVariantId: line.shopifyVariantId,
      productTitle: buildExpensePickerLineTitle(line),
    }));
  };

  const submitCashSale = async () => {
    if (!newCashSale.lineKey || !newCashSale.shopifyProductId) {
      setError('Выберыце тавар.');
      return;
    }
    if (newCashSale.quantity <= 0) {
      setError('Колькасць павінна быць больш за 0.');
      return;
    }
    if (newCashSale.unitPrice < 0) {
      setError('Цана не можа быць адмоўнай.');
      return;
    }
    const targetReportId = data?.id ?? reportId;
    setCashSaving(true);
    setError(null);
    try {
      await createVatReportCashSale(targetReportId, {
        shopifyProductId: newCashSale.shopifyProductId,
        shopifyVariantId: newCashSale.shopifyVariantId,
        productTitle: newCashSale.productTitle,
        quantity: newCashSale.quantity,
        unitPrice: newCashSale.unitPrice,
      });
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
      setCashModalOpen(false);
      resetNewCashSaleForm();
      setExpandedOrderId('cash-summary');
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка дадання наяўнай продажы');
    } finally {
      setCashSaving(false);
    }
  };

  const openUnpaidLinkModal = async (item: VatReportUnpaidProductRow) => {
    if (!data?.periodYear || !data?.periodMonth || !item.supplierId) return;
    setUnpaidLinkTarget(item);
    setUnpaidLinkModalOpen(true);
    setUnpaidLinkError(null);
    setUnpaidLinkOptions(null);
    setSelectedOverpaidProductId('');
    setSelectedInvoiceExpenseId('');
    setSelectedPaymentRecordExpenseId('');
    setUnpaidLinkSource('invoice');
    setUnpaidLinkOptionsLoading(true);
    try {
      const options = await fetchUnpaidLinkOptions({
        supplierId: item.supplierId,
        periodYear: data.periodYear,
        periodMonth: data.periodMonth,
        shopifyProductId: item.shopifyProductId,
        shopifyVariantId: item.shopifyVariantId,
      });
      setUnpaidLinkOptions(options);
      if (options.overpaidProducts.length > 0) {
        setUnpaidLinkMode('replace');
        if (options.overpaidProducts.length === 1) {
          setSelectedOverpaidProductId(String(options.overpaidProducts[0].expenseProductId));
        }
      } else {
        setUnpaidLinkMode('link');
        if (options.supplierInvoices.length > 0) {
          setUnpaidLinkSource('invoice');
          if (options.supplierInvoices.length === 1) {
            setSelectedInvoiceExpenseId(String(options.supplierInvoices[0].expenseId));
          }
        } else if (options.supplierPaymentRecords.length > 0) {
          setUnpaidLinkSource('payment');
          if (options.supplierPaymentRecords.length === 1) {
            setSelectedPaymentRecordExpenseId(String(options.supplierPaymentRecords[0].expenseId));
          }
        }
      }
    } catch (err: unknown) {
      setUnpaidLinkError(err instanceof Error ? err.message : 'Памылка загрузкі варыянтаў');
    } finally {
      setUnpaidLinkOptionsLoading(false);
    }
  };

  const submitUnpaidLink = async () => {
    if (!data?.periodYear || !data?.periodMonth || !unpaidLinkTarget?.supplierId) return;
    if (unpaidLinkMode === 'replace' && !selectedOverpaidProductId) {
      setUnpaidLinkError('Выберыце пераплачаны тавар у фактуре.');
      return;
    }
    if (unpaidLinkMode === 'link') {
      if (unpaidLinkSource === 'invoice' && !selectedInvoiceExpenseId) {
        setUnpaidLinkError('Выберыце фактуру пастаўшчыка.');
        return;
      }
      if (unpaidLinkSource === 'payment' && !selectedPaymentRecordExpenseId) {
        setUnpaidLinkError('Выберыце запіс аплаты.');
        return;
      }
    }
    const linkExpenseId =
      unpaidLinkMode === 'link'
        ? Number(unpaidLinkSource === 'invoice' ? selectedInvoiceExpenseId : selectedPaymentRecordExpenseId)
        : undefined;
    setUnpaidLinkSaving(true);
    setUnpaidLinkError(null);
    try {
      await linkUnpaidProduct({
        periodYear: (() => {
          if (unpaidLinkTarget.saleOrderDateUtc) {
            const saleDate = new Date(unpaidLinkTarget.saleOrderDateUtc);
            if (!Number.isNaN(saleDate.getTime())) return saleDate.getUTCFullYear();
          }
          return data.periodYear;
        })(),
        periodMonth: (() => {
          if (unpaidLinkTarget.saleOrderDateUtc) {
            const saleDate = new Date(unpaidLinkTarget.saleOrderDateUtc);
            if (!Number.isNaN(saleDate.getTime())) return saleDate.getUTCMonth() + 1;
          }
          return data.periodMonth;
        })(),
        shopifyProductId: unpaidLinkTarget.shopifyProductId,
        shopifyVariantId: unpaidLinkTarget.shopifyVariantId,
        productTitle: unpaidLinkTarget.productTitle,
        supplierId: unpaidLinkTarget.supplierId,
        quantity: unpaidLinkTarget.quantity,
        mode: unpaidLinkMode,
        linkSource: unpaidLinkMode === 'link' ? unpaidLinkSource : undefined,
        expenseProductId:
          unpaidLinkMode === 'replace' ? Number(selectedOverpaidProductId) : undefined,
        expenseId: linkExpenseId,
      });
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
      setUnpaidLinkModalOpen(false);
      setUnpaidLinkTarget(null);
    } catch (err: unknown) {
      setUnpaidLinkError(err instanceof Error ? err.message : 'Памылка прывязкі');
    } finally {
      setUnpaidLinkSaving(false);
    }
  };

  const removeCashSale = async (cashSaleId: number) => {
    setDeletingCashSaleId(cashSaleId);
    setError(null);
    try {
      await deleteVatReportCashSale(cashSaleId);
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка выдалення наяўнай продажы');
    } finally {
      setDeletingCashSaleId(null);
    }
  };

  const removeExpense = async (expenseId: number) => {
    setDeletingExpenseId(expenseId);
    setError(null);
    try {
      await deleteVatReportExpense(expenseId);
      const { details, foreignRows } = await loadCombinedDetails(reportId);
      setForeignOrderRows(foreignRows);
      setData(details);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Памылка выдалення расходу');
    } finally {
      setDeletingExpenseId(null);
    }
  };

  const openBlobInNewTab = (blob: Blob) => {
    const url = URL.createObjectURL(blob);
    window.open(url, '_blank', 'noopener,noreferrer');
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
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

  const handleExportForeignOrderToPdf = async (row: VatReportDetails['rows'][number]) => {
    const uploadedInvoiceRow = row.polandRows.find((group) => Boolean(group.invoiceFileName));
    if (uploadedInvoiceRow) {
      setError(null);
      try {
        const { blob } = await downloadVatReportRowInvoice(uploadedInvoiceRow.id);
        const invoiceNumber = getInvoiceNumberForFile(
          row.name?.trim() || uploadedInvoiceRow.orderNumber,
          `order-${uploadedInvoiceRow.id}`
        );
        await saveBlobWithDialog(blob, `${invoiceNumber}.pdf`);
      } catch (err: unknown) {
        setError(err instanceof Error ? err.message : 'Памылка загрузкі фактуры');
      }
      return;
    }
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
          title: formatItemTitleForPolishInvoice(item.productTitle, item.productType),
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
    const itemsRowsHtml = itemRows
      .map(
        (item) => `<tr>
          <td>${escapeHtml(item.title)}</td>
          <td class="num">${item.quantity}</td>
          <td class="num">${formatAmount(item.netAmount)}</td>
          <td class="num">${formatAmount(item.vatRatePercent)}%</td>
          <td class="num">${formatAmount(item.vatAmount)}</td>
          <td class="num">${formatAmount(item.grossAmount)}</td>
        </tr>`
      )
      .join('');
    const shippingHtml = shippingRows
      .map(
        (s) => `<tr>
          <td>Dostawa</td>
          <td class="num">1</td>
          <td class="num">${formatAmount(s.netAmount)}</td>
          <td class="num">${formatAmount(s.vatRatePercent)}%</td>
          <td class="num">${formatAmount(s.vatAmount)}</td>
          <td class="num">${formatAmount(s.grossAmount)}</td>
        </tr>`
      )
      .join('');
    const vatByRate = row.polandRows.reduce(
      (acc, group) => {
        const key = Number(group.vatRatePercent || 0);
        acc.set(key, (acc.get(key) ?? 0) + (group.vatAmount || 0));
        return acc;
      },
      new Map<number, number>()
    );
    const vatRateSummary = Array.from(vatByRate.entries())
      .sort((a, b) => a[0] - b[0])
      .map(([rate]) => `${formatAmount(rate)}%`)
      .join('\n');
    const vatAmountSummary = Array.from(vatByRate.entries())
      .sort((a, b) => a[0] - b[0])
      .map(([, amount]) => formatAmount(amount))
      .join('\n');
    const pdfFileName = sanitizeDownloadFileName(`${invoiceNumber}.pdf`);
    const orderDatePl = row.orderDateUtc ? formatDatePl(row.orderDateUtc) : '—';
    const shippingAddressForInvoice = ensureCountryInAddress(
      row.shippingAddress || row.deliveryAddress || '',
      row.shippingCountryCode || row.billingCountryCode,
    );
    const billingAddressForInvoice = ensureCountryInAddress(
      row.billingAddress || row.shippingAddress || row.deliveryAddress || '',
      row.billingCountryCode || row.shippingCountryCode,
    );
    const sellerAddressForInvoice = invoiceSettings
      ? formatSellerAddressForInvoice(invoiceSettings.address)
      : '—';
    const html = `<!doctype html><html lang="pl"><head><meta charset="utf-8"/><title>${escapeHtml(pdfFileName)}</title><style>
      @page { size: A4 portrait; margin: 14mm; }
      :root { --brand:#07809f; --text:#0f172a; --muted:#64748b; --line:#e2e8f0; --soft:#f8fafc; }
      body { font-family: Arial, sans-serif; color:var(--text); margin:0; }
      .invoice { width:100%; }
      .title-wrap { margin-bottom: 14px; border-bottom:2px solid var(--line); padding-bottom:10px; }
      .title { text-align:center; font-size:24px; font-weight:800; letter-spacing:.8px; color:var(--brand); margin:0; }
      .meta-grid { display:grid; grid-template-columns: 1fr 1fr; gap:18px; margin-bottom:16px; }
      .block-title { font-size:11px; font-weight:700; color:var(--muted); text-transform:uppercase; letter-spacing:.5px; margin:0 0 6px; }
      .block { font-size:12px; line-height:1.5; }
      .row { margin:0 0 3px; }
      .label { color:var(--muted); font-weight:700; }
      table { width:100%; border-collapse:separate; border-spacing:0; font-size:12px; }
      th, td { padding:8px 8px; vertical-align:top; border-bottom:1px solid var(--line); }
      th { background:var(--soft); color:var(--muted); text-align:left; font-size:11px; text-transform:uppercase; letter-spacing:.4px; }
      tr td:first-child, tr th:first-child { border-left:1px solid transparent; }
      tr td:last-child, tr th:last-child { border-right:1px solid transparent; }
      td.num, th.num { text-align:right; font-variant-numeric: tabular-nums; }
      tfoot td { font-weight:700; border-top:2px solid var(--brand); border-bottom:0; }
      .total-label { color:var(--brand); }
      .multi-line { white-space: pre-line; }
    </style></head><body>
      <div class="invoice">
        <div class="title-wrap">
          <h1 class="title">FAKTURA ${escapeHtml(row.name)}</h1>
        </div>
        <div class="meta-grid">
          <div>
            <p class="block-title">Sprzedawca</p>
            <div class="block">
              ${
                invoiceSettings
                  ? `<div class="row"><strong>${escapeHtml(invoiceSettings.companyName)}</strong></div>
              <div class="row">${escapeHtml(sellerAddressForInvoice)}</div>
              <div class="row">${escapeHtml(invoiceSettings.email)}, ${escapeHtml(invoiceSettings.website)}</div>
              <div class="row"><span class="label">NIP:</span> ${escapeHtml(invoiceSettings.nip)}</div>`
                  : `<div class="row">—</div>`
              }
            </div>
          </div>
          <div>
            <p class="block-title">Nabywca i zamówienie</p>
            <div class="block">
              <div class="row"><span class="label">Numer zamówienia:</span> ${escapeHtml(row.name)}</div>
              <div class="row"><span class="label">Data:</span> ${escapeHtml(orderDatePl)}</div>
              <div class="row"><span class="label">Odbiorca:</span> ${escapeHtml(row.deliveryName || '—')}</div>
              <div class="row"><span class="label">Adres dostawy:</span> ${escapeHtml(shippingAddressForInvoice)}</div>
              <div class="row"><span class="label">Adres rozliczeniowy:</span> ${escapeHtml(billingAddressForInvoice)}</div>
            </div>
          </div>
        </div>
        <table>
          <thead>
            <tr>
              <th>Pozycja</th>
              <th class="num">Ilość</th>
              <th class="num">Wartość netto, ${escapeHtml(currency)}</th>
              <th class="num">Stawka VAT</th>
              <th class="num">VAT, ${escapeHtml(currency)}</th>
              <th class="num">Wartość brutto, ${escapeHtml(currency)}</th>
            </tr>
          </thead>
          <tbody>${itemsRowsHtml}${shippingHtml}</tbody>
          <tfoot>
            <tr>
              <td colspan="2" class="total-label">Razem</td>
              <td class="num">${formatAmount(row.netAmount ?? 0)}</td>
              <td class="num multi-line">${vatRateSummary || '—'}</td>
              <td class="num multi-line">${vatAmountSummary || formatAmount(row.vat)}</td>
              <td class="num">${formatAmount(row.grossAmount ?? 0)}</td>
            </tr>
          </tfoot>
        </table>
      </div>
    </body></html>`;
    printHtmlAsPdf(html);
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
    if (!addModalOpen || addMode !== 'select' || !selectedSourceIndex) return;
    const option = sourceOrderOptions[Number(selectedSourceIndex)];
    if (!option) return;
    setNewRow({
      orderNumber: option.orderNumber,
      orderDateUtc: toDateInputValue(option.orderDateUtc),
      vatRatePercent: option.vatRatePercent,
      grossAmount: option.grossAmount,
      vatAmount: option.vatAmount,
      netAmount: option.netAmount,
    });
  }, [addModalOpen, addMode, selectedSourceIndex, sourceOrderOptions]);

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

  const isLocked = data.isLocked;
  const lockedTitle = 'Справаздача заблакавана.';

  return (
    <div className="mx-auto w-full max-w-6xl space-y-6">
      {isLocked ? (
        <div className="flex flex-wrap items-center gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 sm:gap-4">
          <div
            className="flex size-10 shrink-0 items-center justify-center rounded-full border border-amber-300 bg-amber-100 text-amber-800"
            aria-hidden
          >
            <FiLock className="size-5" />
          </div>
          <p className="min-w-0 flex-1 text-sm text-amber-900">
            Перыяд заблакаваны — змяненні, выдаленне і перагенерацыя адключаны.
          </p>
          <button
            type="button"
            onClick={() => void handleToggleLock()}
            disabled={lockingReport}
            className="inline-flex shrink-0 items-center gap-2 rounded-lg border border-amber-300 bg-white px-3.5 py-2 text-sm font-medium text-amber-900 shadow-sm transition hover:bg-amber-100 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {lockingReport ? (
              <span className="size-4 animate-spin rounded-full border-2 border-amber-200 border-t-amber-800" />
            ) : (
              <FiUnlock className="size-4" aria-hidden />
            )}
            Разблакаваць
          </button>
        </div>
      ) : (
        <div className="flex justify-end">
          <button
            type="button"
            onClick={() => void handleToggleLock()}
            disabled={lockingReport}
            className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3.5 py-2 text-sm font-medium text-gray-700 shadow-sm transition hover:border-primary/40 hover:bg-primary/5 hover:text-primary disabled:cursor-not-allowed disabled:opacity-60"
          >
            {lockingReport ? (
              <span className="size-4 animate-spin rounded-full border-2 border-gray-200 border-t-gray-700" />
            ) : (
              <FiLock className="size-4" aria-hidden />
            )}
            Заблакаваць перыяд
          </button>
        </div>
      )}
      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="flex flex-wrap gap-x-8 gap-y-1 border-b border-gray-100 px-6 py-4 text-sm text-gray-600">
          <div>
            Усяго VAT:{' '}
            <span className="font-semibold text-gray-900">{formatAmount(displayTotalVat)}</span>
          </div>
          <div
            title={
              isLocked
                ? 'Прыбытак = выручка − расходы (без аплат пастаўшчыку) − сабестаімасць − аплаты асоб з раздзела «Фінансы» за месяц.'
                : 'Прыбытак уключае аплаты асоб (Зоя, Міця і г.д.) з раздзела «Фінансы» за гэты месяц. Выплаты Кірмашу не аднімаюцца.'
            }
          >
            Усяго прыбытак:{' '}
            <span className="font-semibold text-gray-900">{formatAmount(displayProfit)}</span>
          </div>
          {isForeignReportOnly && (
            <div className="ml-auto">
              <button
                type="button"
                onClick={openForeignAddModal}
                disabled={isLocked}
                className="inline-flex size-9 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary active:scale-[0.99] disabled:cursor-not-allowed disabled:opacity-40"
                aria-label="Дадаць замежны радок"
                title={isLocked ? lockedTitle : 'Дадаць замежны радок'}
              >
                <FiPlus className="size-4" aria-hidden />
              </button>
            </div>
          )}
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
                    <th className="px-4 py-2.5 text-right">PDF</th>
                  </>
                ) : (
                  <>
                    <th className="px-4 py-2.5">Тып</th>
                    <th className="px-4 py-2.5 text-right">Сума</th>
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
                      row.type === 'poland' ||
                      row.type === 'foreign' ||
                      row.type === 'expense' ||
                      row.type === 'cash' ||
                      row.type === 'unpaid'
                        ? 'cursor-pointer hover:bg-primary/10'
                        : ''
                    } ${
                      row.type === 'foreign' &&
                      row.shopifyOrderId !== 'foreign-summary' &&
                      row.polandRows.some((group) => Boolean(group.invoiceFileName))
                        ? 'bg-emerald-200/60 font-medium'
                        : ''
                    }`}
                    onClick={() => {
                      if (
                        row.type === 'poland' ||
                        row.type === 'foreign' ||
                        row.type === 'expense' ||
                        row.type === 'cash' ||
                        row.type === 'unpaid'
                      ) {
                        setExpandedOrderId((prev) => {
                          const next = prev === row.shopifyOrderId ? null : row.shopifyOrderId;
                          if (next !== null) {
                            if (row.type !== 'poland') setExpandedPolandRowId(null);
                            if (row.type !== 'foreign') setExpandedForeignOrderId(null);
                          }
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
                                if (isLocked) return;
                                const targetRowId = row.polandRows[0]?.id;
                                if (targetRowId) void handleUploadInvoice(targetRowId);
                              }}
                              disabled={isLocked}
                              className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:cursor-not-allowed disabled:opacity-40"
                              aria-label="Загрузіць фактуру"
                              title={isLocked ? lockedTitle : 'Загрузіць фактуру'}
                            >
                              <FiUpload className="size-4" aria-hidden />
                            </button>
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                handleExportForeignOrderToPdf(row);
                              }}
                              className="inline-flex size-8 items-center justify-center rounded-full border border-primary bg-primary text-white shadow-sm transition hover:bg-primary/90"
                              aria-label="Экспарт у PDF"
                              title="Экспарт у PDF"
                            >
                              <FiPrinter className="size-4" aria-hidden />
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
                              disabled={
                                isLocked ||
                                !row.polandRows[0]?.id ||
                                deletingRowKey === `foreign-${row.polandRows[0]?.id}`
                              }
                              className="inline-flex size-8 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
                              aria-label="Выдаліць радок"
                              title={isLocked ? lockedTitle : 'Выдаліць радок'}
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
                  <td className="px-4 py-3">
                    {row.type === 'poland'
                      ? 'Польшча'
                      : row.type === 'foreign'
                        ? 'Замежжа'
                        : row.type === 'cash'
                          ? 'Наяўнымі'
                          : row.type === 'unpaid'
                            ? 'Неаплачанае'
                            : 'Расход'}
                  </td>
                  <td className="px-4 py-3 text-right tabular-nums">
                    {row.type === 'unpaid' ? (
                      <span title="Ацэнка сабакошту па пастаўках">{formatAmount(row.grossAmount ?? 0)}</span>
                    ) : (
                      formatAmount(row.grossAmount ?? 0)
                    )}
                  </td>
                        <td className="px-4 py-3 text-right tabular-nums">{formatAmount(row.vat)}</td>
                        <td className="px-4 py-3 text-right">
                          {row.type !== 'expense' && row.type !== 'cash' && row.type !== 'unpaid' && (
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                if (isLocked) return;
                                setPendingRegenerateRowKey(`${row.type}-${row.shopifyOrderId}`);
                              }}
                              disabled={
                                isLocked || regeneratingRowKey === `${row.type}-${row.shopifyOrderId}`
                              }
                              className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:cursor-not-allowed disabled:opacity-40"
                              aria-label="Перегенераваць справаздачу"
                              title={isLocked ? lockedTitle : 'Перегенераваць справаздачу'}
                            >
                              {regeneratingRowKey === `${row.type}-${row.shopifyOrderId}` ? (
                                <span className="size-3.5 animate-spin rounded-full border-2 border-gray-300 border-t-gray-700" />
                              ) : (
                                <FiRefreshCw className="size-4" aria-hidden />
                              )}
                            </button>
                          )}
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
                                const { title, variantLabel } = formatOrderItemLabel(item);
                                return (
                                  <tr key={`${group.id}-${idx}`}>
                                    <td className="px-2 py-1.5">
                                      <div>{title}</div>
                                      {variantLabel && (
                                        <div className="text-[11px] text-primary">{variantLabel}</div>
                                      )}
                                    </td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">{item.quantity}</td>
                                    <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(netAmount)}</td>
                                    <td className="px-2 py-1.5 text-right">
                                      <div className="inline-flex items-center justify-end gap-2">
                                        <select
                                          value={String(item.assignedVatRatePercent)}
                                          onChange={(e) => {
                                            const nextVat = Number(e.target.value);
                                            if (!Number.isFinite(nextVat)) return;
                                            void handleUpdateForeignItemVat(item.id, nextVat);
                                          }}
                                          disabled={isLocked || updatingItemVatId === item.id}
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
                                          const nextVat = Number(e.target.value);
                                          if (!Number.isFinite(nextVat)) return;
                                          void handleUpdateForeignShippingVat(group, nextVat);
                                        }}
                                        disabled={isLocked || updatingShippingVatRowId === group.id}
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

      {activeDetailsPanel && (
        <div ref={detailsPanelRef} className="w-full max-w-full scroll-mt-4 [overflow-anchor:none]">
      {activeDetailsPanel === 'poland' && (
        <div className="w-full overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="flex items-center justify-between border-b border-gray-100 px-6 py-4">
            <h3 className="text-sm font-semibold text-gray-900">Дэталі па Польшчы</h3>
            <div className="flex flex-wrap items-end gap-2">
              <label className="w-full max-w-[11.5rem] space-y-1">
                <span className="text-xs font-medium uppercase tracking-wide text-gray-500">Пошук</span>
                <div className="flex items-center gap-2">
                  <input
                    type="text"
                    value={orderSearch}
                    onChange={(e) => setOrderSearch(e.target.value)}
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
                disabled={isLocked}
                className="inline-flex size-9 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary active:scale-[0.99] disabled:cursor-not-allowed disabled:opacity-40"
                aria-label="Дадаць радок"
                title={isLocked ? lockedTitle : 'Дадаць радок'}
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
                              onChange={(e) => setVatFilter5(e.target.checked)}
                              className="size-3.5 rounded border-gray-300 accent-primary"
                            />
                            5%
                          </label>
                          <label className="mt-1 flex items-center gap-2 rounded-md px-2 py-1.5 text-xs font-medium normal-case tracking-normal text-gray-700 hover:bg-gray-50">
                            <input
                              type="checkbox"
                              checked={vatFilter23}
                              onChange={(e) => setVatFilter23(e.target.checked)}
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
                              const value = Number(e.target.value) || 0;
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
                              const value = Number(e.target.value) || 0;
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
                              const value = Number(e.target.value) || 0;
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
                              const value = Number(e.target.value) || 0;
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
                            onClick={() => {
                              if (isLocked) return;
                              void handleUploadInvoice(row.id);
                            }}
                            disabled={isLocked}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:cursor-not-allowed disabled:opacity-40"
                            aria-label="Загрузіць фактуру"
                            title={isLocked ? lockedTitle : 'Загрузіць фактуру'}
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
                              if (isLocked) return;
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
                            disabled={isLocked}
                            className={`inline-flex size-8 items-center justify-center rounded-full border text-gray-900 shadow-sm transition disabled:cursor-not-allowed disabled:opacity-40 ${
                              isEditing
                                ? 'border-primary/40 bg-white text-primary hover:bg-primary/10'
                                : 'border-gray-200 bg-white hover:border-primary/40 hover:bg-primary/10 hover:text-primary'
                            }`}
                            aria-label={isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                            title={isLocked ? lockedTitle : isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                          >
                            <FiEdit2 className="size-4" aria-hidden />
                          </button>
                          <button
                            type="button"
                            onClick={() => {
                              if (isLocked) return;
                              setPendingMoveToForeignRow({ rowId: row.id, rowKey });
                              setMoveToForeignName('');
                              setMoveToForeignAddress('');
                            }}
                            disabled={isLocked || movingToForeignRowKey === rowKey}
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
                            onClick={() => {
                              if (isLocked) return;
                              setPendingDeleteRow({ rowId: row.id, rowKey });
                            }}
                            disabled={isLocked || deletingRowKey === rowKey}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
                            aria-label="Выдаліць радок"
                            title={isLocked ? lockedTitle : 'Выдаліць радок'}
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

      {activeDetailsPanel === 'foreign' && (
        <div className="w-full overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="flex items-center justify-between border-b border-gray-100 px-6 py-4">
            <h3 className="shrink-0 text-sm font-semibold text-gray-900">Дэталі па Замежжы</h3>
            <div className="flex shrink-0 flex-wrap items-end justify-end gap-2">
              <label className="w-full max-w-[11.5rem] space-y-1">
                <span className="text-xs font-medium uppercase tracking-wide text-gray-500">Пошук</span>
                <div className="flex items-center gap-2">
                  <input
                    type="text"
                    value={foreignOrderSearch}
                    onChange={(e) => setForeignOrderSearch(e.target.value)}
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
              <button
                type="button"
                onClick={openForeignAddModal}
                disabled={isLocked}
                className="inline-flex size-9 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary active:scale-[0.99] disabled:cursor-not-allowed disabled:opacity-40"
                aria-label="Дадаць замежны радок"
                title={isLocked ? lockedTitle : 'Дадаць замежны радок'}
              >
                <FiPlus className="size-4" aria-hidden />
              </button>
            </div>
          </div>
          <div className="overflow-x-auto [scrollbar-gutter:stable]">
            <table className="min-w-full border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="px-4 py-2.5">Нумар замовы</th>
                  <th className="px-4 py-2.5">Дата</th>
                  <th className="px-4 py-2.5">Дастаўка</th>
                  <th className="px-4 py-2.5 text-right">Сума нета</th>
                  <th className="px-4 py-2.5 text-right">VAT</th>
                  <th className="px-4 py-2.5 text-right">Сума брута</th>
                  <th className="px-4 py-2.5 text-right">PDF</th>
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
                              if (isLocked) return;
                              const targetRowId = row.polandRows[0]?.id;
                              if (targetRowId) void handleUploadInvoice(targetRowId);
                            }}
                            disabled={isLocked}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:cursor-not-allowed disabled:opacity-40"
                            aria-label="Загрузіць фактуру"
                            title={isLocked ? lockedTitle : 'Загрузіць фактуру'}
                          >
                            <FiUpload className="size-4" aria-hidden />
                          </button>
                          <button
                            type="button"
                            onClick={(e) => {
                              e.stopPropagation();
                              handleExportForeignOrderToPdf(row);
                            }}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-primary bg-primary text-white shadow-sm transition hover:bg-primary/90"
                            aria-label="Экспарт у PDF"
                            title="Экспарт у PDF"
                          >
                            <FiPrinter className="size-4" aria-hidden />
                          </button>
                          <button
                            type="button"
                            onClick={(e) => {
                              e.stopPropagation();
                              if (isLocked) return;
                              const targetRowId = row.polandRows[0]?.id;
                              if (!targetRowId) return;
                              setPendingDeleteRow({
                                rowId: targetRowId,
                                rowKey: `foreign-${targetRowId}`,
                              });
                            }}
                            disabled={
                              isLocked ||
                              !row.polandRows[0]?.id ||
                              deletingRowKey === `foreign-${row.polandRows[0]?.id}`
                            }
                            className="inline-flex size-8 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
                            aria-label="Выдаліць радок"
                            title={isLocked ? lockedTitle : 'Выдаліць радок'}
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
                                              const value = Number(e.target.value) || 0;
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
                                              const value = Math.max(0, Number(e.target.value) || 0);
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
                                                  const checked = e.target.checked;
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
                                                const value = Math.max(0, Number(e.target.value) || 0);
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
                                              if (isLocked) return;
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
                                            disabled={isLocked}
                                            className={`inline-flex size-7 items-center justify-center rounded-full border text-gray-700 shadow-sm transition disabled:cursor-not-allowed disabled:opacity-40 ${
                                              isEditing
                                                ? 'border-primary bg-primary text-white hover:bg-primary/90'
                                                : 'border-gray-200 bg-white hover:border-primary/40 hover:bg-primary/15 hover:text-primary'
                                            }`}
                                            aria-label={isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                                            title={isLocked ? lockedTitle : isEditing ? 'Завяршыць рэдагаванне радка' : 'Рэдагаваць радок'}
                                          >
                                            <FiEdit2 className="size-3.5" aria-hidden />
                                          </button>
                                          <button
                                            type="button"
                                            onClick={() => {
                                              if (isLocked) return;
                                              setPendingDeleteRow({ rowId: group.id, rowKey });
                                            }}
                                            disabled={isLocked || deletingRowKey === rowKey}
                                            className="inline-flex size-7 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
                                            aria-label="Выдаліць радок"
                                            title={isLocked ? lockedTitle : 'Выдаліць радок'}
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
                                  const { title, variantLabel } = formatOrderItemLabel(item);
                                  return (
                                    <tr key={`${group.id}-${idx}`}>
                                      <td className="px-2 py-1.5">
                                        <div>{title}</div>
                                        {variantLabel && (
                                          <div className="text-[11px] text-primary">{variantLabel}</div>
                                        )}
                                      </td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">{item.quantity}</td>
                                      <td className="px-2 py-1.5 text-right tabular-nums">{formatAmount(netAmount)}</td>
                                      <td className="px-2 py-1.5 text-right">
                                        <div className="inline-flex items-center justify-end gap-2">
                                          <select
                                            value={String(item.assignedVatRatePercent)}
                                            onChange={(e) => {
                                              const nextVat = Number(e.target.value);
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
                                            const nextVat = Number(e.target.value);
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

      {activeDetailsPanel === 'cash' && (
        <div className="w-full overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="flex items-center justify-between border-b border-gray-100 px-6 py-4">
            <h3 className="shrink-0 text-sm font-semibold text-gray-900">Наяўнымі</h3>
            <button
              type="button"
              onClick={() => {
                if (isLocked) return;
                resetNewCashSaleForm();
                setCashModalOpen(true);
              }}
              disabled={isLocked}
              className="inline-flex size-9 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary active:scale-[0.99] disabled:cursor-not-allowed disabled:opacity-40"
              aria-label="Дадаць наяўную продажу"
              title={isLocked ? lockedTitle : 'Дадаць наяўную продажу'}
            >
              <FiPlus className="size-4" aria-hidden />
            </button>
          </div>
          <table className="w-full border-collapse text-left text-sm">
            <thead>
              <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                <th className="px-4 py-2.5">Тавар</th>
                <th className="px-4 py-2.5 text-right">Колькасць</th>
                <th className="px-4 py-2.5 text-right">Цана</th>
                <th className="px-4 py-2.5 text-right">Сума</th>
                <th className="px-4 py-2.5 text-right">Дзеянне</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {visibleCashRows.map((sale) => (
                <tr key={`cash-${sale.id}`}>
                  <td className="px-4 py-3">{sale.productTitle}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{sale.quantity}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{formatAmount(sale.unitPrice)}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{formatAmount(sale.grossAmount)}</td>
                  <td className="px-4 py-3 text-right">
                    <button
                      type="button"
                      onClick={() => {
                        if (isLocked) return;
                        void removeCashSale(sale.id);
                      }}
                      disabled={isLocked || deletingCashSaleId === sale.id}
                      className="inline-flex size-8 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
                      aria-label="Выдаліць"
                      title={isLocked ? lockedTitle : 'Выдаліць'}
                    >
                      {deletingCashSaleId === sale.id ? (
                        <span className="size-3.5 animate-spin rounded-full border-2 border-red-300 border-t-red-600" />
                      ) : (
                        <FiTrash2 className="size-4" aria-hidden />
                      )}
                    </button>
                  </td>
                </tr>
              ))}
              {visibleCashRows.length === 0 && (
                <tr>
                  <td colSpan={5} className="px-4 py-6 text-center text-sm text-gray-500">
                    Наяўных продаж пакуль няма.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {activeDetailsPanel === 'unpaid' && (
        <div className="w-full overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-100 px-6 py-4">
            <h3 className="text-sm font-semibold text-gray-900">Неаплачаныя тавары</h3>
            <p className="mt-1 text-xs text-gray-500">
              Проданы ў гэтым месяцы (па даце продажы), але фактура пастаўшчыку не пакрыла гэты тавар.
              Прывязаныя да аплаты пазначаны нумарам фактуры.
            </p>
          </div>
          <table className="w-full border-collapse text-left text-sm">
            <thead>
              <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                <th className="px-4 py-2.5">Дата продажы</th>
                <th className="px-4 py-2.5">Тавар</th>
                <th className="px-4 py-2.5">Пастаўшчык</th>
                <th className="px-4 py-2.5">Прывязка</th>
                <th className="px-4 py-2.5 text-right">Колькасць</th>
                <th className="px-4 py-2.5 text-right">Цана з пастаўкі</th>
                <th className="px-4 py-2.5 text-right">Ацэнка COGS</th>
                <th className="px-4 py-2.5 text-right">Дзеянне</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {visibleUnpaidRows.map((item) => {
                const productLabel = formatUnpaidProductLabel(item, unpaidCatalogProducts);
                return (
                <tr
                  key={`unpaid-${item.shopifyProductId}-${item.shopifyVariantId ?? ''}-${item.supplierId ?? 0}-${item.linkedExpenseId ?? 0}`}
                  className={item.isManuallyLinked ? 'bg-sky-50/60' : undefined}
                >
                  <td className="px-4 py-3">
                    {item.saleOrderDateUtc ? formatDate(item.saleOrderDateUtc) : '—'}
                  </td>
                  <td className="px-4 py-3">
                    <div className="font-medium text-gray-900">{productLabel.title}</div>
                    {productLabel.variantLabel ? (
                      <div className="mt-0.5 text-xs font-medium text-primary">{productLabel.variantLabel}</div>
                    ) : null}
                  </td>
                  <td className="px-4 py-3">{item.supplierName || '—'}</td>
                  <td className="px-4 py-3">
                    {item.isManuallyLinked && item.linkedPaymentLabel ? (
                      <span className="inline-flex rounded-md bg-sky-100 px-2 py-0.5 text-xs font-medium text-sky-800">
                        {item.linkedPaymentLabel}
                      </span>
                    ) : (
                      '—'
                    )}
                  </td>
                  <td className="px-4 py-3 text-right tabular-nums">{item.quantity}</td>
                  <td className="px-4 py-3 text-right tabular-nums">
                    {item.unitSupplyPrice > 0 ? formatAmount(item.unitSupplyPrice) : '—'}
                  </td>
                  <td className="px-4 py-3 text-right tabular-nums">
                    {item.isManuallyLinked
                      ? '—'
                      : item.estimatedCogs > 0
                        ? formatAmount(item.estimatedCogs)
                        : '—'}
                  </td>
                  <td className="px-4 py-3 text-right">
                    {item.isManuallyLinked ? (
                      <span className="text-xs text-gray-500">Прывязана</span>
                    ) : (
                      <button
                        type="button"
                        onClick={() => openUnpaidLinkModal(item)}
                        disabled={isLocked || !item.supplierId}
                        className="rounded-lg border border-gray-200 bg-white px-2.5 py-1 text-xs font-medium text-gray-800 transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:cursor-not-allowed disabled:opacity-40"
                        title={isLocked ? lockedTitle : 'Прывязаць да фактуры'}
                      >
                        Прывязаць
                      </button>
                    )}
                  </td>
                </tr>
              );
              })}
              {visibleUnpaidRows.length === 0 && (
                <tr>
                  <td colSpan={8} className="px-4 py-6 text-center text-sm text-gray-500">
                    Усе проданыя тавары аплачаны па фактурах.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {activeDetailsPanel === 'expense' && (
        <div className="w-full overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="flex items-center justify-between border-b border-gray-100 px-6 py-4">
            <h3 className="shrink-0 text-sm font-semibold text-gray-900">Дэталі па Расходу</h3>
            <div className="flex shrink-0 flex-wrap items-end justify-end gap-2">
              <label className="w-full max-w-[11.5rem] space-y-1">
                <span className="text-xs font-medium uppercase tracking-wide text-gray-500">Пошук</span>
                <div className="flex items-center gap-2">
                  <input
                    type="text"
                    value={expenseSearch}
                    onChange={(e) => setExpenseSearch(e.target.value)}
                    placeholder="Тып або каментар"
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2.5 text-sm text-gray-800 transition placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                  <button
                    type="button"
                    onClick={() => setExpenseSearch('')}
                    className="inline-flex size-9 shrink-0 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                    aria-label="Скінуць пошук"
                    title="Скінуць пошук"
                  >
                    <FiX className="size-4" aria-hidden />
                  </button>
                </div>
              </label>
              <button
                type="button"
                onClick={() => {
                  if (isLocked) return;
                  resetNewExpenseForm();
                  setExpenseModalOpen(true);
                }}
                disabled={isLocked}
                className="inline-flex size-9 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary active:scale-[0.99] disabled:cursor-not-allowed disabled:opacity-40"
                aria-label="Дадаць расход"
                title={isLocked ? lockedTitle : 'Дадаць расход'}
              >
                <FiPlus className="size-4" aria-hidden />
              </button>
            </div>
          </div>
          <table className="w-full border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50 text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="px-4 py-2.5">Тып</th>
                  <th className="px-4 py-2.5">Дата</th>
                  <th className="px-4 py-2.5 text-right">Сума нета</th>
                  <th className="px-4 py-2.5 text-right">VAT</th>
                  <th className="px-4 py-2.5 text-right">Сума брута</th>
                  <th className="px-4 py-2.5">Каментар</th>
                  <th className="px-4 py-2.5 text-center">Аплочана</th>
                  <th className="px-4 py-2.5 text-right">Дзеянне</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {visibleExpenseRows.map((expense) => {
                  const rowHighlightClass = expense.invoiceFileName ? 'bg-emerald-200/60 font-medium' : '';
                  return (
                  <tr
                    key={`expense-${expense.id}`}
                    className={rowHighlightClass}
                  >
                    <td className={`px-4 py-3 ${rowHighlightClass}`}>
                      <div className="space-y-1">
                        <div className="inline-flex items-center gap-2">
                          <span>{expense.expenseInvoiceTypeName || '—'}</span>
                          {expense.invoiceFileName && (
                            <span className="rounded-full bg-emerald-600 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-white">
                              Фактура загружана
                            </span>
                          )}
                          {expense.isByProsvet && (
                            <span className="rounded-full bg-sky-100 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-sky-800">
                              ByProsvet
                            </span>
                          )}
                        </div>
                        {expense.supplierName && (
                          <div className="text-xs text-gray-500">Пастаўшчык: {expense.supplierName}</div>
                        )}
                        {expense.invoiceNumber && (
                          <div className="text-xs text-gray-500">№ фактуры: {expense.invoiceNumber}</div>
                        )}
                        {expense.products.length > 0 && (
                          <div className="text-xs text-gray-500">
                            {expense.products
                              .map((product) =>
                                product.unitGrossPrice > 0
                                  ? `${product.productTitle} × ${product.quantity} @ ${formatAmount(product.unitGrossPrice)}`
                                  : `${product.productTitle} × ${product.quantity}`
                              )
                              .join(', ')}
                          </div>
                        )}
                      </div>
                    </td>
                    <td className={`px-4 py-3 ${rowHighlightClass}`}>{formatDate(expense.expenseDateUtc)}</td>
                    <td className={`px-4 py-3 text-right tabular-nums ${rowHighlightClass}`}>{formatAmount(expense.netAmount)}</td>
                    <td className={`px-4 py-3 text-right tabular-nums ${rowHighlightClass}`}>{formatAmount(expense.vatAmount)}</td>
                    <td className={`px-4 py-3 text-right tabular-nums ${rowHighlightClass}`}>{formatAmount(expense.grossAmount)}</td>
                    <td className={`max-w-[16rem] truncate px-4 py-3 ${rowHighlightClass}`} title={expense.comment || undefined}>
                      {expense.comment || '—'}
                    </td>
                    <td className={`px-4 py-3 text-center ${rowHighlightClass}`}>{expense.isPaid ? 'Так' : 'Не'}</td>
                    <td className={`px-4 py-3 text-right ${rowHighlightClass}`}>
                      <div className="inline-flex items-center gap-2">
                        {expense.invoiceFileName && (
                          <button
                            type="button"
                            onClick={() => void downloadExpenseInvoice(expense.id)}
                            className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
                            aria-label="Спампаваць фактуру"
                            title="Спампаваць фактуру"
                          >
                            <FiDownload className="size-4" aria-hidden />
                          </button>
                        )}
                        <button
                          type="button"
                          onClick={() => {
                            if (isLocked) return;
                            void openExpenseForEdit(expense);
                          }}
                          disabled={isLocked}
                          className="inline-flex size-8 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-900 shadow-sm transition hover:border-primary/40 hover:bg-primary/10 hover:text-primary disabled:cursor-not-allowed disabled:opacity-40"
                          aria-label="Змяніць расход"
                          title={isLocked ? lockedTitle : 'Змяніць расход'}
                        >
                          <FiEdit2 className="size-4" aria-hidden />
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            if (isLocked) return;
                            void removeExpense(expense.id);
                          }}
                          disabled={isLocked || deletingExpenseId === expense.id}
                          className="inline-flex size-8 items-center justify-center rounded-full border border-red-200 bg-white text-red-600 shadow-sm transition hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
                          aria-label="Выдаліць расход"
                          title="Выдаліць расход"
                        >
                          {deletingExpenseId === expense.id ? (
                            <span className="size-3.5 animate-spin rounded-full border-2 border-red-300 border-t-red-600" />
                          ) : (
                            <FiTrash2 className="size-4" aria-hidden />
                          )}
                        </button>
                      </div>
                    </td>
                  </tr>
                  );
                })}
                {visibleExpenseRows.length === 0 && (
                  <tr>
                    <td colSpan={8} className="px-4 py-6 text-center text-sm text-gray-500">
                      {(expandedRow?.expenseRows?.length ?? 0) === 0
                        ? 'Расходаў пакуль няма.'
                        : 'Няма радкоў па выбраным пошуку.'}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
        </div>
      )}
        </div>
      )}

      {addModalOpen && (
        <div
          className="fixed inset-0 z-[80] flex items-center justify-center bg-black/40 p-4"
        >
          <div
            className="w-full max-w-2xl rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
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
                    setSelectedSourceIndex('');
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
                  <span className="text-sm font-medium text-gray-700">
                    Нумар замовы (за гэты месяц)
                    {sourceOrdersLoading && (
                      <span className="ml-2 text-xs font-normal text-gray-500">Загрузка...</span>
                    )}
                  </span>
                  <select
                    value={selectedSourceIndex}
                    onChange={(e) => setSelectedSourceIndex(e.target.value)}
                    disabled={addingRow}
                    className="relative z-[81] w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  >
                    <option value="">
                      {sourceOrdersLoading ? 'Загрузка спісу замоў...' : 'Выберыце заказ'}
                    </option>
                    {sourceOrderOptions.map((option, index) => (
                      <option key={toSourceKey(option) + index} value={String(index)}>
                        {formatSourceOrderLabel(option)}
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
                      const orderNumber = e.target.value;
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
                      const orderDateUtc = e.target.value;
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
                      const vatRatePercent = Number(e.target.value) || 0;
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
                      const grossAmount = Number(e.target.value) || 0;
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
                      const vatAmount = Number(e.target.value) || 0;
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
                      const netAmount = Number(e.target.value) || 0;
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
                disabled={addingRow || (addMode === 'select' && !selectedSourceIndex) || sourceOrdersLoading}
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

      {foreignAddModalOpen && (
        <div
          className="fixed inset-0 z-[80] flex items-end justify-center overflow-y-auto bg-black/40 p-3 sm:items-center sm:p-4"
        >
          <div
            className="my-auto flex max-h-[min(760px,calc(100dvh-1.5rem))] w-full max-w-2xl min-h-0 flex-col overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl sm:my-0"
          >
            <div className="shrink-0 border-b border-gray-100 px-4 py-3 sm:px-5">
              <div className="text-base font-semibold text-gray-900">Дадаць замежны заказ</div>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-3 sm:px-5">
              {foreignAddError && (
                <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
                  {foreignAddError}
                </div>
              )}
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Нумар замовы</span>
                  <input
                    type="text"
                    value={newForeignRow.orderNumber}
                    onChange={(e) => {
                      const orderNumber = e.target.value;
                      setNewForeignRow((prev) => ({ ...prev, orderNumber }));
                    }}
                    placeholder="#1701"
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Дата замовы</span>
                  <input
                    type="date"
                    value={newForeignRow.orderDateUtc}
                    onChange={(e) => {
                      const orderDateUtc = e.target.value;
                      setNewForeignRow((prev) => ({ ...prev, orderDateUtc }));
                    }}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5 sm:col-span-2">
                  <span className="text-sm font-medium text-gray-700">Імя атрымальніка</span>
                  <input
                    type="text"
                    value={newForeignRow.deliveryName}
                    onChange={(e) => {
                      const deliveryName = e.target.value;
                      setNewForeignRow((prev) => ({ ...prev, deliveryName }));
                    }}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5 sm:col-span-2">
                  <span className="text-sm font-medium text-gray-700">Адрас дастаўкі</span>
                  <input
                    type="text"
                    value={newForeignRow.deliveryAddress}
                    onChange={(e) => {
                      const deliveryAddress = e.target.value;
                      setNewForeignRow((prev) => ({ ...prev, deliveryAddress }));
                    }}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Краіна</span>
                  <select
                    value={newForeignRow.countryCode}
                    onChange={(e) => {
                      const countryCode = e.target.value;
                      setNewForeignRow((prev) => ({ ...prev, countryCode }));
                    }}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  >
                    {FOREIGN_COUNTRY_OPTIONS.map((option) => (
                      <option key={option.code} value={option.code}>
                        {option.label}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Дастаўка (брута)</span>
                  <input
                    type="number"
                    min="0"
                    step="0.01"
                    value={newForeignRow.shippingGrossAmount || ''}
                    onChange={(e) => {
                      const shippingGrossAmount = Number(e.target.value) || 0;
                      setNewForeignRow((prev) => ({ ...prev, shippingGrossAmount }));
                    }}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
              </div>
              <div className="mt-4 space-y-2">
                <div className="flex items-center justify-between gap-3">
                  <span className="text-sm font-medium text-gray-700">Тавары</span>
                  {foreignProductLines.length > 0 && (
                    <span className="text-xs text-gray-500">Выбрана: {foreignProductLines.length}</span>
                  )}
                </div>
                <input
                  type="search"
                  value={foreignProductSearch}
                  onChange={(e) => setForeignProductSearch(e.target.value)}
                  placeholder="Пошук тавару..."
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
                <div className="max-h-[min(12rem,30vh)] overflow-y-auto rounded-lg border border-gray-200">
                  {foreignShopifyProductsLoading && (
                    <div className="px-3 py-4 text-sm text-gray-500">Загрузка тавараў з Shopify...</div>
                  )}
                  {!foreignShopifyProductsLoading && visibleForeignPickerLines.length === 0 && (
                    <div className="px-3 py-4 text-sm text-gray-500">
                      {foreignProductSearch.trim()
                        ? 'Тавары не знойдзены'
                        : 'Няма тавараў у Shopify'}
                    </div>
                  )}
                  {!foreignShopifyProductsLoading &&
                    visibleForeignPickerLines.map((pickerLine) => {
                      const line = foreignProductLines.find((item) => item.lineKey === pickerLine.lineKey);
                      const selected = Boolean(line);
                      return (
                        <div
                          key={pickerLine.lineKey}
                          className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-gray-100 px-3 py-2 last:border-b-0"
                        >
                          <input
                            type="checkbox"
                            checked={selected}
                            onChange={(e) => toggleForeignPickerLine(pickerLine, e.target.checked)}
                            className="size-4 shrink-0 rounded border-gray-300 accent-primary"
                          />
                          <span className="min-w-0 flex-1 basis-[12rem] text-sm text-gray-800">
                            <span className="line-clamp-2">
                              {buildExpensePickerLineTitle(pickerLine)}
                            </span>
                          </span>
                          {selected && (
                            <div className="flex w-full shrink-0 flex-wrap items-center gap-2 sm:ml-auto sm:w-auto">
                              <label className="inline-flex items-center gap-1.5 text-xs text-gray-600">
                                <span>Кол.</span>
                                <input
                                  type="number"
                                  min="1"
                                  step="1"
                                  value={line?.quantity ?? 1}
                                  onChange={(e) =>
                                    updateForeignProductQuantity(pickerLine.lineKey, Number(e.target.value))
                                  }
                                  className="w-16 rounded-md border border-gray-200 bg-white px-2 py-1 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                                />
                              </label>
                              <label className="inline-flex items-center gap-1.5 text-xs text-gray-600">
                                <span>Цана брута</span>
                                <input
                                  type="number"
                                  min="0"
                                  step="0.01"
                                  value={line?.unitGrossPrice || ''}
                                  onChange={(e) =>
                                    updateForeignProductUnitPrice(pickerLine.lineKey, Number(e.target.value))
                                  }
                                  className="w-24 rounded-md border border-gray-200 bg-white px-2 py-1 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                                />
                              </label>
                            </div>
                          )}
                        </div>
                      );
                    })}
                </div>
                {foreignProductLines.length > 0 && (
                  <div className="rounded-lg bg-gray-50 px-3 py-2 text-sm text-gray-700">
                    Сума тавараў:{' '}
                    <span className="font-semibold tabular-nums">{formatAmount(foreignProductGrossTotal)}</span>
                    {newForeignRow.shippingGrossAmount > 0 && (
                      <>
                        {' '}
                        + дастаўка{' '}
                        <span className="font-semibold tabular-nums">
                          {formatAmount(newForeignRow.shippingGrossAmount)}
                        </span>
                      </>
                    )}
                  </div>
                )}
              </div>
            </div>
            <div className="shrink-0 border-t border-gray-100 px-4 py-3 sm:px-5">
              <div className="flex justify-end gap-2">
                <button
                  type="button"
                  onClick={() => setForeignAddModalOpen(false)}
                  disabled={foreignAddSaving}
                  className="rounded-lg border border-gray-200 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-60"
                >
                  Скасаваць
                </button>
                <button
                  type="button"
                  onClick={() => void submitForeignRow()}
                  disabled={foreignAddSaving}
                  className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary/90 disabled:opacity-60"
                >
                  {foreignAddSaving ? 'Захаванне...' : 'Дадаць'}
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {cashModalOpen && (
        <div
          className="fixed inset-0 z-[80] flex items-end justify-center overflow-y-auto bg-black/40 p-3 sm:items-center sm:p-4"
        >
          <div
            className="my-auto flex max-h-[min(640px,calc(100dvh-1.5rem))] w-full max-w-2xl min-h-0 flex-col overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl sm:my-0"
          >
            <div className="shrink-0 border-b border-gray-100 px-4 py-3 sm:px-5">
              <div className="text-base font-semibold text-gray-900">Дадаць наяўную продажу</div>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-3 sm:px-5">
            <div className="space-y-3">
              <div className="space-y-2">
                <span className="text-sm font-medium text-gray-700">Тавар</span>
                <input
                  type="search"
                  value={cashProductSearch}
                  onChange={(e) => setCashProductSearch(e.target.value)}
                  placeholder="Пошук тавару..."
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
                <div className="max-h-[min(12rem,32vh)] overflow-y-auto rounded-lg border border-gray-200">
                  {cashProductsLoading && (
                    <div className="px-3 py-4 text-sm text-gray-500">Загрузка тавараў...</div>
                  )}
                  {!cashProductsLoading && visibleCashPickerLines.length === 0 && (
                    <div className="px-3 py-4 text-sm text-gray-500">Тавары не знойдзены</div>
                  )}
                  {!cashProductsLoading &&
                    visibleCashPickerLines.map((line) => {
                      const selected = newCashSale.lineKey === line.lineKey;
                      return (
                        <button
                          key={line.lineKey}
                          type="button"
                          onClick={() => selectCashPickerLine(line)}
                          className={`flex w-full items-center gap-x-3 border-b border-gray-100 px-3 py-2 text-left last:border-b-0 transition ${
                            selected ? 'bg-primary/5' : 'hover:bg-gray-50'
                          }`}
                        >
                          <input
                            type="radio"
                            checked={selected}
                            readOnly
                            className="size-4 shrink-0 accent-primary"
                            aria-hidden
                          />
                          {line.mainImageUrl ? (
                            <img
                              src={line.mainImageUrl}
                              alt={line.productName}
                              className="h-10 w-8 shrink-0 rounded border border-gray-200 object-cover object-center"
                              loading="lazy"
                            />
                          ) : (
                            <div className="h-10 w-8 shrink-0 rounded border border-gray-200 bg-gray-100" />
                          )}
                          <span className="min-w-0 flex-1 text-sm text-gray-800">
                            <span className="line-clamp-2">
                              {formatProductNameWithAuthor(line.productName, line.productAuthor)}
                            </span>
                            {line.variantName.trim() && (
                              <span className="block text-xs text-gray-600">{line.variantName}</span>
                            )}
                          </span>
                        </button>
                      );
                    })}
                </div>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Колькасць</span>
                  <input
                    type="number"
                    min="1"
                    value={newCashSale.quantity}
                    onChange={(e) => {
                      const quantity = Math.max(1, Number(e.target.value) || 1);
                      setNewCashSale((prev) => ({ ...prev, quantity }));
                    }}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
                <label className="block space-y-1.5">
                  <span className="text-sm font-medium text-gray-700">Цана</span>
                  <input
                    type="number"
                    min="0"
                    step="0.01"
                    value={newCashSale.unitPrice || ''}
                    onChange={(e) => {
                      const unitPrice = Number(e.target.value) || 0;
                      setNewCashSale((prev) => ({ ...prev, unitPrice }));
                    }}
                    className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                  />
                </label>
              </div>
              {newCashSale.lineKey && newCashSale.quantity > 0 && newCashSale.unitPrice > 0 && (
                <div className="rounded-lg bg-gray-50 px-3 py-2 text-sm text-gray-700">
                  Сума:{' '}
                  <span className="font-semibold tabular-nums">
                    {formatAmount(round2(newCashSale.quantity * newCashSale.unitPrice))}
                  </span>
                </div>
              )}
            </div>
            </div>
            <div className="shrink-0 flex justify-end gap-2 border-t border-gray-100 px-4 py-3 sm:px-5">
              <button
                type="button"
                onClick={() => setCashModalOpen(false)}
                disabled={cashSaving}
                className="rounded-lg border border-gray-200 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-60"
              >
                Скасаваць
              </button>
              <button
                type="button"
                onClick={() => void submitCashSale()}
                disabled={cashSaving}
                className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary/90 disabled:opacity-60"
              >
                {cashSaving ? 'Захаванне...' : 'Дадаць'}
              </button>
            </div>
          </div>
        </div>
      )}

      {unpaidLinkModalOpen && unpaidLinkTarget && (
        <div className="fixed inset-0 z-[80] flex items-end justify-center overflow-y-auto bg-black/40 p-3 sm:items-center sm:p-4">
          <div className="my-auto flex max-h-[min(640px,calc(100dvh-1.5rem))] w-full max-w-2xl min-h-0 flex-col overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl sm:my-0">
            <div className="shrink-0 border-b border-gray-100 px-4 py-3 sm:px-5">
              <div className="text-base font-semibold text-gray-900">Прывязаць да фактуры</div>
              <p className="mt-1 text-sm text-gray-600">
                {(() => {
                  const label = formatUnpaidProductLabel(unpaidLinkTarget, unpaidCatalogProducts);
                  return label.variantLabel
                    ? `${label.title} — ${label.variantLabel}`
                    : label.title;
                })()}
                {unpaidLinkTarget.supplierName ? ` · ${unpaidLinkTarget.supplierName}` : ''}
              </p>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-3 sm:px-5">
              {unpaidLinkOptionsLoading && (
                <div className="py-8 text-center text-sm text-gray-500">Загрузка варыянтаў...</div>
              )}
              {!unpaidLinkOptionsLoading && unpaidLinkOptions && (
                <div className="space-y-4">
                  <div className="space-y-2">
                    <label className="flex cursor-pointer items-start gap-2 rounded-lg border border-gray-200 px-3 py-2.5">
                      <input
                        type="radio"
                        name="unpaid-link-mode"
                        checked={unpaidLinkMode === 'replace'}
                        onChange={() => setUnpaidLinkMode('replace')}
                        disabled={unpaidLinkOptions.overpaidProducts.length === 0}
                        className="mt-0.5 accent-primary"
                      />
                      <span>
                        <span className="block text-sm font-medium text-gray-900">
                          Замяніць пераплачаны тавар у фактуре
                        </span>
                        <span className="mt-0.5 block text-xs text-gray-500">
                          Тавар, за які заплачана больш, чым прадана.
                        </span>
                      </span>
                    </label>
                    {unpaidLinkMode === 'replace' && (
                      <OverpaidExpenseProductSearchSelect
                        options={unpaidLinkOptions.overpaidProducts}
                        value={selectedOverpaidProductId}
                        onChange={setSelectedOverpaidProductId}
                        formatDate={formatDate}
                      />
                    )}
                    {unpaidLinkOptions.overpaidProducts.length === 0 && (
                      <p className="text-xs text-gray-500">Пераплачаных тавараў у гэтага пастаўшчыка няма.</p>
                    )}
                  </div>

                  <div className="space-y-3">
                    <label className="flex cursor-pointer items-start gap-2 rounded-lg border border-gray-200 px-3 py-2.5">
                      <input
                        type="radio"
                        name="unpaid-link-mode"
                        checked={unpaidLinkMode === 'link'}
                        onChange={() => setUnpaidLinkMode('link')}
                        disabled={
                          (unpaidLinkOptions.supplierInvoices.length === 0 &&
                            unpaidLinkOptions.supplierPaymentRecords.length === 0)
                        }
                        className="mt-0.5 accent-primary"
                      />
                      <span>
                        <span className="block text-sm font-medium text-gray-900">
                          Прывязаць да аплаты пастаўшчыка
                        </span>
                        <span className="mt-0.5 block text-xs text-gray-500">
                          Неаплачаны тавар пакрываецца выбранай фактурай або іншым запісам аплаты.
                        </span>
                      </span>
                    </label>
                    {unpaidLinkMode === 'link' && (
                      <div className="space-y-3 pl-1">
                        <label className="block space-y-1.5">
                          <span className="text-xs font-medium uppercase tracking-wide text-gray-500">
                            Фактура пастаўшчыка
                          </span>
                          <select
                            value={selectedInvoiceExpenseId}
                            onChange={(e) => {
                              setSelectedInvoiceExpenseId(e.target.value);
                              if (e.target.value) {
                                setUnpaidLinkSource('invoice');
                                setSelectedPaymentRecordExpenseId('');
                              }
                            }}
                            className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                          >
                            <option value="">Выберыце фактуру</option>
                            {unpaidLinkOptions.supplierInvoices.map((option) => (
                              <option key={`invoice-${option.expenseId}`} value={String(option.expenseId)}>
                                {formatSupplierExpenseOptionLabel(option)}
                              </option>
                            ))}
                          </select>
                          {unpaidLinkOptions.supplierInvoices.length === 0 && (
                            <p className="text-xs text-gray-500">Фактур з нумарам або файлам няма.</p>
                          )}
                        </label>

                        <label className="block space-y-1.5">
                          <span className="text-xs font-medium uppercase tracking-wide text-gray-500">
                            Любая аплата пастаўшчыку
                          </span>
                          <select
                            value={selectedPaymentRecordExpenseId}
                            onChange={(e) => {
                              setSelectedPaymentRecordExpenseId(e.target.value);
                              if (e.target.value) {
                                setUnpaidLinkSource('payment');
                                setSelectedInvoiceExpenseId('');
                              }
                            }}
                            className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                          >
                            <option value="">Выберыце аплату</option>
                            {unpaidLinkOptions.supplierPaymentRecords.map((option) => (
                              <option key={`payment-${option.expenseId}`} value={String(option.expenseId)}>
                                {formatSupplierExpenseOptionLabel(option)}
                              </option>
                            ))}
                          </select>
                          {unpaidLinkOptions.supplierPaymentRecords.length === 0 && (
                            <p className="text-xs text-gray-500">
                              Запісаў аплаты пастаўшчыка не знойдзена.
                            </p>
                          )}
                        </label>
                      </div>
                    )}
                  </div>

                  {unpaidLinkError && (
                    <p className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                      {unpaidLinkError}
                    </p>
                  )}
                </div>
              )}
            </div>
            <div className="shrink-0 flex justify-end gap-2 border-t border-gray-100 px-4 py-3 sm:px-5">
              <button
                type="button"
                onClick={() => {
                  setUnpaidLinkModalOpen(false);
                  setUnpaidLinkTarget(null);
                  setUnpaidLinkError(null);
                }}
                disabled={unpaidLinkSaving}
                className="rounded-lg border border-gray-200 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-60"
              >
                Скасаваць
              </button>
              <button
                type="button"
                onClick={() => void submitUnpaidLink()}
                disabled={unpaidLinkSaving || unpaidLinkOptionsLoading}
                className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary/90 disabled:opacity-60"
              >
                {unpaidLinkSaving ? 'Захаванне...' : 'Прывязаць'}
              </button>
            </div>
          </div>
        </div>
      )}

      {expenseModalOpen && (
        <div
          className="fixed inset-0 z-[80] flex items-end justify-center overflow-y-auto bg-black/40 p-3 sm:items-center sm:p-4"
        >
          <div
            className="my-auto flex max-h-[min(720px,calc(100dvh-1.5rem))] w-full max-w-2xl min-h-0 flex-col overflow-hidden rounded-xl border border-gray-200 bg-white shadow-xl sm:my-0"
          >
            <div className="shrink-0 border-b border-gray-100 px-4 py-3 sm:px-5">
              <div className="text-base font-semibold text-gray-900">
                {editingExpenseId !== null ? 'Змяніць расход' : 'Дадаць расход'}
              </div>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-3 sm:px-5">
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <label className="block space-y-1.5 sm:col-span-2">
                <span className="text-sm font-medium text-gray-700">Тып расходу</span>
                <select
                  value={newExpense.expenseInvoiceTypeId}
                  onChange={(e) => {
                    const expenseInvoiceTypeId = Number(e.target.value) || 0;
                    setNewExpense((prev) => ({ ...prev, expenseInvoiceTypeId }));
                    const nextType = expenseTypes.find((t) => t.id === expenseInvoiceTypeId);
                    if (nextType?.name !== SUPPLIER_PAYMENT_TYPE_NAME) {
                      setExpenseSupplierId(0);
                      setExpenseProductLines([]);
                      setExpenseProductSearch('');
                      setSupplierProducts([]);
                      setExpenseGrossOverride(null);
                      setExpenseVatOverride(null);
                    }
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                >
                  {expenseTypes.map((t) => (
                    <option key={t.id} value={t.id}>
                      {t.name}
                    </option>
                  ))}
                </select>
              </label>
              {isSupplierPaymentExpense && (
                <>
                  <label className="block space-y-1.5 sm:col-span-2">
                    <span className="text-sm font-medium text-gray-700">Пастаўшчык (неабавязкова)</span>
                    <select
                      value={expenseSupplierId || ''}
                      onChange={(e) => {
                        const nextSupplierId = Number(e.target.value) || 0;
                        setExpenseSupplierId(nextSupplierId);
                        setExpenseProductLines([]);
                        setExpenseProductSearch('');
                        setExpenseGrossOverride(null);
                        setExpenseVatOverride(null);
                      }}
                      className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                    >
                      <option value="">Без пастаўшчыка</option>
                      {expenseSuppliers.map((supplier) => (
                        <option key={supplier.id} value={supplier.id}>
                          {supplier.name}
                        </option>
                      ))}
                    </select>
                  </label>
                  <div className="space-y-2 sm:col-span-2">
                      <div className="flex items-center justify-between gap-3">
                        <span className="text-sm font-medium text-gray-700">Тавары для аплаты</span>
                        {expenseProductLines.length > 0 && (
                          <span className="text-xs text-gray-500">
                            Выбрана: {expenseProductLines.length}
                          </span>
                        )}
                      </div>
                      <input
                        type="search"
                        value={expenseProductSearch}
                        onChange={(e) => setExpenseProductSearch(e.target.value)}
                        placeholder="Пошук тавару..."
                        className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                      />
                      <div className="max-h-[min(10rem,28vh)] overflow-y-auto rounded-lg border border-gray-200">
                        {supplierProductsLoading && (
                          <div className="px-3 py-4 text-sm text-gray-500">Загрузка тавараў...</div>
                        )}
                        {!supplierProductsLoading && visibleSupplierProducts.length === 0 && (
                          <div className="px-3 py-4 text-sm text-gray-500">
                            {expenseSupplierId > 0
                              ? 'У гэтага пастаўшчыка няма тавараў у пастаўках.'
                              : 'Няма тавараў у каталогу.'}
                          </div>
                        )}
                        {!supplierProductsLoading &&
                          visibleSupplierProducts.map((pickerLine) => {
                            const line = expenseProductLines.find(
                              (item) => item.lineKey === pickerLine.lineKey
                            );
                            const selected = Boolean(line);
                            return (
                              <div
                                key={pickerLine.lineKey}
                                className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-gray-100 px-3 py-2 last:border-b-0"
                              >
                                <input
                                  type="checkbox"
                                  checked={selected}
                                  onChange={(e) => toggleExpenseProduct(pickerLine, e.target.checked)}
                                  className="size-4 shrink-0 rounded border-gray-300 accent-primary"
                                />
                                {pickerLine.mainImageUrl ? (
                                  <img
                                    src={pickerLine.mainImageUrl}
                                    alt={pickerLine.productName}
                                    className="h-10 w-8 shrink-0 rounded border border-gray-200 object-cover object-center"
                                    loading="lazy"
                                  />
                                ) : (
                                  <div className="h-10 w-8 shrink-0 rounded border border-gray-200 bg-gray-100" />
                                )}
                                <span className="min-w-0 flex-1 basis-[12rem] text-sm text-gray-800">
                                  <span className="line-clamp-2">
                                    {formatProductNameWithAuthor(
                                      pickerLine.productName,
                                      pickerLine.productAuthor
                                    )}
                                  </span>
                                  {pickerLine.variantName.trim() && (
                                    <span className="block text-xs text-gray-600">
                                      {pickerLine.variantName}
                                    </span>
                                  )}
                                  <span className="text-xs text-gray-500">
                                    VAT {formatAmount(pickerLine.vatRatePercent)}%
                                  </span>
                                </span>
                                {selected && (
                                  <div className="flex w-full shrink-0 flex-wrap items-center gap-2 sm:ml-auto sm:w-auto">
                                    <label className="inline-flex items-center gap-1.5 text-xs text-gray-600">
                                      <span>Кол.</span>
                                      <input
                                        type="number"
                                        min="1"
                                        step="1"
                                        value={line?.quantity ?? 1}
                                        onChange={(e) =>
                                          updateExpenseProductQuantity(
                                            pickerLine.lineKey,
                                            Number(e.target.value)
                                          )
                                        }
                                        className="w-16 rounded-md border border-gray-200 bg-white px-2 py-1 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                                      />
                                    </label>
                                    <label className="inline-flex items-center gap-1.5 text-xs text-gray-600">
                                      <span>Брута</span>
                                      <input
                                        type="number"
                                        min="0"
                                        step="0.01"
                                        value={line?.unitGrossPrice || ''}
                                        onChange={(e) =>
                                          updateExpenseProductUnitGrossPrice(
                                            pickerLine.lineKey,
                                            Number(e.target.value)
                                          )
                                        }
                                        className="w-20 rounded-md border border-gray-200 bg-white px-2 py-1 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                                      />
                                    </label>
                                  </div>
                                )}
                              </div>
                            );
                          })}
                      </div>
                    </div>
                </>
              )}
              {isSupplierPaymentExpense ? (
                <>
                  <label className="block space-y-1.5 sm:col-span-2">
                    <span className="text-sm font-medium text-gray-700">Сума брута</span>
                    <input
                      type="number"
                      step="0.01"
                      min={expenseProductGrossTotal || 0}
                      value={newExpense.grossAmount || ''}
                      onChange={(e) => {
                        const grossAmount = round2(Number(e.target.value) || 0);
                        if (grossAmount > expenseProductGrossTotal) {
                          setExpenseGrossOverride(grossAmount);
                        } else {
                          setExpenseGrossOverride(null);
                        }
                      }}
                      className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                    />
                    {expenseProductGrossTotal > 0 && (
                      <span className="text-xs text-gray-500">
                        Мінімум па таварах: {formatAmount(expenseProductGrossTotal)}
                      </span>
                    )}
                  </label>
                  <label className="block space-y-1.5">
                    <span className="text-sm font-medium text-gray-700">Сума VAT</span>
                    <input
                      type="number"
                      step="0.01"
                      min="0"
                      max={newExpense.grossAmount || undefined}
                      value={newExpense.vatAmount || ''}
                      onChange={(e) => {
                        const vatAmount = round2(Number(e.target.value) || 0);
                        if (vatAmount !== expenseProductVatTotal) {
                          setExpenseVatOverride(vatAmount);
                        } else {
                          setExpenseVatOverride(null);
                        }
                      }}
                      className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                    />
                    {expenseProductVatTotal > 0 && (
                      <span className="text-xs text-gray-500">
                        Па таварах: {formatAmount(expenseProductVatTotal)}
                      </span>
                    )}
                  </label>
                  <label className="block space-y-1.5">
                    <span className="text-sm font-medium text-gray-700">Сума нета</span>
                    <div className="w-full rounded-lg border border-gray-100 bg-gray-50 px-3 py-2 text-sm font-medium text-gray-900">
                      {formatAmount(newExpense.netAmount)}
                    </div>
                  </label>
                </>
              ) : (
                <>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Сума нета</span>
                <input
                  type="number"
                  step="0.01"
                  min="0"
                  value={newExpense.netAmount || ''}
                  onChange={(e) => {
                    const netAmount = Number(e.target.value) || 0;
                    setNewExpense((prev) => ({
                      ...prev,
                      ...syncExpenseAmounts(
                        { grossAmount: prev.grossAmount, vatAmount: prev.vatAmount, netAmount },
                        'net'
                      ),
                    }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Сума VAT</span>
                <input
                  type="number"
                  step="0.01"
                  min="0"
                  value={newExpense.vatAmount || ''}
                  onChange={(e) => {
                    const vatAmount = Number(e.target.value) || 0;
                    setNewExpense((prev) => ({
                      ...prev,
                      ...syncExpenseAmounts(
                        { grossAmount: prev.grossAmount, vatAmount, netAmount: prev.netAmount },
                        'vat'
                      ),
                    }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Сума брута</span>
                <input
                  type="number"
                  step="0.01"
                  min="0"
                  value={newExpense.grossAmount || ''}
                  onChange={(e) => {
                    const grossAmount = Number(e.target.value) || 0;
                    setNewExpense((prev) => ({
                      ...prev,
                      ...syncExpenseAmounts(
                        { grossAmount, vatAmount: prev.vatAmount, netAmount: prev.netAmount },
                        'gross'
                      ),
                    }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
                </>
              )}
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-gray-700">Дата</span>
                <input
                  type="date"
                  value={newExpense.expenseDateUtc}
                  onChange={(e) => {
                    const expenseDateUtc = e.target.value;
                    setNewExpense((prev) => ({ ...prev, expenseDateUtc }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <label className="block space-y-1.5 sm:col-span-2">
                <span className="text-sm font-medium text-gray-700">Нумар фактуры</span>
                <input
                  type="text"
                  value={newExpense.invoiceNumber}
                  onChange={(e) => {
                    const invoiceNumber = e.target.value;
                    setNewExpense((prev) => ({ ...prev, invoiceNumber }));
                  }}
                  placeholder="Напр. FV/06/2026/001"
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <label className="block space-y-1.5 sm:col-span-2">
                <span className="text-sm font-medium text-gray-700">Каментар</span>
                <textarea
                  rows={1}
                  value={newExpense.comment}
                  onChange={(e) => {
                    const comment = e.target.value;
                    setNewExpense((prev) => ({ ...prev, comment }));
                  }}
                  className="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
                />
              </label>
              <div className="block space-y-1.5 sm:col-span-2">
                <span className="text-sm font-medium text-gray-700">Фактура</span>
                <div className="flex items-center gap-3 rounded-lg border border-gray-200 bg-white px-3 py-2">
                  <label className="cursor-pointer rounded-md bg-gray-100 px-2 py-1 text-xs font-medium text-gray-700 transition hover:bg-gray-200">
                    Выберыце файл
                    <input
                      type="file"
                      accept=".pdf,.png,.jpg,.jpeg,.webp"
                      onChange={(e) => setExpenseInvoiceFile(e.target.files?.[0] ?? null)}
                      className="hidden"
                    />
                  </label>
                  <span className="truncate text-sm text-gray-500">
                    {expenseInvoiceFile?.name ??
                      editingExpenseInvoiceFileName ??
                      'Файл не выбраны'}
                  </span>
                </div>
              </div>
              <label className="inline-flex items-center gap-2 text-sm font-medium text-gray-700 sm:col-span-2">
                <input
                  type="checkbox"
                  checked={newExpense.isPaid}
                  onChange={(e) => {
                    const isPaid = e.target.checked;
                    setNewExpense((prev) => ({ ...prev, isPaid }));
                  }}
                  className="size-4 rounded border-gray-300 accent-primary"
                />
                Аплочана
              </label>
              <label className="inline-flex items-center gap-2 text-sm font-medium text-gray-700 sm:col-span-2">
                <input
                  type="checkbox"
                  checked={newExpense.isByProsvet}
                  onChange={(e) => {
                    const isByProsvet = e.target.checked;
                    setNewExpense((prev) => ({ ...prev, isByProsvet }));
                  }}
                  className="size-4 rounded border-gray-300 accent-primary"
                />
                ByProsvet
              </label>
              <label className="inline-flex items-center gap-2 text-sm font-medium text-gray-700 sm:col-span-2">
                <input
                  type="checkbox"
                  checked={newExpense.includeVatInTotal}
                  onChange={(e) => {
                    const includeVatInTotal = e.target.checked;
                    setNewExpense((prev) => ({ ...prev, includeVatInTotal }));
                  }}
                  className="size-4 rounded border-gray-300 accent-primary"
                />
                Улічваць VAT
              </label>
            </div>
            </div>
            <div className="shrink-0 border-t border-gray-100 px-4 py-3 sm:px-5">
            <div className="flex items-center justify-end gap-2">
              <button
                type="button"
                onClick={() => {
                  setExpenseModalOpen(false);
                  resetNewExpenseForm();
                }}
                disabled={expenseSaving}
                className="rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 transition hover:bg-gray-50 disabled:opacity-60"
              >
                Адмена
              </button>
              <button
                type="button"
                onClick={submitExpense}
                disabled={expenseSaving || !newExpense.expenseInvoiceTypeId}
                className="inline-flex min-w-24 items-center justify-center rounded-lg border border-primary bg-primary px-3 py-1.5 text-sm font-medium text-white transition hover:bg-primary/90 disabled:opacity-60"
              >
                {expenseSaving ? (
                  <span className="size-4 animate-spin rounded-full border-2 border-primary/20 border-t-white" />
                ) : editingExpenseId !== null ? (
                  'Захаваць'
                ) : (
                  'Дадаць'
                )}
              </button>
            </div>
            </div>
          </div>
        </div>
      )}

      {pendingDeleteRow && (
        <div
          className="fixed inset-0 z-[80] flex items-center justify-center bg-black/40 p-4"
        >
          <div
            className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
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
        >
          <div
            className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
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
                onChange={(e) => setMoveToForeignName(e.target.value)}
                placeholder="Увядзіце імя атрымальніка"
                disabled={!!movingToForeignRowKey}
                className="mt-1 w-full rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-800 transition placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20 disabled:opacity-60"
              />
            </label>
            <label className="mt-4 block text-sm font-medium text-gray-700">
              Адрас
              <textarea
                value={moveToForeignAddress}
                onChange={(e) => setMoveToForeignAddress(e.target.value)}
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
        >
          <div
            className="w-full max-w-md rounded-xl border border-gray-200 bg-white p-5 shadow-xl"
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
