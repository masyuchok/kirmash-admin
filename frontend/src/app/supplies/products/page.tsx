import SupplyProductPickerClient from '@/components/supplies/SupplyProductPickerClient';

type Props = {
  searchParams: Promise<{
    supplyId?: string;
    supplierId?: string;
    supplierName?: string;
    date?: string;
    selectedProductIds?: string;
  }>;
};

export default async function SupplyProductsPage({ searchParams }: Props) {
  const params = await searchParams;
  const selectedProductIds =
    typeof params.selectedProductIds === 'string' && params.selectedProductIds.trim()
      ? params.selectedProductIds.split(',').map((x) => x.trim()).filter(Boolean)
      : [];

  return (
    <SupplyProductPickerClient
      supplyId={typeof params.supplyId === 'string' ? params.supplyId : ''}
      supplierId={typeof params.supplierId === 'string' ? params.supplierId : ''}
      supplierName={typeof params.supplierName === 'string' ? params.supplierName : ''}
      date={typeof params.date === 'string' ? params.date : ''}
      selectedProductIds={selectedProductIds}
    />
  );
}
