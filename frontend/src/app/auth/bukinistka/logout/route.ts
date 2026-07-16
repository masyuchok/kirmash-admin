import { appendBukinistkaAuthCookieClear } from '@/lib/auth/cookie';
import { redirectPublic } from '@/lib/auth/public-url';
import { NextRequest, NextResponse } from 'next/server';

function logoutResponse(request: NextRequest): NextResponse {
  const loginPath = '/login?loggedOut=1&org=bukinistka';
  const response = redirectPublic(request, loginPath);
  appendBukinistkaAuthCookieClear(
    response,
    request.headers.get('x-forwarded-host') || request.headers.get('host')
  );
  return response;
}

export function GET(request: NextRequest) {
  return logoutResponse(request);
}

export async function POST(request: NextRequest) {
  return logoutResponse(request);
}
