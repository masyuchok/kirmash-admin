/**
 * Normalize age recommendation labels (cover / shop / price list) to "0+", "18+", etc.
 */
export function normalizeAgeRating(
  raw: string | null | undefined
): string | null {
  if (raw == null) return null;
  const t = String(raw).trim().replace(/\s+/g, ' ');
  if (!t) return null;

  if (
    /только\s+для\s+взрослых|толькі\s+для\s+дарослых|adults?\s+only|dla\s+dorosłych|18\s*years?\s*and\s*(?:over|older)/i.test(
      t
    )
  ) {
    return '18+';
  }

  const plus = t.match(/\b(\d{1,2})\s*\+/);
  if (plus) {
    const age = Number(plus[1]);
    if (Number.isFinite(age) && age >= 0 && age <= 21) return `${age}+`;
  }

  const fromAge = t.match(
    /(?:для\s+(?:детей|дзяцей|детей)|od\s+lat|от|ад|from|wiek|возр[аa]ст|ўзрост|возраст)\s*:?\s*(\d{1,2})\s*\+?/i
  );
  if (fromAge) {
    const age = Number(fromAge[1]);
    if (Number.isFinite(age) && age >= 0 && age <= 21) return `${age}+`;
  }

  if (
    /возраст|ўзрост|узрост|wiek|age\b|рекоменд|рэкаменд|для\s+(?:детей|дзяцей)|od\s+lat/i.test(
      t
    )
  ) {
    const years = t.match(
      /\b(\d{1,2})\s*(?:лет|года|год|рок[іi]ў?|lat|years?|yrs?)\b/i
    );
    if (years) {
      const age = Number(years[1]);
      if (Number.isFinite(age) && age >= 0 && age <= 21) return `${age}+`;
    }
  }

  return null;
}

export function isAgeAttributeLabel(
  taxonomy: string,
  attrName: string
): boolean {
  const tax = taxonomy || '';
  const name = attrName || '';
  return (
    /age|wiek|vozrast|uzrost/i.test(tax) ||
    /возраст|ўзрост|узрост|wiek|age|рекоменд|рэкаменд|для кого|для каго/i.test(
      name
    )
  );
}
