/**
 * Bibliographic field parsers for shop pages / price lists (modal autofill).
 */

function collapse(raw: string): string {
  return String(raw).trim().replace(/\s+/g, ' ');
}

export function normalizeBookFormat(
  raw: string | null | undefined
): string | null {
  if (raw == null) return null;
  const t = collapse(raw);
  if (!t) return null;
  const size = t.match(
    /\b(\d{2,3})\s*[x×хXХ]\s*(\d{2,3})\s*(?:mm|мм|cm|см)?\b/i
  );
  if (size) return `${size[1]}×${size[2]}`;
  if (/фармат|формат|format|размер|памер|mm|мм|wymiar/i.test(t)) {
    const letter = t.match(/\b([AB][0-9]|[AB][0-9]\/[0-9])\b/i);
    if (letter) return letter[1].toUpperCase();
  }
  return null;
}

export function normalizeIllustrator(
  raw: string | null | undefined
): string | null {
  if (raw == null) return null;
  let t = collapse(raw);
  const labeled = t.match(
    /(?:ілюстратар(?:ы)?|иллюстратор(?:ы)?|illustrator(?:s)?|ilustrator(?:zy)?|ілюстрацыі|иллюстрации|ilustracje|мастак|художник)\s*:?\s*(.+)/i
  );
  if (labeled) {
    t = labeled[1];
    t = t.split(/\s*[|;]\s*/)[0];
    t = t.split(
      /(?=\b(?:мова|язык|год|rok|фармат|формат|ISBN|аўтар|автор)\b)/i
    )[0];
  }
  t = t
    .replace(
      /^(?:ілюстратар(?:ы)?|иллюстратор(?:ы)?|illustrator(?:s)?|ilustrator(?:zy)?|ілюстрацыі|иллюстрации|ilustracje|мастак|художник)\s*:?\s*/i,
      ''
    )
    .trim()
    .replace(/^[,;.:|]+|[,;.:|]+$/g, '');
  return looksLikePersonName(t) ? t : null;
}

/** Only when text explicitly labels an illustrator — never bare titles/price rows. */
export function extractIllustratorFromText(
  raw: string | null | undefined
): string | null {
  if (raw == null) return null;
  if (
    !/(?:ілюстратар(?:ы)?|иллюстратор(?:ы)?|illustrator(?:s)?|ilustrator(?:zy)?|ілюстрацыі|иллюстрации|ilustracje|мастак|художник)\s*:?/i.test(
      raw
    )
  ) {
    return null;
  }
  return normalizeIllustrator(raw);
}

function looksLikePersonName(t: string): boolean {
  t = collapse(t);
  if (t.length < 2 || t.length > 60) return false;
  if (
    /©|copyright|копирайт|аўтарск|авторск|зборнік|сборник|collection|эсэ|эссе|essay|туры|ISBN|\b\d{2,}\b/i.test(
      t
    )
  ) {
    return false;
  }
  if (/\.\s+\S/.test(t)) return false;
  if (/^\d/.test(t) || /\d{3,}/.test(t)) return false;
  const tokens = t.split(/\s+/).filter(Boolean);
  if (tokens.length < 1 || tokens.length > 5) return false;
  for (const token of tokens) {
    if (
      /^[A-Za-zА-Яа-яЁёІіЎў'\-]+\.?$/.test(token) ||
      /^[A-Za-zА-Яа-яЁёІіЎў]\.$/.test(token)
    ) {
      continue;
    }
    return false;
  }
  return true;
}

export function normalizeLanguage(
  raw: string | null | undefined
): string | null {
  if (raw == null) return null;
  const collapsed = collapse(raw);
  if (!collapsed) return null;
  if (collapsed.length > 60) {
    return (
      extractExplicitLanguage(collapsed) || inferLanguageFromText(collapsed)
    );
  }
  const t = collapsed.toLowerCase();
  const labels: string[] = [];
  const add = (label: string) => {
    if (!labels.some((x) => x.toLowerCase() === label.toLowerCase()))
      labels.push(label);
  };
  if (/беларус|belarus|белорус|\bbe\b/.test(t)) add('беларуская');
  if (/польск|polish|polski|\bpl\b/.test(t)) add('польская');
  if (/руск|русск|russian|rosyjski|\bru\b/.test(t)) add('руская');
  if (/англел|англий|english|angielski|\ben\b/.test(t)) add('англійская');
  if (/украін|украин|ukrain|ukraińsk|\buk\b/.test(t)) add('украінская');
  if (/літоў|литов|lithuan|litewsk|\blt\b/.test(t)) add('літоўская');
  if (labels.length) return labels.join(', ');
  if (
    collapsed.length >= 2 &&
    collapsed.length <= 40 &&
    !/\d/.test(collapsed) &&
    /мова|язык|language|język|jezyk/i.test(collapsed)
  ) {
    return collapsed;
  }
  return null;
}

/** Prefer explicit attr / "мова:" label; else infer from description orthography. */
export function resolveLanguage(
  attributeOrShort: string | null | undefined,
  ...texts: Array<string | null | undefined>
): string | null {
  const attr = String(attributeOrShort ?? '').trim();
  if (attr && attr.length <= 60) {
    const fromAttr = normalizeLanguage(attr);
    if (fromAttr) return fromAttr;
  }
  for (const text of texts) {
    const explicit = extractExplicitLanguage(text);
    if (explicit) return explicit;
  }
  for (const text of texts) {
    const inferred = inferLanguageFromText(text);
    if (inferred) return inferred;
  }
  return null;
}

export function extractExplicitLanguage(
  raw: string | null | undefined
): string | null {
  if (raw == null) return null;
  const m = String(raw).match(
    /(?:мова|язык|language|język|jezyk)\s*[:\-–]?\s*([^\n.;|]{2,40})/i
  );
  if (!m) return null;
  let piece = m[1].trim().replace(/^[,;.:]+|[,;.:]+$/g, '');
  if (piece.length > 60) piece = piece.slice(0, 60);
  const t = piece.toLowerCase();
  if (/беларус|belarus|белорус|\bbe\b/.test(t)) return 'беларуская';
  if (/польск|polish|polski|\bpl\b/.test(t)) return 'польская';
  if (/руск|русск|russian|rosyjski|\bru\b/.test(t)) return 'руская';
  if (/англел|англий|english|angielski|\ben\b/.test(t)) return 'англійская';
  if (/украін|украин|ukrain|ukraińsk|\buk\b/.test(t)) return 'украінская';
  if (/літоў|литов|lithuan|litewsk|\blt\b/.test(t)) return 'літоўская';
  return piece.length >= 2 && piece.length <= 40 ? piece : null;
}

export function inferLanguageFromText(
  raw: string | null | undefined
): string | null {
  if (raw == null) return null;
  const t = collapse(raw);
  if (t.length < 40) return null;

  let cyr = 0;
  let lat = 0;
  for (const c of t) {
    if (/[А-Яа-яЁёІіЎўЇїЄєҐґ]/.test(c)) cyr++;
    else if (/[A-Za-zĄĆĘŁŃÓŚŹŻąćęłńóśźż]/.test(c)) lat++;
  }

  const countChars = (...chars: string[]) => {
    let n = 0;
    for (const ch of t) if (chars.includes(ch)) n++;
    return n;
  };
  const countMatches = (re: RegExp) => (t.match(re) || []).length;

  if (lat > cyr * 2 && lat >= 30) {
    const pl = countMatches(/[ĄĆĘŁŃÓŚŹŻąćęłńóśźż]/g);
    if (pl >= 2 || /\b(się|nie|jest|oraz|który|książka)\b/i.test(t)) {
      return 'польская';
    }
    if (/\b(the|and|with|from|this|that|book|about)\b/i.test(t)) {
      return 'англійская';
    }
    return pl > 0 ? 'польская' : 'англійская';
  }

  if (cyr < 20) return null;

  const be =
    countChars('ў', 'Ў') * 6 +
    countChars('і', 'І') +
    countMatches(/\b(гэта|ёсць|таксама|яшчэ|каб|толькі|ўсё|ўжо)\b/gi) * 3;
  const ru =
    countChars('ы', 'Ы') +
    countChars('и', 'И') +
    countChars('щ', 'Щ') * 4 +
    countChars('ъ', 'Ъ') * 4 +
    countMatches(/\b(это|есть|также|только|уже|чтобы)\b/gi) * 3;
  const uk =
    countChars('ї', 'Ї') * 5 +
    countChars('є', 'Є') * 5 +
    countChars('ґ', 'Ґ') * 5 +
    countMatches(/\b(це|є|також|ще|щоб|тільки)\b/gi) * 3;

  if (countChars('ў', 'Ў') >= 1 && be >= ru && be >= uk) return 'беларуская';
  if (uk >= be && uk >= ru && uk >= 3) return 'украінская';
  if (be > ru && be >= 3) return 'беларуская';
  if (ru >= 3) return 'руская';
  if (be > 0) return 'беларуская';
  return ru > 0 ? 'руская' : null;
}

export function normalizePageCount(
  raw: string | null | undefined
): number | null {
  if (raw == null) return null;
  const t = collapse(raw);
  const m = t.match(
    /\b(\d{2,4})\s*(?:с\.|стр\.?|старонак|страниц|stron(?:y|a)?|pages?|szt\.?)\b/i
  );
  if (m) {
    const n = Number(m[1]);
    if (Number.isFinite(n) && n >= 8 && n <= 5000) return n;
  }
  if (/^\d{2,4}$/.test(t)) {
    const n = Number(t);
    if (Number.isFinite(n) && n >= 8 && n <= 5000) return n;
  }
  return null;
}

export function normalizePlace(raw: string | null | undefined): string | null {
  if (raw == null) return null;
  let t = collapse(raw);
  t = t.replace(
    /^(?:месца\s+выдання|место\s+издания|miejsce\s+wydania|place\s+of\s+publication|город|горад)\s*:?\s*/i,
    ''
  );
  t = t.trim().replace(/^[,;.:]+|[,;.:]+$/g, '');
  if (t.length < 2 || t.length > 80) return null;
  if (/^\d/.test(t)) return null;
  return t;
}

export function normalizeTranslation(
  raw: string | null | undefined
): string | null {
  if (raw == null) return null;
  const t = collapse(raw);
  const m = t.match(
    /(?:пераклад|перевод|przekład|tłumaczenie|translated?\s+from|translation\s+from)\s*(?:з|с|z|from)?\s*([^\n,;.|]{2,40})/i
  );
  if (m) {
    const lang = m[1].trim().replace(/^[,;.:]+|[,;.:]+$/g, '');
    if (lang.length >= 2 && lang.length <= 40) {
      return `з ${normalizeLanguage(lang) || lang}`;
    }
  }
  if (
    /пераклад|перевод|przekład|tłumaczen|translat/i.test(t) &&
    t.length <= 80
  ) {
    return t;
  }
  return null;
}

export function normalizeYear(raw: string | null | undefined): number | null {
  if (raw == null) return null;
  const t = collapse(raw);
  const labeled = t.match(
    /(?:год(?:\s+выдання)?|rok(?:\s+wydania)?|year(?:\s+of\s+publication)?|wydanie|издание|выданне)\s*:?\s*((?:19|20)\d{2})\b/i
  );
  if (labeled) {
    const y = Number(labeled[1]);
    if (Number.isFinite(y) && y >= 1800 && y <= 2100) return y;
  }
  if (/^\d{4}$/.test(t)) {
    const y = Number(t);
    if (Number.isFinite(y) && y >= 1800 && y <= 2100) return y;
  }
  if (/год|rok|year|выдан|издан|wydan/i.test(t)) {
    const bare = t.match(/\b((?:19|20)\d{2})\b/);
    if (bare) {
      const y = Number(bare[1]);
      if (Number.isFinite(y) && y >= 1800 && y <= 2100) return y;
    }
  }
  return null;
}

export function isFormatAttributeLabel(
  taxonomy: string,
  attrName: string
): boolean {
  return /format|фармат|формат|размер|памер|size|wymiar/i.test(
    `${taxonomy} ${attrName}`
  );
}

export function isIllustratorAttributeLabel(
  taxonomy: string,
  attrName: string
): boolean {
  return /illustrat|ilustrat|ілюстрат|иллюстрат|мастак|художник/i.test(
    `${taxonomy} ${attrName}`
  );
}

export function isLanguageAttributeLabel(
  taxonomy: string,
  attrName: string
): boolean {
  return /language|lang|мова|язык|język|jezyk/i.test(`${taxonomy} ${attrName}`);
}

export function isPageCountAttributeLabel(
  taxonomy: string,
  attrName: string
): boolean {
  return /page|pages|старон|страниц|stron|колькасць стар|количество стр/i.test(
    `${taxonomy} ${attrName}`
  );
}

export function isPlaceAttributeLabel(
  taxonomy: string,
  attrName: string
): boolean {
  return /place|city|месца|место|miejsce|город|горад/i.test(
    `${taxonomy} ${attrName}`
  );
}

export function isTranslationAttributeLabel(
  taxonomy: string,
  attrName: string
): boolean {
  return /transl|пераклад|перевод|przekład|przeklad|tłumacz|tlumacz/i.test(
    `${taxonomy} ${attrName}`
  );
}

export function isYearAttributeLabel(
  taxonomy: string,
  attrName: string
): boolean {
  return /year|год|rok|выдан|издан|wydan/i.test(`${taxonomy} ${attrName}`);
}

export function languageFromOcrCode(
  code: string | null | undefined
): string | null {
  const c = String(code ?? '')
    .trim()
    .toLowerCase();
  if (c === 'be') return 'беларуская';
  if (c === 'pl') return 'польская';
  if (c === 'ru') return 'руская';
  if (c === 'en') return 'англійская';
  if (c === 'uk') return 'украінская';
  if (c === 'lt') return 'літоўская';
  return normalizeLanguage(code);
}
