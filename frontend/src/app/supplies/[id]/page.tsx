import NewSupplyClient from '@/components/supplies/NewSupplyClient';

type Props = {
  params: Promise<{ id: string }>;
  searchParams: Promise<{
    supplierId?: string;
    supplierName?: string;
    date?: string;
    selectedProductIds?: string;
  }>;
};

export default async function SupplyDetailsPage({ params, searchParams }: Props) {
  const { id } = await params;
  const query = await searchParams;
  const selectedProductIds =
    typeof query.selectedProductIds === 'string' && query.selectedProductIds.trim()
      ? query.selectedProductIds.split(',').map((x) => x.trim()).filter(Boolean)
      : [];
  return (
    <NewSupplyClient
      supplyId={id}
      initialSupplierId={typeof query.supplierId === 'string' ? query.supplierId : ''}
      initialSupplierName={typeof query.supplierName === 'string' ? query.supplierName : ''}
      initialDate={typeof query.date === 'string' ? query.date : ''}
      selectedProductIds={selectedProductIds}
    />
  );
}
