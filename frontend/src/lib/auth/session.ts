import { decodeJwt, type JWTPayload } from 'jose';

export function isAuthTokenValid(token: string | undefined): boolean {
  if (!token) {
    return false;
  }

  try {
    const { exp } = decodeJwt(token);
    const now = Math.floor(Date.now() / 1000);
    return !exp || exp >= now;
  } catch {
    return false;
  }
}

export function getTokenOrganization(
  token: string | undefined
): string | undefined {
  if (!token) {
    return undefined;
  }

  try {
    const payload = decodeJwt(token) as JWTPayload & { org?: string };
    return typeof payload.org === 'string' ? payload.org : undefined;
  } catch {
    return undefined;
  }
}
