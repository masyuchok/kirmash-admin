import { NextRequest, NextResponse } from 'next/server';
import { decodeJwt } from 'jose';
import { getApiBaseUrl } from '@/lib/api/common';

const COOKIE = process.env.AUTH_COOKIE_NAME || 'jwt_token';

export function middleware(req: NextRequest) {
  const { pathname, search } = req.nextUrl;
  const backendUrl = getApiBaseUrl();

  // public paths without auth
  const publicPaths = [
    '/_next',
    '/favicon.ico',
    '/robots.txt',
    '/sitemap.xml',
    '/login',
  ];
  if (publicPaths.some((p) => pathname.startsWith(p)))
    return NextResponse.next();

  const token = req.cookies.get(COOKIE)?.value;

  // if there is no token — start login via backend
  if (!token) {
    const url = new URL(`${backendUrl}/auth/login`);
    url.searchParams.set('shop', process.env.NEXT_PUBLIC_SHOP_DOMAIN!);
    return NextResponse.redirect(url);
  }

  try {
    const { exp } = decodeJwt(token);
    const now = Math.floor(Date.now() / 1000);
    if (exp && exp < now) {
      const url = new URL(`${backendUrl}/auth/login`);
      url.searchParams.set('shop', process.env.NEXT_PUBLIC_SHOP_DOMAIN!);
      return NextResponse.redirect(url);
    }
  } catch {
    const url = new URL(`${backendUrl}/auth/login`);
    url.searchParams.set('shop', process.env.NEXT_PUBLIC_SHOP_DOMAIN!);
    return NextResponse.redirect(url);
  }

  return NextResponse.next();
}

export const config = { matcher: ['/:path*'] };
