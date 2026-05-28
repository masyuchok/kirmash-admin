import { NextRequest, NextResponse } from 'next/server';
import { decodeJwt } from 'jose';
import { getAuthLoginUrl } from '@/lib/api/common';

const COOKIE = process.env.AUTH_COOKIE_NAME || 'jwt_token';

function redirectToLogin(req: NextRequest): NextResponse {
  const url = getAuthLoginUrl(req.url);
  url.searchParams.set('shop', process.env.NEXT_PUBLIC_SHOP_DOMAIN!);
  return NextResponse.redirect(url);
}

export function middleware(req: NextRequest) {
  const { pathname } = req.nextUrl;

  const publicPaths = [
    '/_next',
    '/favicon.ico',
    '/robots.txt',
    '/sitemap.xml',
    '/login',
    '/api',
  ];
  if (publicPaths.some((p) => pathname.startsWith(p))) {
    return NextResponse.next();
  }

  const token = req.cookies.get(COOKIE)?.value;

  if (!token) {
    return redirectToLogin(req);
  }

  try {
    const { exp } = decodeJwt(token);
    const now = Math.floor(Date.now() / 1000);
    if (exp && exp < now) {
      return redirectToLogin(req);
    }
  } catch {
    return redirectToLogin(req);
  }

  return NextResponse.next();
}

export const config = { matcher: ['/:path*'] };
