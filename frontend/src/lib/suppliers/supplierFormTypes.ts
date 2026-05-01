import type { Supplier } from '@/types/supplier';

export type SupplierFormValues = {
  name: string;
  contactName: string;
  website: string;
  country: string;
  city: string;
  currency: string;
  workStart: string;
  isVATPayer: boolean;
  email: string;
  instagram: string;
  phone: string;
  tgContact: string;
};

export function defaultEmptySupplierForm(): SupplierFormValues {
  return {
    name: '',
    contactName: '',
    website: '',
    country: '',
    city: '',
    currency: 'PLN',
    workStart: new Date().toISOString().split('T')[0],
    isVATPayer: false,
    email: '',
    instagram: '',
    phone: '',
    tgContact: '',
  };
}

/** Stable string for dirty-checking the whole form. */
export function serializeSupplierForm(v: SupplierFormValues): string {
  return JSON.stringify(v);
}

/** Prefill from list row when detail API is missing fields. */
export function mapListSupplierToFormValues(s: Supplier): SupplierFormValues {
  const base = defaultEmptySupplierForm();
  return {
    ...base,
    name: s.name ?? '',
    website: s.website ?? '',
    country: s.country ?? '',
    city: s.city ?? '',
    tgContact: s.telegram ?? '',
    isVATPayer: Boolean(s.isVatPayer),
  };
}

/**
 * Merge GET /suppliers/:id (or future shape) into form values.
 * Accepts alternate keys from API (`telegram`, `isVatPayer`).
 */
export function mapApiDetailToFormValues(
  detail: Partial<SupplierFormValues> & {
    id: number;
    telegram?: string;
    isVatPayer?: boolean;
  }
): SupplierFormValues {
  const base = defaultEmptySupplierForm();
  return {
    name: detail.name ?? base.name,
    contactName: detail.contactName ?? base.contactName,
    website: detail.website ?? base.website,
    country: detail.country ?? base.country,
    city: detail.city ?? base.city,
    currency: detail.currency ?? base.currency,
    workStart: detail.workStart ?? base.workStart,
    isVATPayer: detail.isVATPayer ?? detail.isVatPayer ?? base.isVATPayer,
    email: detail.email ?? base.email,
    instagram: detail.instagram ?? base.instagram,
    phone: detail.phone ?? base.phone,
    tgContact: detail.tgContact ?? detail.telegram ?? base.tgContact,
  };
}
