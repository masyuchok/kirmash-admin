import {
  apiCredentials,
  getApiBaseUrl,
  getAuthLoginUrl,
  readErrorMessage,
} from '@/lib/api/common';

/** Navigate to Kirma logout route (clears Shopify session cookie). */
export function getLogoutUrl(): string {
  return '/auth/logout';
}

/** Navigate to Bukinistka logout route (clears Odoo session cookie). */
export function getBukinistkaLogoutUrl(): string {
  return '/auth/bukinistka/logout';
}

/**
 * Clear JWT via backend (same cookie attributes as login), then hard-navigate
 * to the Next logout route which clears again and redirects to /login.
 */
export async function logoutKirma(): Promise<void> {
  try {
    await fetch(`${getApiBaseUrl()}/auth/logout`, {
      method: 'POST',
      credentials: apiCredentials,
    });
  } catch {
    // Still proceed to frontend logout clear/redirect.
  }
  window.location.href = getLogoutUrl();
}

/** Clear Bukinistka cookie via backend, then Next logout redirect. */
export async function logoutBukinistka(): Promise<void> {
  try {
    await fetch(`${getApiBaseUrl()}/auth/odoo/logout`, {
      method: 'POST',
      credentials: apiCredentials,
    });
  } catch {
    // Still proceed to frontend logout clear/redirect.
  }
  window.location.href = getBukinistkaLogoutUrl();
}

/** Full URL to start Shopify OAuth from the login page. */
export function getShopifyLoginUrl(): string {
  const url = getAuthLoginUrl(
    typeof window !== 'undefined' ? window.location.href : 'http://localhost'
  );
  const shop = process.env.NEXT_PUBLIC_SHOP_DOMAIN?.trim();
  if (shop) {
    url.searchParams.set('shop', shop);
  }
  return url.toString();
}

export type BukinistkaLoginResult = {
  success: boolean;
  redirectUrl: string;
  user?: {
    login: string;
    name: string;
  };
};

export async function loginBukinistka(
  login: string,
  password: string
): Promise<BukinistkaLoginResult> {
  const res = await fetch(`${getApiBaseUrl()}/auth/odoo/login`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ login, password }),
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося ўвайсці праз Odoo.')
    );
  }

  return res.json();
}

export type BukinistkaMe = {
  login: string;
  name: string;
  uid: string;
  database: string;
};

export async function fetchBukinistkaMe(): Promise<BukinistkaMe> {
  const res = await fetch(`${getApiBaseUrl()}/auth/odoo/me`, {
    credentials: apiCredentials,
  });

  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося загрузіць профіль.')
    );
  }

  return res.json();
}
