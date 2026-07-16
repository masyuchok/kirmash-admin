import SupplyProductPickerClient from '@/components/supplies/SupplyProductPickerClient';

type Props = {
  searchParams: Promise<{
    supplyId?: string;
    supplierId?: string;
    supplierName?: string;
    date?: string;
    selectedProductIds?: string;
    selectedProductQuantities?: string;
  }>;
};

export default async function SupplyProductsPage({ searchParams }: Props) {
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

  return (
    <SupplyProductPickerClient
      supplyId={typeof params.supplyId === 'string' ? params.supplyId : ''}
      supplierId={
        typeof params.supplierId === 'string' ? params.supplierId : ''
      }
      supplierName={
        typeof params.supplierName === 'string' ? params.supplierName : ''
      }
      date={typeof params.date === 'string' ? params.date : ''}
      selectedProductIds={selectedProductIds}
      selectedProductQuantities={selectedProductQuantities}
    />
  );
}
