import NewSupplyClient from '@/components/supplies/NewSupplyClient';

type Props = {
  searchParams: Promise<{
    supplierId?: string;
    date?: string;
    selectedProductIds?: string;
  }>;
};

export default async function NewSupplyPage({ searchParams }: Props) {
  const params = await searchParams;
  const selectedProductIds =
    typeof params.selectedProductIds === 'string' && params.selectedProductIds.trim()
      ? params.selectedProductIds.split(',').map((x) => x.trim()).filter(Boolean)
      : [];
  return (
    <NewSupplyClient
      initialSupplierId={typeof params.supplierId === 'string' ? params.supplierId : ''}
      initialSupplierName=""
      initialDate={typeof params.date === 'string' ? params.date : ''}
      selectedProductIds={selectedProductIds}
    />
  );
}
