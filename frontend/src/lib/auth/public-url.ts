import type { NextRequest } from 'next/server';
import { NextResponse } from 'next/server';

/**
 * Public origin for redirects behind Docker/reverse proxy.
 * Prefer forwarded headers / APP_URL; never trust container-local origins alone.
 */
export function getPublicOrigin(request: NextRequest): string {
  const forwardedHost = request.headers
    .get('x-forwarded-host')
    ?.split(',')[0]
    ?.trim();
  const host =
    forwardedHost || request.headers.get('host')?.split(',')[0]?.trim();

  const forwardedProto = request.headers
    .get('x-forwarded-proto')
    ?.split(',')[0]
    ?.trim();
  const proto =
    forwardedProto ||
    (request.nextUrl.protocol === 'https:' ? 'https' : 'http');

  if (host && !isInternalDockerHost(host)) {
    return `${proto}://${host}`;
  }

  const referer = request.headers.get('referer');
  if (referer) {
    try {
      const origin = new URL(referer).origin;
      if (!isInternalDockerHost(new URL(origin).host)) {
        return origin;
      }
    } catch {
      // ignore invalid referer
    }
  }

  const appUrl = process.env.APP_URL?.trim() || process.env.CLIENT_URL?.trim();
  if (appUrl) {
    try {
      const origin = new URL(appUrl).origin;
      if (!isInternalDockerHost(new URL(origin).host)) {
        return origin;
      }
    } catch {
      // ignore invalid APP_URL
    }
  }

  return request.nextUrl.origin;
}

export function getPublicUrl(request: NextRequest, path: string): URL {
  const normalized = path.startsWith('/') ? path : `/${path}`;
  return new URL(normalized, getPublicOrigin(request));
}

/**
 * Safe middleware/route redirect. Uses a public absolute URL when possible.
 * Avoids rewriting Location to a relative path — that can 500 in Next.js 15
 * middleware behind Cloudflare/Docker.
 */
export function redirectPublic(
  request: NextRequest,
  path: string
): NextResponse {
  const normalized = path.startsWith('/') ? path : `/${path}`;

  try {
    const origin = getPublicOrigin(request);
    if (origin && !isInternalDockerHost(new URL(origin).host)) {
      return NextResponse.redirect(new URL(normalized, origin));
    }
  } catch {
    // fall through
  }

  const appUrl = process.env.APP_URL?.trim() || process.env.CLIENT_URL?.trim();
  if (appUrl) {
    try {
      return NextResponse.redirect(new URL(normalized, appUrl));
    } catch {
      // fall through
    }
  }

  try {
    return NextResponse.redirect(new URL(normalized, request.url));
  } catch {
    return new NextResponse(null, {
      status: 307,
      headers: { Location: normalized },
    });
  }
}

function isInternalDockerHost(host: string): boolean {
  const hostname = host.split(':')[0]?.toLowerCase() ?? '';
  if (
    hostname === 'localhost' ||
    hostname === '127.0.0.1' ||
    hostname === '0.0.0.0' ||
    hostname === 'frontend' ||
    hostname === 'backend' ||
    hostname.endsWith('.internal') ||
    hostname.endsWith('.local')
  ) {
    return true;
  }

  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(hostname)) {
    const parts = hostname.split('.').map(Number);
    const [a, b] = parts;
    if (a === 10) return true;
    if (a === 127) return true;
    if (a === 192 && b === 168) return true;
    if (a === 172 && b >= 16 && b <= 31) return true;
    if (a === 169 && b === 254) return true;
  }

  return false;
}
