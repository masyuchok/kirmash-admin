/**
 * Normalized API origin (no trailing slash).
 * In the browser always uses the current site + `/api` so auth redirects never
 * stick to a stale localhost baked in at build time.
 */
export function getApiBaseUrl(): string {
  if (typeof window !== 'undefined') {
    return `${window.location.origin}/api`;
  }

  const configured = process.env.NEXT_PUBLIC_API_URL?.trim();
  if (!configured) {
    return '/api';
  }

  if (configured.startsWith('http://') || configured.startsWith('https://')) {
    return configured.replace(/\/$/, '');
  }

  return `https://${configured.replace(/\/$/, '')}`;
}

/** For middleware: login URL on the same host the user opened. */
export function getAuthLoginUrl(requestUrl: string): URL {
  return new URL('/api/auth/login', requestUrl);
}

/** Default for cookie-authenticated API calls. */
export const apiCredentials: RequestCredentials = 'include';

/**
 * Best-effort message from a failed JSON API response (`error` or `message`),
 * otherwise response text, otherwise `fallback`.
 */
export function readErrorMessage(res: Response, fallback: string): Promise<string> {
  return res
    .json()
    .then((data) => (typeof data?.error === 'string' ? data.error : data?.message) || fallback)
    .catch(() => res.text().catch(() => fallback));
}
