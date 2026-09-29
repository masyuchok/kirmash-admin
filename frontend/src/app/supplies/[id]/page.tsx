import NewSupplyClient from '@/components/supplies/NewSupplyClient';
import { parseSelectedProductPrices } from '@/lib/supply-picker-pending';

type Props = {
  params: Promise<{ id: string }>;
  searchParams: Promise<{
    supplierId?: string;
    supplierName?: string;
    date?: string;
    selectedProductIds?: string;
    selectedProductQuantities?: string;
    selectedProductPrices?: string;
    restoreDraft?: string;
  }>;
};

export default async function SupplyDetailsPage({
  params,
  searchParams,
}: Props) {
  const { id } = await params;
  const query = await searchParams;
  const selectedProductIds =
    typeof query.selectedProductIds === 'string' &&
    query.selectedProductIds.trim()
      ? query.selectedProductIds
          .split(',')
          .map((x) => x.trim())
          .filter(Boolean)
      : [];
  let selectedProductQuantities: Record<string, string> = {};
  if (
    typeof query.selectedProductQuantities === 'string' &&
    query.selectedProductQuantities.trim()
  ) {
    try {
      const parsed = JSON.parse(query.selectedProductQuantities) as Record<
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
    typeof query.selectedProductPrices === 'string'
      ? query.selectedProductPrices
      : undefined
  );
  const restoreDraft = query.restoreDraft === '1';
  return (
    <NewSupplyClient
      supplyId={id}
      initialSupplierId={
        typeof query.supplierId === 'string' ? query.supplierId : ''
      }
      initialSupplierName={
        typeof query.supplierName === 'string' ? query.supplierName : ''
      }
      initialDate={typeof query.date === 'string' ? query.date : ''}
      selectedProductIds={selectedProductIds}
      selectedProductQuantities={selectedProductQuantities}
      selectedProductPrices={selectedProductPrices}
      restoreDraft={restoreDraft}
    />
  );
}
