import { NextRequest, NextResponse } from 'next/server';
import {
  getBukinistkaAuthCookieName,
  getKirmaAuthCookieName,
} from '@/lib/auth/cookie';
import { redirectPublic } from '@/lib/auth/public-url';
import { getTokenOrganization, isAuthTokenValid } from '@/lib/auth/session';

function redirectToLogin(req: NextRequest): NextResponse {
  return redirectPublic(req, '/login');
}

function redirectToBukinistka(req: NextRequest): NextResponse {
  return redirectPublic(req, '/bukinistka');
}

export function middleware(req: NextRequest) {
  const { pathname } = req.nextUrl;

  const publicPaths = [
    '/_next',
    '/favicon.ico',
    '/robots.txt',
    '/sitemap.xml',
    '/login',
    '/auth/logout',
    '/auth/bukinistka/logout',
    '/api',
  ];
  if (publicPaths.some((p) => pathname.startsWith(p))) {
    return NextResponse.next();
  }

  // Static files from /public (logos, icons) must stay reachable without auth.
  if (
    /\.(?:png|jpe?g|gif|webp|svg|ico|woff2?|ttf|eot|css|js|map|txt)$/i.test(
      pathname
    )
  ) {
    return NextResponse.next();
  }

  const kirmaCookie = getKirmaAuthCookieName();
  const bukinistkaCookie = getBukinistkaAuthCookieName();
  const kirmaToken = req.cookies.get(kirmaCookie)?.value;
  const bukinistkaToken = req.cookies.get(bukinistkaCookie)?.value;
  const kirmaValid = isAuthTokenValid(kirmaToken);
  const bukinistkaValid = isAuthTokenValid(bukinistkaToken);
  const isBukinistkaRoute =
    pathname === '/bukinistka' || pathname.startsWith('/bukinistka/');

  if (isBukinistkaRoute) {
    if (!bukinistkaValid) {
      return redirectToLogin(req);
    }

    const org = getTokenOrganization(bukinistkaToken);
    if (org && org !== 'bukinistka') {
      return redirectToLogin(req);
    }

    return NextResponse.next();
  }

  // Kirma panel routes require a Kirma (Shopify) session.
  // Bukinistka-only users go to login so they can choose Kirma / sign in —
  // not to an absolute internal Docker URL.
  if (kirmaValid) {
    const org = getTokenOrganization(kirmaToken);
    if (org === 'bukinistka') {
      return redirectToBukinistka(req);
    }

    return NextResponse.next();
  }

  return redirectToLogin(req);
}

export const config = { matcher: ['/:path*'] };
