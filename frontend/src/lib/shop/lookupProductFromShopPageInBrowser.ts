import type { BookLookupCandidate } from '@/lib/api/bookLookup';

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
  let isbn: string | null = null;
  let publisher: string | null = null;
  const attrs = Array.isArray(p.attributes) ? p.attributes : [];
  for (const attr of attrs) {
    if (!attr || typeof attr !== 'object') continue;
    const a = attr as Record<string, unknown>;
    const taxonomy = String(a.taxonomy || '');
    const attrName = String(a.name || '');
    const terms = Array.isArray(a.terms) ? a.terms : [];
    const options = Array.isArray(a.options) ? a.options : [];
    const term =
      (terms[0] &&
        typeof terms[0] === 'object' &&
        typeof (terms[0] as { name?: string }).name === 'string' &&
        (terms[0] as { name: string }).name) ||
      (typeof options[0] === 'string' ? options[0] : null);
    if (!term) continue;
    const value = decodeHtml(term).trim();
    if (!value) continue;
    if (
      /autar|author/i.test(taxonomy) ||
      /аўтар|автор|author/i.test(attrName)
    ) {
      author ??= value;
    } else if (/isbn/i.test(taxonomy) || /isbn/i.test(attrName)) {
      isbn ??= value;
    } else if (
      /vydav|publisher/i.test(taxonomy) ||
      /выдавец|издател|publisher/i.test(attrName)
    ) {
      publisher ??= value;
    }
  }

  const { title, authorFromTitle } = splitAuthorFromTitle(name, author);
  return {
    title: title || name,
    author: author || authorFromTitle,
    isbn,
    publisher,
    url: `${pageUrl.origin}${pageUrl.pathname}`.replace(/\/?$/, '/'),
    source: 'manual',
    snippet: null,
  };
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
