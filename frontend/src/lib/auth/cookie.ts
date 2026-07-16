import type { NextResponse } from 'next/server';

export function getKirmaAuthCookieName(): string {
  return process.env.AUTH_COOKIE_NAME?.trim() || 'jwt_token';
}

export function getBukinistkaAuthCookieName(): string {
  return process.env.BUKINISTKA_COOKIE_NAME?.trim() || 'bukinistka_token';
}

function resolveCookieDomain(host: string | null): string | undefined {
  const configured = process.env.AUTH_COOKIE_DOMAIN?.trim();
  if (configured) {
    return configured;
  }

  if (!host) {
    return undefined;
  }

  const normalized = host.split(':')[0]?.toLowerCase() ?? '';
  if (normalized === 'kirma.sh' || normalized.endsWith('.kirma.sh')) {
    return '.kirma.sh';
  }

  return undefined;
}

function uniqueStrings(
  values: Array<string | undefined>
): Array<string | undefined> {
  const seen = new Set<string>();
  const result: Array<string | undefined> = [];
  for (const value of values) {
    const key = value ?? '';
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(value);
  }
  return result;
}

/**
 * Build expired Set-Cookie header. Uses headers.append so multiple Domain /
 * SameSite / Secure variants are not overwritten by response.cookies.set().
 */
function serializeClearedCookie(
  name: string,
  options: {
    domain?: string;
    secure: boolean;
    sameSite: 'None' | 'Lax';
  }
): string {
  const parts = [
    `${name}=`,
    'Path=/',
    'HttpOnly',
    'Max-Age=0',
    'Expires=Thu, 01 Jan 1970 00:00:00 GMT',
    `SameSite=${options.sameSite}`,
  ];
  if (options.domain) {
    parts.push(`Domain=${options.domain}`);
  }
  if (options.secure) {
    parts.push('Secure');
  }
  return parts.join('; ');
}

function clearCookie(
  response: NextResponse,
  host: string | null,
  name: string
): void {
  const configuredDomain = resolveCookieDomain(host);
  const domains = uniqueStrings([
    undefined,
    configuredDomain,
    configuredDomain?.replace(/^\./, ''),
    configuredDomain && !configuredDomain.startsWith('.')
      ? `.${configuredDomain}`
      : undefined,
    '.kirma.sh',
    'kirma.sh',
  ]);

  const variants: Array<{ secure: boolean; sameSite: 'None' | 'Lax' }> = [
    { secure: true, sameSite: 'None' },
    { secure: true, sameSite: 'Lax' },
    { secure: false, sameSite: 'Lax' },
  ];

  for (const domain of domains) {
    for (const variant of variants) {
      // SameSite=None requires Secure; skip invalid combo.
      if (variant.sameSite === 'None' && !variant.secure) continue;
      response.headers.append(
        'Set-Cookie',
        serializeClearedCookie(name, {
          domain,
          secure: variant.secure,
          sameSite: variant.sameSite,
        })
      );
    }
  }
}

/** Clear Kirma (Shopify) auth cookie. */
export function appendKirmaAuthCookieClear(
  response: NextResponse,
  host: string | null
): void {
  clearCookie(response, host, getKirmaAuthCookieName());
}

/** Clear Bukinistka (Odoo) auth cookie. */
export function appendBukinistkaAuthCookieClear(
  response: NextResponse,
  host: string | null
): void {
  clearCookie(response, host, getBukinistkaAuthCookieName());
}

/** @deprecated Use appendKirmaAuthCookieClear */
export function appendAuthCookieClear(
  response: NextResponse,
  host: string | null
): void {
  appendKirmaAuthCookieClear(response, host);
}
