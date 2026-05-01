import { notFound } from 'next/navigation';
import EditSupplierClient from '@/components/suppliers/EditSupplierClient';

type PageProps = {
  params: Promise<{ id: string }>;
};

export default async function EditSupplierPage({ params }: PageProps) {
  const { id } = await params;
  const numericId = Number(id);
  if (!Number.isFinite(numericId) || numericId <= 0) {
    notFound();
  }
  return <EditSupplierClient supplierId={numericId} />;
}
