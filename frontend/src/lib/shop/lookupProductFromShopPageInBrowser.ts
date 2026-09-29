import type { BookLookupCandidate } from '@/lib/api/bookLookup';
import { isAgeAttributeLabel, normalizeAgeRating } from '@/lib/books/ageRating';
import {
  isFormatAttributeLabel,
  isIllustratorAttributeLabel,
  isLanguageAttributeLabel,
  isPageCountAttributeLabel,
  isPlaceAttributeLabel,
  isTranslationAttributeLabel,
  isYearAttributeLabel,
  extractIllustratorFromText,
  normalizeBookFormat,
  normalizeIllustrator,
  normalizeLanguage,
  resolveLanguage,
  normalizePageCount,
  normalizePlace,
  normalizeTranslation,
  normalizeYear,
} from '@/lib/books/bibliographicFields';

/**
 * Read product title/author/ISBN from the shop itself in the browser via
 * WordPress JSONP (no CORS). Uses the visitor's IP, so Cloudflare that blocks
 * our server still allows the real product payload for that page URL.
 */
export function lookupProductFromShopPageInBrowser(
  productUrl: string
): Promise<BookLookupCandidate | null> {
  return new Promise((resolve) => {
    let parsed: URL;
    try {
      parsed = new URL(productUrl.trim());
    } catch {
      resolve(null);
      return;
    }

    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
      resolve(null);
      return;
    }

    const slugMatch = parsed.pathname.match(
      /\/(?:pradukt|produkt|product)\/([^/]+)\/?/i
    );
    if (!slugMatch?.[1]) {
      resolve(null);
      return;
    }

    const slug = decodeURIComponent(slugMatch[1]);
    const host = parsed.host;
    const callbackName = `__kirmaShopJsonp_${Date.now()}_${Math.floor(Math.random() * 1e6)}`;
    const endpoints = [
      `https://${host}/wp-json/wc/store/v1/products?slug=${encodeURIComponent(slug)}&_jsonp=${callbackName}`,
      `https://${host}/wp-json/wc/store/products?slug=${encodeURIComponent(slug)}&_jsonp=${callbackName}`,
      `https://${host}/wp-json/wp/v2/product?slug=${encodeURIComponent(slug)}&_jsonp=${callbackName}`,
    ];

    let endpointIndex = 0;
    let settled = false;
    let script: HTMLScriptElement | null = null;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const cleanup = () => {
      if (timer) clearTimeout(timer);
      timer = null;
      if (script?.parentNode) script.parentNode.removeChild(script);
      script = null;
      try {
        delete (window as unknown as Record<string, unknown>)[callbackName];
      } catch {
        (window as unknown as Record<string, unknown>)[callbackName] =
          undefined;
      }
    };

    const finish = (value: BookLookupCandidate | null) => {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(value);
    };

    const tryNext = () => {
      if (endpointIndex >= endpoints.length) {
        finish(null);
        return;
      }

      const src = endpoints[endpointIndex++];
      if (script?.parentNode) script.parentNode.removeChild(script);

      script = document.createElement('script');
      script.async = true;
      script.src = src;
      script.onerror = () => tryNext();
      document.head.appendChild(script);
    };

    (window as unknown as Record<string, unknown>)[callbackName] = (
      payload: unknown
    ) => {
      try {
        const product = Array.isArray(payload) ? payload[0] : payload;
        const mapped = mapShopProduct(product, parsed);
        if (mapped?.title) {
          finish(mapped);
          return;
        }
      } catch {
        // try next endpoint
      }
      tryNext();
    };

    timer = setTimeout(() => finish(null), 15000);
    tryNext();
  });
}

function mapShopProduct(
  product: unknown,
  pageUrl: URL
): BookLookupCandidate | null {
  if (!product || typeof product !== 'object') return null;
  const p = product as Record<string, unknown>;

  const nameRaw =
    (typeof p.name === 'string' && p.name) ||
    (p.title &&
      typeof p.title === 'object' &&
      typeof (p.title as { rendered?: string }).rendered === 'string' &&
      (p.title as { rendered: string }).rendered) ||
    '';
  const name = decodeHtml(String(nameRaw)).trim();
  if (!name || name.length < 3) return null;

  let author: string | null = null;
  const authorParts: string[] = [];
  let isbn: string | null = null;
  let publisher: string | null = null;
  let weightRaw: string | null = null;
  let coverRaw: string | null = null;
  let ageRaw: string | null = null;
  let formatRaw: string | null = null;
  let illustratorRaw: string | null = null;
  let languageRaw: string | null = null;
  let pagesRaw: string | null = null;
  let placeRaw: string | null = null;
  let translationRaw: string | null = null;
  let yearRaw: string | null = null;
  const attrs = Array.isArray(p.attributes) ? p.attributes : [];
  for (const attr of attrs) {
    if (!attr || typeof attr !== 'object') continue;
    const a = attr as Record<string, unknown>;
    const taxonomy = String(a.taxonomy || '');
    const attrName = String(a.name || '');
    const terms = Array.isArray(a.terms) ? a.terms : [];
    const options = Array.isArray(a.options) ? a.options : [];
    const values: string[] = [];
    for (const term of terms) {
      if (
        term &&
        typeof term === 'object' &&
        typeof (term as { name?: string }).name === 'string'
      ) {
        const v = decodeHtml((term as { name: string }).name).trim();
        if (v) values.push(v);
      }
    }
    for (const opt of options) {
      if (typeof opt === 'string') {
        const v = decodeHtml(opt).trim();
        if (v) values.push(v);
      }
    }
    if (values.length === 0) continue;
    const joined = values.join(' ');
    if (
      /autar|author/i.test(taxonomy) ||
      /аўтар|автор|author/i.test(attrName)
    ) {
      authorParts.push(...values);
      author ??= values[0];
    } else if (/isbn/i.test(taxonomy) || /isbn/i.test(attrName)) {
      isbn ??= values[0];
    } else if (
      /vydav|publisher/i.test(taxonomy) ||
      /выдавец|издател|publisher/i.test(attrName)
    ) {
      publisher ??= values[0];
    } else if (
      /weight|vaga|ves/i.test(taxonomy) ||
      /вага|вес|weight|кг/i.test(attrName)
    ) {
      weightRaw ??= values[0];
    } else if (
      /cover|binding|opraw|voklad/i.test(taxonomy) ||
      /воклад|облож|opraw|binding|cover|пераплёт|перепл/i.test(attrName)
    ) {
      coverRaw ??= values[0];
    } else if (isAgeAttributeLabel(taxonomy, attrName)) {
      ageRaw ??= normalizeAgeRating(joined);
      for (const v of values) ageRaw ??= normalizeAgeRating(v);
    } else if (isFormatAttributeLabel(taxonomy, attrName)) {
      formatRaw ??= normalizeBookFormat(joined) || joined;
    } else if (isIllustratorAttributeLabel(taxonomy, attrName)) {
      illustratorRaw ??= normalizeIllustrator(joined);
    } else if (isLanguageAttributeLabel(taxonomy, attrName)) {
      languageRaw ??= normalizeLanguage(joined);
    } else if (isPageCountAttributeLabel(taxonomy, attrName)) {
      pagesRaw ??= joined;
    } else if (isPlaceAttributeLabel(taxonomy, attrName)) {
      placeRaw ??= normalizePlace(joined) || joined;
    } else if (isTranslationAttributeLabel(taxonomy, attrName)) {
      translationRaw ??= normalizeTranslation(joined) || joined;
    } else if (isYearAttributeLabel(taxonomy, attrName)) {
      yearRaw ??= joined;
    } else {
      for (const v of values) {
        if (normalizeCoverType(v)) coverRaw ??= v;
        if (!ageRaw) ageRaw = normalizeAgeRating(v);
        if (!formatRaw) formatRaw = normalizeBookFormat(v);
      }
    }
  }

  const description = extractPlainDescription(p);
  const imageUrls = extractImageUrls(p);
  const coverImageUrl = imageUrls[0] ?? null;
  const additionalImageUrls = imageUrls.slice(1);
  const weightKg =
    parseWeightToKg(
      typeof p.weight === 'string' || typeof p.weight === 'number'
        ? String(p.weight)
        : null
    ) ?? parseWeightToKg(weightRaw);
  const salePrice = extractSalePrice(p);
  const { title, authorFromTitle } = splitAuthorFromTitle(name, author);
  const formattedAuthors =
    formatAuthorsList(authorParts) ||
    (authorFromTitle ? formatAuthorFirstLast(authorFromTitle) : null);
  const coverType = normalizeCoverType(coverRaw);
  const ageRating =
    ageRaw || normalizeAgeRating(description) || normalizeAgeRating(name);
  const format =
    normalizeBookFormat(formatRaw) || normalizeBookFormat(description) || null;
  const illustrator =
    normalizeIllustrator(illustratorRaw) ||
    extractIllustratorFromText(description);
  const language = resolveLanguage(languageRaw, description, name);
  const pageCount =
    normalizePageCount(pagesRaw) || normalizePageCount(description);
  const placeOfPublication =
    normalizePlace(placeRaw) || normalizePlace(description) || placeRaw;
  const translation =
    normalizeTranslation(translationRaw) ||
    normalizeTranslation(description) ||
    translationRaw;
  const year = normalizeYear(yearRaw) || normalizeYear(description);
  return {
    title: title || name,
    author: formattedAuthors,
    isbn,
    publisher,
    description,
    coverImageUrl,
    additionalImageUrls,
    weightKg,
    salePrice,
    coverType,
    ageRating,
    format,
    illustrator,
    language,
    pageCount,
    placeOfPublication,
    translation,
    year,
    url: `${pageUrl.origin}${pageUrl.pathname}`.replace(/\/?$/, '/'),
    source: 'manual',
    snippet: null,
  };
}

function normalizeCoverType(
  raw: string | null | undefined
): 'soft' | 'hard' | null {
  const t = String(raw ?? '')
    .trim()
    .toLowerCase();
  if (!t) return null;
  if (/мягк|мякк|soft|paperback|miękk|miekk|broszur/.test(t)) return 'soft';
  if (/твёрд|тверд|цвёрд|цверд|hard|hardcover|hardback|tward/.test(t))
    return 'hard';
  return null;
}

function formatAuthorFirstLast(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length !== 2) return name.trim();
  const [a, b] = parts;
  const looksSurname = (t: string) =>
    /-/.test(t) ||
    /віч$|вич$|ўна$|евна$|овна$|скі$|ская$|cki$|ska$|ski$|енка$|энка$|оў$|ёў$|ов$|ова$|ева$|ёва$|ина$|іна$|ына$|ук$|юк$|як$|ец$|шк$|ік$|ык$/i.test(
      t
    );
  const looksGiven = (t: string) => {
    if (/-/.test(t) || looksSurname(t)) return false;
    if (/[ая]$|ія$|ия$/i.test(t)) return true;
    return t.length <= 8;
  };
  // Surname GivenName → GivenName Surname
  if (looksSurname(a) && !looksSurname(b)) return `${b} ${a}`;
  if (looksGiven(b) && !looksGiven(a)) return `${b} ${a}`;
  return `${a} ${b}`;
}

function formatAuthorsList(authors: string[]): string | null {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const raw of authors) {
    for (const piece of raw
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean)) {
      const one = formatAuthorFirstLast(piece);
      const key = one.toLowerCase();
      if (!one || seen.has(key)) continue;
      seen.add(key);
      out.push(one);
    }
  }
  return out.length ? out.join(', ') : null;
}

function extractSalePrice(product: Record<string, unknown>): number | null {
  const prices = product.prices;
  if (prices && typeof prices === 'object') {
    const p = prices as Record<string, unknown>;
    let minor = 2;
    if (
      typeof p.currency_minor_unit === 'number' &&
      p.currency_minor_unit >= 0
    ) {
      minor = p.currency_minor_unit;
    }
    for (const key of ['sale_price', 'price', 'regular_price'] as const) {
      const raw = p[key];
      const n =
        typeof raw === 'number'
          ? raw
          : typeof raw === 'string'
            ? Number(raw.replace(',', '.'))
            : NaN;
      if (Number.isFinite(n) && n > 0) {
        return Math.round((n / Math.pow(10, minor)) * 100) / 100;
      }
    }
  }

  for (const key of ['price', 'regular_price', 'sale_price'] as const) {
    const raw = product[key];
    const n =
      typeof raw === 'number'
        ? raw
        : typeof raw === 'string'
          ? Number(String(raw).replace(',', '.'))
          : NaN;
    if (Number.isFinite(n) && n > 0 && n < 100000) {
      return Math.round(n * 100) / 100;
    }
  }
  return null;
}

function extractImageUrls(product: Record<string, unknown>): string[] {
  const images = Array.isArray(product.images) ? product.images : [];
  const urls: string[] = [];
  const seen = new Set<string>();
  for (const image of images) {
    if (!image || typeof image !== 'object') continue;
    const src = (image as { src?: unknown }).src;
    if (typeof src !== 'string') continue;
    const trimmed = src.trim();
    if (!/^https?:\/\//i.test(trimmed)) continue;
    const normalized = trimmed.replace(
      /-\d+x\d+(?=\.(?:jpe?g|png|webp|gif)$)/i,
      ''
    );
    const key = normalized.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    urls.push(normalized);
  }
  return urls;
}

function parseWeightToKg(raw: string | null | undefined): number | null {
  if (!raw) return null;
  const t = raw
    .trim()
    .toLowerCase()
    .replace(',', '.')
    .replace(/\u00a0/g, ' ');
  const m = t.match(/(\d+(?:\.\d+)?)/);
  if (!m) return null;
  let value = Number(m[1]);
  if (!Number.isFinite(value) || value <= 0) return null;

  const grams =
    /\b(g|gr|gram|grams|г|гр|грам)\b/.test(t) ||
    (t.includes('г') && !t.includes('кг') && !t.includes('kg'));
  const kilograms = /\b(kg|кг)\b/.test(t);
  if (grams && !kilograms) value /= 1000;
  else if (!kilograms && !grams && value > 20) value /= 1000;

  if (value <= 0 || value > 50) return null;
  return Math.round(value * 1000) / 1000;
}

function extractPlainDescription(
  product: Record<string, unknown>
): string | null {
  const shortHtml =
    typeof product.short_description === 'string'
      ? product.short_description
      : '';
  const fullHtml =
    typeof product.description === 'string'
      ? product.description
      : product.description &&
          typeof product.description === 'object' &&
          typeof (product.description as { rendered?: string }).rendered ===
            'string'
        ? (product.description as { rendered: string }).rendered
        : '';

  const candidates = [shortHtml, fullHtml]
    .map((html) => htmlToPlainText(html))
    .filter((t) => t.length >= 20)
    .sort((a, b) => b.length - a.length);

  const best = candidates[0];
  if (!best) return null;
  return best.length <= 4000 ? best : `${best.slice(0, 4000).trimEnd()}…`;
}

function htmlToPlainText(html: string): string {
  if (!html) return '';
  const withBreaks = html
    .replace(/<\s*br\s*\/?>/gi, '\n')
    .replace(/<\s*\/\s*p\s*>/gi, '\n\n')
    .replace(/<\s*\/\s*div\s*>/gi, '\n')
    .replace(/<[^>]+>/g, ' ');
  return decodeHtml(withBreaks)
    .replace(/[ \t]+\n/g, '\n')
    .replace(/\n{3,}/g, '\n\n')
    .replace(/[ \t]{2,}/g, ' ')
    .trim();
}

function decodeHtml(value: string): string {
  return value
    .replace(/&amp;/g, '&')
    .replace(/&quot;/g, '"')
    .replace(/&#039;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&#(\d+);/g, (_, n) => String.fromCharCode(Number(n)))
    .replace(/&#x([0-9a-f]+);/gi, (_, h) =>
      String.fromCharCode(parseInt(h, 16))
    );
}

function splitAuthorFromTitle(
  title: string,
  knownAuthor: string | null
): { title: string; authorFromTitle: string | null } {
  const t = title.trim();
  const comma = t.indexOf(',');
  if (comma > 0 && comma < t.length - 3) {
    const prefix = t.slice(0, comma).trim();
    const rest = t.slice(comma + 1).trim();
    if (
      rest.length >= 3 &&
      prefix.split(/\s+/).length >= 2 &&
      prefix.length <= 80
    ) {
      return { title: rest, authorFromTitle: knownAuthor || prefix };
    }
  }
  return { title: t, authorFromTitle: knownAuthor };
}
