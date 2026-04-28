import NewSupplyClient from '@/components/supplies/NewSupplyClient';

type Props = {
  searchParams: Promise<{
    supplierId?: string;
    date?: string;
  }>;
};

export default async function NewSupplyPage({ searchParams }: Props) {
  const params = await searchParams;
  return (
    <NewSupplyClient
      initialSupplierId={typeof params.supplierId === 'string' ? params.supplierId : ''}
      initialSupplierName=""
      initialDate={typeof params.date === 'string' ? params.date : ''}
    />
  );
}
