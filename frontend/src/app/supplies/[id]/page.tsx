import NewSupplyClient from '@/components/supplies/NewSupplyClient';

type Props = {
  params: Promise<{ id: string }>;
  searchParams: Promise<{
    supplierName?: string;
    date?: string;
  }>;
};

export default async function SupplyDetailsPage({ params, searchParams }: Props) {
  const { id } = await params;
  const query = await searchParams;
  return (
    <NewSupplyClient
      supplyId={id}
      initialSupplierName={typeof query.supplierName === 'string' ? query.supplierName : ''}
      initialDate={typeof query.date === 'string' ? query.date : ''}
    />
  );
}
