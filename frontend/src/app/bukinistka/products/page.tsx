import BukinistkaProductsClient from '@/components/bukinistka/BukinistkaProductsClient';
import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'Прадукты | Bukinistka',
};

export default function BukinistkaProductsPage() {
  return <BukinistkaProductsClient />;
}
