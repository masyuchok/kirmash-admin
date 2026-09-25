import {
  apiCredentials,
  getApiBaseUrl,
  readErrorMessage,
} from '@/lib/api/common';

export type BookLookupCandidate = {
  title: string;
  author?: string | null;
  isbn?: string | null;
  publisher?: string | null;
  url: string;
  source: string;
  snippet?: string | null;
};

export type BookLookupStepResult = {
  sessionId: string;
  candidate: BookLookupCandidate | null;
  queryTitle?: string | null;
  queryAuthor?: string | null;
  queryIsbn?: string | null;
  attemptsUsed: number;
  attemptsMax: number;
  done: boolean;
  awaitingOcrConfirm?: boolean;
  canSearchByPhoto?: boolean;
  photoSearchExhausted?: boolean;
  message?: string | null;
};

export type BookCreateFromLookupResult = {
  shopifyProductId: string;
  shopifyVariantId: string;
  title: string;
};

function mapStep(raw: Record<string, unknown>): BookLookupStepResult {
  const candidateRaw = (raw.candidate ?? raw.Candidate) as
    | Record<string, unknown>
    | null
    | undefined;
  const candidate =
    candidateRaw && typeof candidateRaw === 'object'
      ? {
          title: String(candidateRaw.title ?? candidateRaw.Title ?? ''),
          author: (candidateRaw.author ?? candidateRaw.Author ?? null) as
            | string
            | null,
          isbn: (candidateRaw.isbn ?? candidateRaw.Isbn ?? null) as
            | string
            | null,
          publisher: (candidateRaw.publisher ??
            candidateRaw.Publisher ??
            null) as string | null,
          url: String(candidateRaw.url ?? candidateRaw.Url ?? ''),
          source: String(candidateRaw.source ?? candidateRaw.Source ?? 'web'),
          snippet: (candidateRaw.snippet ?? candidateRaw.Snippet ?? null) as
            | string
            | null,
        }
      : null;

  return {
    sessionId: String(raw.sessionId ?? raw.SessionId ?? ''),
    candidate,
    queryTitle: (raw.queryTitle ?? raw.QueryTitle ?? null) as string | null,
    queryAuthor: (raw.queryAuthor ?? raw.QueryAuthor ?? null) as string | null,
    queryIsbn: (raw.queryIsbn ?? raw.QueryIsbn ?? null) as string | null,
    attemptsUsed: Number(raw.attemptsUsed ?? raw.AttemptsUsed ?? 0),
    attemptsMax: Number(raw.attemptsMax ?? raw.AttemptsMax ?? 0),
    done: Boolean(raw.done ?? raw.Done),
    awaitingOcrConfirm: Boolean(
      raw.awaitingOcrConfirm ?? raw.AwaitingOcrConfirm
    ),
    canSearchByPhoto: Boolean(raw.canSearchByPhoto ?? raw.CanSearchByPhoto),
    photoSearchExhausted: Boolean(
      raw.photoSearchExhausted ?? raw.PhotoSearchExhausted
    ),
    message: (raw.message ?? raw.Message ?? null) as string | null,
  };
}

export async function lookupBookFromPhoto(input: {
  cover: File;
  isbnPhoto?: File | null;
  supplierId?: string | number | null;
}): Promise<BookLookupStepResult> {
  const form = new FormData();
  form.append('cover', input.cover);
  if (input.isbnPhoto) {
    form.append('isbnPhoto', input.isbnPhoto);
  }
  if (input.supplierId != null && String(input.supplierId).trim() !== '') {
    form.append('supplierId', String(input.supplierId));
  }

  const res = await fetch(`${getApiBaseUrl()}/books/lookup-from-photo`, {
    method: 'POST',
    credentials: apiCredentials,
    body: form,
  });
  if (!res.ok) {
    if (res.status === 413) {
      throw new Error(
        'Фота занадта вялікае для загрузкі. Паспрабуйце іншае фота або меншы памер.'
      );
    }
    throw new Error(await readErrorMessage(res, 'Не ўдалося шукаць па фота'));
  }
  return mapStep((await res.json()) as Record<string, unknown>);
}

export async function lookupBookFromText(input: {
  title: string;
  isbn?: string;
  supplierId?: string | number | null;
}): Promise<BookLookupStepResult> {
  const res = await fetch(`${getApiBaseUrl()}/books/lookup-from-text`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      title: input.title,
      isbn: input.isbn || null,
      supplierId: input.supplierId ? Number(input.supplierId) : null,
    }),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося шукаць па назве'));
  }
  return mapStep((await res.json()) as Record<string, unknown>);
}

export async function confirmBookOcrAndSearch(input: {
  sessionId: string;
  title: string;
  author?: string;
  isbn?: string;
}): Promise<BookLookupStepResult> {
  const res = await fetch(
    `${getApiBaseUrl()}/books/lookup/${encodeURIComponent(input.sessionId)}/confirm-ocr`,
    {
      method: 'POST',
      credentials: apiCredentials,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        title: input.title,
        author: input.author || null,
        isbn: input.isbn || null,
      }),
    }
  );
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося запусціць пошук'));
  }
  return mapStep((await res.json()) as Record<string, unknown>);
}

export async function lookupBookNext(
  sessionId: string
): Promise<BookLookupStepResult> {
  const res = await fetch(
    `${getApiBaseUrl()}/books/lookup/${encodeURIComponent(sessionId)}/next`,
    {
      method: 'POST',
      credentials: apiCredentials,
    }
  );
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося шукаць далей'));
  }
  return mapStep((await res.json()) as Record<string, unknown>);
}

export async function lookupBookByPhoto(
  sessionId: string
): Promise<BookLookupStepResult> {
  const res = await fetch(
    `${getApiBaseUrl()}/books/lookup/${encodeURIComponent(sessionId)}/search-by-photo`,
    {
      method: 'POST',
      credentials: apiCredentials,
    }
  );
  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося шукаць па фота вокладкі')
    );
  }
  return mapStep((await res.json()) as Record<string, unknown>);
}

export async function lookupBookFromUrl(input: {
  sessionId?: string;
  url: string;
}): Promise<BookLookupCandidate> {
  const res = await fetch(`${getApiBaseUrl()}/books/lookup/from-url`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      sessionId: input.sessionId || null,
      url: input.url,
    }),
  });
  if (!res.ok) {
    throw new Error(
      await readErrorMessage(res, 'Не ўдалося прачытаць спасылку')
    );
  }
  const raw = (await res.json()) as Record<string, unknown>;
  return {
    title: String(raw.title ?? raw.Title ?? ''),
    author: (raw.author ?? raw.Author ?? null) as string | null,
    isbn: (raw.isbn ?? raw.Isbn ?? null) as string | null,
    publisher: (raw.publisher ?? raw.Publisher ?? null) as string | null,
    url: String(raw.url ?? raw.Url ?? ''),
    source: String(raw.source ?? raw.Source ?? 'manual'),
    snippet: (raw.snippet ?? raw.Snippet ?? null) as string | null,
  };
}

export async function createBookFromLookup(input: {
  sessionId: string;
  title: string;
  author?: string;
  isbn?: string;
  publisher?: string;
  descriptionHtml?: string;
  salePrice: number;
  unitCost: number;
  useCoverImage?: boolean;
}): Promise<BookCreateFromLookupResult> {
  const res = await fetch(`${getApiBaseUrl()}/books/create-from-lookup`, {
    method: 'POST',
    credentials: apiCredentials,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      sessionId: input.sessionId,
      title: input.title,
      author: input.author || null,
      isbn: input.isbn || null,
      publisher: input.publisher || null,
      descriptionHtml: input.descriptionHtml || null,
      salePrice: input.salePrice,
      unitCost: input.unitCost,
      useCoverImage: input.useCoverImage ?? true,
    }),
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, 'Не ўдалося стварыць тавар'));
  }
  const raw = (await res.json()) as Record<string, unknown>;
  return {
    shopifyProductId: String(
      raw.shopifyProductId ?? raw.ShopifyProductId ?? ''
    ),
    shopifyVariantId: String(
      raw.shopifyVariantId ?? raw.ShopifyVariantId ?? ''
    ),
    title: String(raw.title ?? raw.Title ?? ''),
  };
}
