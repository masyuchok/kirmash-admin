/** Supplier row returned by GET /suppliers (list). */
export type Supplier = {
  id: number;
  name: string;
  telegram: string;
  website: string;
  country: string;
  city: string;
  isVatPayer: boolean;
};
