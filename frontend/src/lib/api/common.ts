/** Normalized API origin (no trailing slash). */
export function getApiBaseUrl(): string {
  const url = process.env.NEXT_PUBLIC_API_URL;
  if (!url?.trim()) {
    throw new Error('NEXT_PUBLIC_API_URL is not set');
  }
  return url.replace(/\/$/, '');
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
