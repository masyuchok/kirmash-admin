import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';
import { normalizeAgeRating } from '@/lib/books/ageRating';
import {
  languageFromOcrCode,
  normalizeBookFormat,
  normalizeIllustrator,
  resolveLanguage,
  normalizePageCount,
  normalizePlace,
  normalizeTranslation,
  normalizeYear,
} from '@/lib/books/bibliographicFields';

export type BookLookupCandidate = {
  title: string;
  author?: string | null;
  isbn?: string | null;
  publisher?: string | null;
  description?: string | null;
  coverImageUrl?: string | null;
  additionalImageUrls?: string[] | null;
  weightKg?: number | null;
  salePrice?: number | null;
  coverType?: string | null;
  ageRating?: string | null;
  format?: string | null;
  illustrator?: string | null;
  language?: string | null;
  pageCount?: number | null;
  placeOfPublication?: string | null;
  translation?: string | null;
  year?: number | null;
  url: string;
  source: string;
  snippet?: string | null;
};

export type BookCreateFromLookupResult = {
  shopifyProductId: string;
  shopifyVariantId: string;
  title: string;
  shopifyAdminUrl?: string;
};

function mapStringList(raw: unknown): string[] {
  if (!Array.isArray(raw)) return [];
  return raw
    .map((item) => String(item ?? '').trim())
    .filter((item) => /^https?:\/\//i.test(item));
}

function mapOptionalNumber(raw: unknown): number | null {
  if (raw == null || raw === '') return null;
  const n =
    typeof raw === 'number' ? raw : Number(String(raw).replace(',', '.'));
  return Number.isFinite(n) && n > 0 ? n : null;
}

function mapOptionalCoverType(raw: unknown): 'soft' | 'hard' | null {
  const t = String(raw ?? '')
    .trim()
    .toLowerCase();
  if (!t) return null;
  if (/мягк|мякк|soft|paperback|miękk|miekk|broszur/.test(t)) return 'soft';
  if (/твёрд|тверд|цвёрд|цверд|hard|hardcover|hardback|tward/.test(t))
    return 'hard';
  if (t === 'soft' || t === 'hard') return t;
  return null;
}

function mapOptionalAgeRating(raw: unknown): string | null {
  return normalizeAgeRating(raw == null ? null : String(raw));
}

function mapOptionalText(raw: unknown): string | null {
  const t = String(raw ?? '').trim();
  return t || null;
}

function toOptionalString(raw: unknown): string | null {
  if (raw == null) return null;
  const t = String(raw).trim();
  return t || null;
}

function mapOptionalInt(raw: unknown): number | null {
  if (raw == null || raw === '') return null;
  const n =
    typeof raw === 'number' ? raw : Number(String(raw).replace(',', '.'));
  return Number.isFinite(n) && n > 0 ? Math.round(n) : null;
}

function mapBibliographicFields(raw: Record<string, unknown>): {
  format: string | null;
  illustrator: string | null;
  language: string | null;
  pageCount: number | null;
  placeOfPublication: string | null;
  translation: string | null;
  year: number | null;
} {
  const formatRaw = raw.format ?? raw.Format;
  const illustratorRaw = raw.illustrator ?? raw.Illustrator;
  const languageRaw = raw.language ?? raw.Language;
  const pagesRaw = raw.pageCount ?? raw.PageCount;
  const placeRaw = raw.placeOfPublication ?? raw.PlaceOfPublication;
  const translationRaw = raw.translation ?? raw.Translation;
  const yearRaw = raw.year ?? raw.Year;
  return {
    format:
      normalizeBookFormat(formatRaw == null ? null : String(formatRaw)) ||
      mapOptionalText(formatRaw),
    illustrator: normalizeIllustrator(
      illustratorRaw == null ? null : String(illustratorRaw)
    ),
    language:
      resolveLanguage(
        languageRaw == null ? null : String(languageRaw),
        toOptionalString(raw.description ?? raw.Description),
        toOptionalString(raw.title ?? raw.Title),
        toOptionalString(raw.snippet ?? raw.Snippet)
      ) ||
      languageFromOcrCode(languageRaw == null ? null : String(languageRaw)),
    pageCount:
      normalizePageCount(pagesRaw == null ? null : String(pagesRaw)) ||
      mapOptionalInt(pagesRaw),
    placeOfPublication:
      normalizePlace(placeRaw == null ? null : String(placeRaw)) ||
      mapOptionalText(placeRaw),
    translation:
      normalizeTranslation(
        translationRaw == null ? null : String(translationRaw)
      ) || mapOptionalText(translationRaw),
    year:
      normalizeYear(yearRaw == null ? null : String(yearRaw)) ||
      mapOptionalInt(yearRaw),
  };
}

function mapCandidate(raw: Record<string, unknown>): BookLookupCandidate {
  return {
    title: String(raw.title ?? raw.Title ?? ''),
    author: (raw.author ?? raw.Author ?? null) as string | null,
    isbn: (raw.isbn ?? raw.Isbn ?? null) as string | null,
    publisher: (raw.publisher ?? raw.Publisher ?? null) as string | null,
    description: (raw.description ?? raw.Description ?? null) as string | null,
    coverImageUrl: (raw.coverImageUrl ?? raw.CoverImageUrl ?? null) as
      | string
      | null,
    additionalImageUrls: mapStringList(
      raw.additionalImageUrls ?? raw.AdditionalImageUrls
    ),
    weightKg: mapOptionalNumber(raw.weightKg ?? raw.WeightKg),
    salePrice: mapOptionalNumber(raw.salePrice ?? raw.SalePrice),
    coverType: mapOptionalCoverType(raw.coverType ?? raw.CoverType),
    ageRating: mapOptionalAgeRating(raw.ageRating ?? raw.AgeRating),
    ...mapBibliographicFields(raw),
    url: String(raw.url ?? raw.Url ?? ''),
    source: String(raw.source ?? raw.Source ?? 'manual'),
    snippet: (raw.snippet ?? raw.Snippet ?? null) as string | null,
  };
}

export async function lookupBookFromUrl(input: {
  sessionId?: string;
  url: string;
}): Promise<BookLookupCandidate> {
  const res = await fetch(`${getApiBaseUrl()}/books/lookup/from-url`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      sessionId: input.sessionId || null,
      url: input.url,
    }),
  });
  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося прачытаць спасылку')
    );
  }
  const raw = (await res.json()) as Record<string, unknown>;
  return mapCandidate(raw);
}

export async function lookupSupplierCost(input: {
  supplierId: number;
  title?: string;
  isbn?: string;
  author?: string;
}): Promise<{
  unitCostBrutto: number | null;
  weightKg: number | null;
  coverType: 'soft' | 'hard' | null;
  ageRating: string | null;
  format: string | null;
  illustrator: string | null;
  language: string | null;
  pageCount: number | null;
  placeOfPublication: string | null;
  translation: string | null;
  year: number | null;
  priceListRowText: string | null;
}> {
  const empty = {
    unitCostBrutto: null as number | null,
    weightKg: null as number | null,
    coverType: null as 'soft' | 'hard' | null,
    ageRating: null as string | null,
    format: null as string | null,
    illustrator: null as string | null,
    language: null as string | null,
    pageCount: null as number | null,
    placeOfPublication: null as string | null,
    translation: null as string | null,
    year: null as number | null,
    priceListRowText: null as string | null,
  };
  const res = await fetch(`${getApiBaseUrl()}/books/lookup-supplier-cost`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      supplierId: input.supplierId,
      title: input.title || null,
      isbn: input.isbn || null,
      author: input.author || null,
    }),
  });
  if (!res.ok) {
    return empty;
  }
  const raw = (await res.json()) as Record<string, unknown>;
  const rowText = String(
    raw.priceListRowText ?? raw.PriceListRowText ?? ''
  ).trim();
  const biblio = mapBibliographicFields(raw);
  return {
    unitCostBrutto: mapOptionalNumber(raw.unitCostBrutto ?? raw.UnitCostBrutto),
    weightKg: mapOptionalNumber(raw.weightKg ?? raw.WeightKg),
    coverType: mapOptionalCoverType(raw.coverType ?? raw.CoverType),
    ageRating: mapOptionalAgeRating(raw.ageRating ?? raw.AgeRating),
    ...biblio,
    priceListRowText: rowText || null,
  };
}

export async function fetchBookGenreOptions(): Promise<{
  namespace: string;
  key: string;
  type: string;
  options: string[];
}> {
  const res = await fetch(`${getApiBaseUrl()}/books/genre-options`, {
    method: 'GET',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося загрузіць жанры'));
  }
  const raw = (await res.json()) as Record<string, unknown>;
  const optionsRaw = raw.options ?? raw.Options;
  const options = Array.isArray(optionsRaw)
    ? optionsRaw
        .map((item) => String(item ?? '').trim())
        .filter((item) => item.length > 0)
    : [];
  return {
    namespace: String(raw.namespace ?? raw.Namespace ?? 'book'),
    key: String(raw.key ?? raw.Key ?? 'genre'),
    type: String(raw.type ?? raw.Type ?? 'list.single_line_text_field'),
    options,
  };
}

export async function fetchBookVendorOptions(): Promise<string[]> {
  const res = await fetch(`${getApiBaseUrl()}/books/vendor-options`, {
    method: 'GET',
    credentials: apiCredentials,
  });
  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць выдаўцоў (Vendor)')
    );
  }
  const raw = (await res.json()) as Record<string, unknown>;
  const optionsRaw = raw.options ?? raw.Options;
  if (!Array.isArray(optionsRaw)) return [];
  return optionsRaw
    .map((item) => String(item ?? '').trim())
    .filter((item) => item.length > 0);
}

export async function suggestBookVendor(input: {
  publisher?: string | null;
  supplierPageSnippet?: string | null;
  priceListRowText?: string | null;
  title?: string | null;
  author?: string | null;
  isbn?: string | null;
  supplierId?: number | null;
}): Promise<string | null> {
  const res = await fetch(`${getApiBaseUrl()}/books/suggest-vendor`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      publisher: input.publisher || null,
      supplierPageSnippet: input.supplierPageSnippet || null,
      priceListRowText: input.priceListRowText || null,
      title: input.title || null,
      author: input.author || null,
      isbn: input.isbn || null,
      supplierId: input.supplierId ?? null,
    }),
  });
  if (!res.ok) {
    return null;
  }
  const raw = (await res.json()) as Record<string, unknown>;
  const vendor = String(raw.vendor ?? raw.Vendor ?? '').trim();
  return vendor || null;
}

export async function suggestBookGenres(input: {
  title?: string;
  author?: string;
  description?: string;
  coverType?: string | null;
  publisher?: string;
  isbn?: string;
  supplierPageSnippet?: string;
  priceListRowText?: string | null;
  supplierId?: number | null;
  selectedGenres?: string[];
}): Promise<string[]> {
  const res = await fetch(`${getApiBaseUrl()}/books/suggest-genres`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      title: input.title || null,
      author: input.author || null,
      description: input.description || null,
      coverType: input.coverType || null,
      publisher: input.publisher || null,
      isbn: input.isbn || null,
      supplierPageSnippet: input.supplierPageSnippet || null,
      priceListRowText: input.priceListRowText || null,
      supplierId: input.supplierId ?? null,
      selectedGenres: input.selectedGenres ?? [],
    }),
  });
  if (!res.ok) {
    return [];
  }
  const raw = (await res.json()) as Record<string, unknown>;
  const genresRaw = raw.genres ?? raw.Genres;
  if (!Array.isArray(genresRaw)) return [];
  return genresRaw
    .map((item) => String(item ?? '').trim())
    .filter((item) => item.length > 0);
}

export async function styleBookCover(input: {
  sessionId?: string;
  coverImageUrl?: string;
  coverImageBase64?: string;
  coverTempMediaId?: string;
}): Promise<{
  dataUrl: string;
  tempMediaId?: string;
  tempMediaPath?: string;
}> {
  const res = await fetch(`${getApiBaseUrl()}/books/style-cover`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      sessionId: input.sessionId || null,
      coverImageUrl: input.coverImageUrl || null,
      coverImageBase64: input.coverImageBase64 || null,
      coverTempMediaId: input.coverTempMediaId || null,
    }),
  });
  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося апрацаваць вокладку')
    );
  }
  const raw = (await res.json()) as Record<string, unknown>;
  const dataUrl = String(
    raw.styledCoverDataUrl ?? raw.StyledCoverDataUrl ?? ''
  ).trim();
  const tempMediaId = String(raw.tempMediaId ?? raw.TempMediaId ?? '').trim();
  const tempMediaPath = String(
    raw.tempMediaPath ?? raw.TempMediaPath ?? ''
  ).trim();
  return {
    dataUrl,
    tempMediaId: tempMediaId || undefined,
    tempMediaPath: tempMediaPath || undefined,
  };
}

export type CachedRemoteImage = {
  sourceUrl: string;
  found: boolean;
  tempMediaId?: string;
  tempMediaPath?: string;
  dataUrl?: string;
  /** Diagnostic fields from fetch-cover when Found=false. */
  statusCode?: number;
  contentType?: string;
  byteLength?: number;
  error?: string;
  finalUrl?: string;
  /** direct | relay — which backend downloader produced the bytes. */
  fetchSource?: string;
  directStatus?: number;
  relayConfigured?: boolean;
  relayAttempted?: boolean;
  shouldFallbackToRelay?: boolean;
  relayStatus?: number;
  relayError?: string;
  relayUrlHost?: string;
};

function optionalNumber(value: unknown): number | undefined {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (value == null || value === '') return undefined;
  const n = Number(value);
  return Number.isFinite(n) ? n : undefined;
}

function optionalBool(value: unknown): boolean | undefined {
  if (typeof value === 'boolean') return value;
  if (value == null || value === '') return undefined;
  if (value === 'true' || value === 'True' || value === 1) return true;
  if (value === 'false' || value === 'False' || value === 0) return false;
  return undefined;
}

export async function cacheRemoteImage(
  imageUrl: string,
  opts?: { pageUrl?: string | null }
): Promise<CachedRemoteImage | null> {
  const url = imageUrl.trim();
  if (!/^https?:\/\//i.test(url)) return null;
  const pageUrl = opts?.pageUrl?.trim() || null;
  try {
    const res = await fetch(`${getApiBaseUrl()}/books/fetch-cover`, {
      method: 'POST',
      credentials: apiCredentials,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url, pageUrl }),
    });
    if (!res.ok) {
      let detail = `fetch-cover HTTP ${res.status}`;
      try {
        const errBody = (await res.json()) as Record<string, unknown>;
        const msg = String(errBody.error ?? errBody.Error ?? '').trim();
        if (msg) detail = `${detail}: ${msg}`;
      } catch {
        // ignore non-JSON error bodies
      }
      return {
        sourceUrl: url,
        found: false,
        statusCode: res.status,
        error: detail,
      };
    }
    const raw = (await res.json()) as Record<string, unknown>;
    const found = Boolean(raw.found ?? raw.Found);
    const dataUrl = String(
      raw.coverImageBase64 ?? raw.CoverImageBase64 ?? ''
    ).trim();
    const tempMediaId = String(raw.tempMediaId ?? raw.TempMediaId ?? '').trim();
    const tempMediaPath = String(
      raw.tempMediaPath ?? raw.TempMediaPath ?? ''
    ).trim();
    const statusCodeRaw = raw.statusCode ?? raw.StatusCode;
    const statusCode =
      typeof statusCodeRaw === 'number'
        ? statusCodeRaw
        : statusCodeRaw != null
          ? Number(statusCodeRaw)
          : undefined;
    const byteLengthRaw = raw.byteLength ?? raw.ByteLength;
    const byteLength =
      typeof byteLengthRaw === 'number'
        ? byteLengthRaw
        : byteLengthRaw != null
          ? Number(byteLengthRaw)
          : undefined;
    return {
      sourceUrl: String(raw.sourceUrl ?? raw.SourceUrl ?? url),
      found,
      dataUrl: dataUrl.startsWith('data:') ? dataUrl : undefined,
      tempMediaId: tempMediaId || undefined,
      tempMediaPath: tempMediaPath || undefined,
      statusCode: Number.isFinite(statusCode) ? statusCode : undefined,
      contentType:
        String(raw.contentType ?? raw.ContentType ?? '') || undefined,
      byteLength: Number.isFinite(byteLength) ? byteLength : undefined,
      error: String(raw.error ?? raw.Error ?? '') || undefined,
      finalUrl: String(raw.finalUrl ?? raw.FinalUrl ?? '') || undefined,
      fetchSource:
        String(raw.fetchSource ?? raw.FetchSource ?? '').toLowerCase() ||
        undefined,
      directStatus: optionalNumber(raw.directStatus ?? raw.DirectStatus),
      relayConfigured: optionalBool(raw.relayConfigured ?? raw.RelayConfigured),
      relayAttempted: optionalBool(raw.relayAttempted ?? raw.RelayAttempted),
      shouldFallbackToRelay: optionalBool(
        raw.shouldFallbackToRelay ?? raw.ShouldFallbackToRelay
      ),
      relayStatus: optionalNumber(raw.relayStatus ?? raw.RelayStatus),
      relayError: String(raw.relayError ?? raw.RelayError ?? '') || undefined,
      relayUrlHost:
        String(raw.relayUrlHost ?? raw.RelayUrlHost ?? '') || undefined,
    };
  } catch (err) {
    return {
      sourceUrl: url,
      found: false,
      error: err instanceof Error ? err.message : 'fetch-cover network error',
    };
  }
}

/**
 * Ensure remote image bytes live in our temp store (server downloads CDN).
 * Preview then uses same-origin /books/temp-media/{id}.
 * Always return the server payload (including Found=false diagnostics).
 */
export async function ensureRemoteImageInTemp(
  imageUrl: string,
  opts?: { pageUrl?: string | null }
): Promise<CachedRemoteImage | null> {
  const url = imageUrl.trim();
  if (!/^https?:\/\//i.test(url)) return null;
  return cacheRemoteImage(url, opts);
}

export function tempMediaAbsoluteUrl(tempMediaPath: string): string {
  const path = tempMediaPath.startsWith('/')
    ? tempMediaPath
    : `/${tempMediaPath}`;
  // getApiBaseUrl is like https://host/api — temp path is /books/...
  return `${getApiBaseUrl()}${path}`;
}

export async function createBookDraftShell(input: {
  title: string;
  descriptionHtml?: string;
  salePrice?: number;
  coverImageUrl?: string;
  useCoverImage?: boolean;
  additionalImageUrls?: string[];
  isbn?: string;
  weightKg?: number;
  quantity?: number;
  author?: string;
  coverType?: string;
  ageRating?: string;
  format?: string;
  illustrator?: string;
  language?: string;
  pageCount?: number;
  placeOfPublication?: string;
  translation?: string;
  year?: number;
  publisher?: string;
  genres?: string[];
  seoImageIds?: string[];
}): Promise<
  BookCreateFromLookupResult & {
    imageAlts: { imageId: string; alt: string }[];
  }
> {
  const res = await fetch(`${getApiBaseUrl()}/books/create-draft-shell`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      title: input.title,
      descriptionHtml: input.descriptionHtml || null,
      salePrice: input.salePrice ?? 0,
      coverImageUrl: input.coverImageUrl || null,
      coverImageBase64: null,
      coverAlreadyStyled: false,
      useCoverImage: input.useCoverImage ?? true,
      additionalImageUrls: input.additionalImageUrls ?? [],
      isbn: input.isbn?.trim() || null,
      weightKg:
        input.weightKg != null &&
        Number.isFinite(input.weightKg) &&
        input.weightKg > 0
          ? input.weightKg
          : null,
      quantity:
        input.quantity != null &&
        Number.isFinite(input.quantity) &&
        input.quantity > 0
          ? Math.floor(input.quantity)
          : null,
      author: input.author?.trim() || null,
      coverType: input.coverType?.trim() || null,
      ageRating: input.ageRating?.trim() || null,
      format: input.format?.trim() || null,
      illustrator: input.illustrator?.trim() || null,
      language: input.language?.trim() || null,
      pageCount:
        input.pageCount != null &&
        Number.isFinite(input.pageCount) &&
        input.pageCount > 0
          ? Math.floor(input.pageCount)
          : null,
      placeOfPublication: input.placeOfPublication?.trim() || null,
      translation: input.translation?.trim() || null,
      year:
        input.year != null && Number.isFinite(input.year) && input.year > 0
          ? Math.floor(input.year)
          : null,
      publisher: input.publisher?.trim() || null,
      genres: input.genres ?? [],
      seoImageIds: input.seoImageIds ?? ['cover'],
    }),
  });
  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося стварыць чарнавік у Shopify')
    );
  }
  const raw = (await res.json()) as Record<string, unknown>;
  const adminUrl = String(
    raw.shopifyAdminUrl ?? raw.ShopifyAdminUrl ?? ''
  ).trim();
  const altsRaw = raw.imageAlts ?? raw.ImageAlts;
  const imageAlts = Array.isArray(altsRaw)
    ? altsRaw
        .map((item) => {
          const row = item as Record<string, unknown>;
          return {
            imageId: String(row.imageId ?? row.ImageId ?? '').trim(),
            alt: String(row.alt ?? row.Alt ?? '').trim(),
          };
        })
        .filter((a) => a.imageId && a.alt)
    : [];
  return {
    shopifyProductId: String(
      raw.shopifyProductId ?? raw.ShopifyProductId ?? ''
    ),
    shopifyVariantId: String(
      raw.shopifyVariantId ?? raw.ShopifyVariantId ?? ''
    ),
    title: String(raw.title ?? raw.Title ?? ''),
    shopifyAdminUrl: adminUrl || undefined,
    imageAlts,
  };
}

/** Upload modal preview bytes / temp-cached media to an existing Shopify draft. */
export async function attachDraftImages(input: {
  shopifyProductId: string;
  coverDataUrl?: string | null;
  coverImageUrl?: string | null;
  coverTempMediaId?: string | null;
  additionalDataUrls?: string[];
  additionalImageUrls?: string[];
  additionalTempMediaIds?: string[];
  coverImageAlt?: string | null;
  additionalImageAlts?: string[];
}): Promise<{
  attachedCount: number;
  errors: string[];
  attachedMediaIds: string[];
}> {
  const res = await fetch(`${getApiBaseUrl()}/books/attach-draft-images`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      shopifyProductId: input.shopifyProductId,
      coverImageBase64: input.coverDataUrl?.trim() || null,
      additionalImageBase64: (input.additionalDataUrls ?? []).filter((u) =>
        u?.trim().startsWith('data:')
      ),
      coverImageUrl: input.coverImageUrl?.trim() || null,
      additionalImageUrls: (input.additionalImageUrls ?? []).filter((u) =>
        Boolean(u?.trim())
      ),
      coverTempMediaId: input.coverTempMediaId?.trim() || null,
      additionalTempMediaIds: (input.additionalTempMediaIds ?? []).filter(
        (id) => Boolean(id?.trim())
      ),
      coverImageAlt: input.coverImageAlt?.trim() || null,
      additionalImageAlts: input.additionalImageAlts ?? [],
    }),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося загрузіць фота'));
  }
  const raw = (await res.json()) as Record<string, unknown>;
  const errorsRaw = raw.errors ?? raw.Errors;
  const mediaRaw = raw.attachedMediaIds ?? raw.AttachedMediaIds;
  const attachedCount =
    Number(raw.attachedCount ?? raw.AttachedCount ?? 0) || 0;
  const errors = Array.isArray(errorsRaw)
    ? errorsRaw.map((e) => String(e ?? '')).filter(Boolean)
    : [];
  const attachedMediaIds = Array.isArray(mediaRaw)
    ? mediaRaw.map((e) => String(e ?? '')).filter(Boolean)
    : [];
  if (attachedCount <= 0) {
    throw new Error(errors[0] || 'Фота не загрузіліся ў Shopify (0 attached).');
  }
  return { attachedCount, errors, attachedMediaIds };
}
