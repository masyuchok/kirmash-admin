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
  extractPlaceFromText,
  extractPublisherFromText,
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
 *
 * Kamunikat.shop quirk: WC Store `?slug=` often returns [] for broken/%-encoded
 * slugs, while WP REST finds the product. Full attrs/price/weight live on
 * WC Store `/products/{id}` — we resolve id via WP then fetch Store by id.
 */
export async function lookupProductFromShopPageInBrowser(
  productUrl: string
): Promise<BookLookupCandidate | null> {
  let parsed: URL;
  try {
    parsed = new URL(productUrl.trim());
  } catch {
    return null;
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return null;
  }

  const slugMatch = parsed.pathname.match(
    /\/(?:pradukt|produkt|product)\/([^/]+)\/?/i
  );
  if (!slugMatch?.[1]) {
    return null;
  }

  const host = parsed.host;
  const slugVariants = buildSlugVariants(slugMatch[1], productUrl);
  let best: BookLookupCandidate | null = null;
  let productId: number | null = null;

  const consider = (payload: unknown) => {
    const product = Array.isArray(payload) ? payload[0] : payload;
    const id = readProductId(product);
    if (id != null) productId = id;
    const mapped = mapShopProduct(product, parsed);
    best = pickRicherCandidate(best, mapped);
  };

  for (const slug of slugVariants) {
    for (const path of [
      `/wp-json/wc/store/v1/products?slug=${encodeURIComponent(slug)}`,
      `/wp-json/wc/store/products?slug=${encodeURIComponent(slug)}`,
    ]) {
      try {
        consider(await jsonpFetch(`https://${host}${path}`));
        if (candidateIsRich(best)) return best;
      } catch {
        // try next
      }
    }
  }

  for (const slug of slugVariants) {
    for (const path of [
      `/wp-json/wp/v2/product?slug=${encodeURIComponent(slug)}&_embed=1`,
      `/wp-json/wp/v2/product?slug=${encodeURIComponent(slug)}`,
    ]) {
      try {
        consider(await jsonpFetch(`https://${host}${path}`));
        if (productId != null) break;
      } catch {
        // try next
      }
    }
    if (productId != null) break;
  }

  if (productId != null) {
    for (const path of [
      `/wp-json/wc/store/v1/products/${productId}`,
      `/wp-json/wc/store/products/${productId}`,
    ]) {
      try {
        consider(await jsonpFetch(`https://${host}${path}`));
        if (candidateIsRich(best)) break;
      } catch {
        // try next
      }
    }
  }

  return best;
}

function buildSlugVariants(pathSegment: string, rawUrl: string): string[] {
  const out: string[] = [];
  const add = (s: string | null | undefined) => {
    const t = (s ?? '').trim().replace(/^\/+|\/+$/g, '');
    if (!t || out.includes(t)) return;
    out.push(t);
  };

  add(pathSegment);
  try {
    add(decodeURIComponent(pathSegment.replace(/\+/g, ' ')));
  } catch {
    // ignore malformed escape
  }

  // Prefer the still-encoded segment from the original URL (Kamunikat stores
  // literal "%d0%bb" inside the slug).
  const rawMatch = rawUrl.match(/\/(?:pradukt|produkt|product)\/([^/?#]+)/i);
  if (rawMatch?.[1]) {
    add(rawMatch[1]);
    try {
      add(decodeURIComponent(rawMatch[1].replace(/\+/g, ' ')));
    } catch {
      // ignore
    }
  }

  return out;
}

function jsonpFetch(endpointWithoutJsonp: string): Promise<unknown> {
  return new Promise((resolve, reject) => {
    const callbackName = `__kirmaShopJsonp_${Date.now()}_${Math.floor(
      Math.random() * 1e6
    )}`;
    const sep = endpointWithoutJsonp.includes('?') ? '&' : '?';
    const src = `${endpointWithoutJsonp}${sep}_jsonp=${callbackName}`;
    let script: HTMLScriptElement | null = document.createElement('script');
    let timer: ReturnType<typeof setTimeout> | null = null;
    let settled = false;

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

    const done = (value: unknown, err?: Error) => {
      if (settled) return;
      settled = true;
      cleanup();
      if (err) reject(err);
      else resolve(value);
    };

    (window as unknown as Record<string, unknown>)[callbackName] = (
      payload: unknown
    ) => done(payload);

    script.async = true;
    script.src = src;
    script.onerror = () => done(null, new Error('JSONP load failed'));
    timer = setTimeout(() => done(null, new Error('JSONP timeout')), 12000);
    document.head.appendChild(script);
  });
}

function readProductId(product: unknown): number | null {
  if (!product || typeof product !== 'object') return null;
  const id = (product as { id?: unknown }).id;
  if (typeof id === 'number' && Number.isFinite(id) && id > 0) return id;
  if (typeof id === 'string' && /^\d+$/.test(id.trim())) return Number(id);
  return null;
}

function candidateIsRich(cand: BookLookupCandidate | null): boolean {
  if (!cand?.title?.trim()) return false;
  return Boolean(
    cand.author?.trim() &&
      (cand.coverImageUrl?.trim() ||
        (cand.salePrice != null && cand.salePrice > 0) ||
        cand.isbn?.trim() ||
        cand.description?.trim())
  );
}

function pickRicherCandidate(
  a: BookLookupCandidate | null,
  b: BookLookupCandidate | null
): BookLookupCandidate | null {
  if (!a) return b;
  if (!b) return a;
  const score = (c: BookLookupCandidate) =>
    (c.title?.trim() ? 1 : 0) +
    (c.author?.trim() ? 3 : 0) +
    (c.description?.trim() ? 2 : 0) +
    (c.coverImageUrl?.trim() ? 2 : 0) +
    (c.isbn?.trim() ? 2 : 0) +
    (c.publisher?.trim() ? 1 : 0) +
    (c.salePrice != null && c.salePrice > 0 ? 2 : 0) +
    (c.weightKg != null && c.weightKg > 0 ? 1 : 0) +
    (c.coverType ? 1 : 0) +
    (c.year != null ? 1 : 0) +
    (c.pageCount != null ? 1 : 0) +
    (c.placeOfPublication?.trim() ? 1 : 0);
  return score(b) > score(a) ? b : a;
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
      /cover|binding|opraw|voklad|oklad/i.test(taxonomy) ||
      /воклад|облож|opraw|binding|cover|пераплёт|перепл|okład/i.test(attrName)
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
    normalizePlace(placeRaw) ||
    extractPlaceFromText(description) ||
    extractPlaceFromText(name) ||
    placeRaw;
  const publisherResolved =
    publisher ||
    extractPublisherFromText(description) ||
    extractPublisherFromText(name);
  const translation =
    normalizeTranslation(translationRaw) ||
    normalizeTranslation(description) ||
    translationRaw;
  const year = normalizeYear(yearRaw) || normalizeYear(description);
  return {
    title: title || name,
    author: formattedAuthors,
    isbn,
    publisher: publisherResolved,
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
    /віч$|вич$|ўна$|евна$|овна$|скі$|ская$|цкая$|cki$|cka$|ska$|ski$|енка$|энка$|оў$|ёў$|ов$|ова$|ева$|ёва$|ава$|ина$|іна$|ына$|ук$|юк$|як$|ец$|шк$|ік$|ык$/i.test(
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
  const urls: string[] = [];
  const seen = new Set<string>();

  const tryAdd = (src: unknown) => {
    if (typeof src !== 'string') return;
    const trimmed = src.trim();
    if (!/^https?:\/\//i.test(trimmed)) return;
    const normalized = trimmed.replace(
      /-\d+x\d+(?=\.(?:jpe?g|png|webp|gif)$)/i,
      ''
    );
    const key = normalized.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    urls.push(normalized);
  };

  const images = Array.isArray(product.images) ? product.images : [];
  for (const image of images) {
    if (!image || typeof image !== 'object') continue;
    tryAdd((image as { src?: unknown }).src);
  }

  // WP REST &_embed=1
  const embedded = product._embedded;
  if (embedded && typeof embedded === 'object') {
    const emb = embedded as Record<string, unknown>;
    for (const key of ['wp:featuredmedia', 'wp:featured_media'] as const) {
      const mediaArr = emb[key];
      if (!Array.isArray(mediaArr)) continue;
      for (const media of mediaArr) {
        if (!media || typeof media !== 'object') continue;
        const m = media as Record<string, unknown>;
        tryAdd(m.source_url);
        const details = m.media_details;
        if (details && typeof details === 'object') {
          const sizes = (details as { sizes?: unknown }).sizes;
          if (sizes && typeof sizes === 'object') {
            const full = (sizes as { full?: unknown }).full;
            if (full && typeof full === 'object') {
              tryAdd((full as { source_url?: unknown }).source_url);
            }
          }
        }
      }
    }
  }

  const yoast = product.yoast_head_json;
  if (yoast && typeof yoast === 'object') {
    const ogImages = (yoast as { og_image?: unknown }).og_image;
    if (Array.isArray(ogImages)) {
      for (const og of ogImages) {
        if (og && typeof og === 'object') {
          tryAdd((og as { url?: unknown }).url);
        }
      }
    }
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
        : product.content &&
            typeof product.content === 'object' &&
            typeof (product.content as { rendered?: string }).rendered ===
              'string'
          ? (product.content as { rendered: string }).rendered
          : typeof product.content === 'string'
            ? product.content
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
  let t = title.trim();
  const labeled = t.match(
    /^(.+?)\s*[.…]?\s*(?:Автор(?:ы)?|Аўтар(?:ы)?|Author(?:s)?)\s*[:：]\s*(.+)$/i
  );
  if (labeled) {
    const titleOnly = unwrapTitleQuotes(labeled[1].trim());
    const authorOnly = labeled[2].trim().replace(/[.,;]+$/g, '');
    if (titleOnly.length >= 1 && authorOnly.length >= 2) {
      return {
        title: titleOnly,
        authorFromTitle: knownAuthor || authorOnly,
      };
    }
  }
  t = unwrapTitleQuotes(t);
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

function unwrapTitleQuotes(title: string): string {
  const t = title.trim();
  if (t.length < 2) return t;
  const a = t[0];
  const b = t[t.length - 1];
  if (
    (a === '«' && b === '»') ||
    (a === '"' && b === '"') ||
    (a === "'" && b === "'") ||
    (a === '“' && b === '”') ||
    (a === '„' && b === '“') ||
    (a === '„' && b === '”')
  ) {
    return t.slice(1, -1).trim();
  }
  return t;
}
