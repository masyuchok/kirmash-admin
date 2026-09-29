import NewSupplyClient from '@/components/supplies/NewSupplyClient';
import { parseSelectedProductPrices } from '@/lib/supply-picker-pending';

type Props = {
  searchParams: Promise<{
    supplierId?: string;
    date?: string;
    selectedProductIds?: string;
    selectedProductQuantities?: string;
    selectedProductPrices?: string;
    restoreDraft?: string;
  }>;
};

export default async function NewSupplyPage({ searchParams }: Props) {
  const params = await searchParams;
  const selectedProductIds =
    typeof params.selectedProductIds === 'string' &&
    params.selectedProductIds.trim()
      ? params.selectedProductIds
          .split(',')
          .map((x) => x.trim())
          .filter(Boolean)
      : [];
  let selectedProductQuantities: Record<string, string> = {};
  if (
    typeof params.selectedProductQuantities === 'string' &&
    params.selectedProductQuantities.trim()
  ) {
    try {
      const parsed = JSON.parse(params.selectedProductQuantities) as Record<
        string,
        unknown
      >;
      selectedProductQuantities = Object.fromEntries(
        Object.entries(parsed).map(([key, value]) => [
          key,
          typeof value === 'string' ? value : String(value ?? ''),
        ])
      );
    } catch {
      selectedProductQuantities = {};
    }
  }
  const selectedProductPrices = parseSelectedProductPrices(
    typeof params.selectedProductPrices === 'string'
      ? params.selectedProductPrices
      : undefined
  );
  const restoreDraft = params.restoreDraft === '1';
  return (
    <NewSupplyClient
      initialSupplierId={
        typeof params.supplierId === 'string' ? params.supplierId : ''
      }
      initialSupplierName=""
      initialDate={typeof params.date === 'string' ? params.date : ''}
      selectedProductIds={selectedProductIds}
      selectedProductQuantities={selectedProductQuantities}
      selectedProductPrices={selectedProductPrices}
      restoreDraft={restoreDraft}
    />
  );
}
