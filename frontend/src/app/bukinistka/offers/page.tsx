import BukinistkaOffersClient from '@/components/bukinistka/BukinistkaOffersClient';
import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'Прапановы ад Кирмаша | Bukinistka',
};

export default function BukinistkaOffersPage() {
  return <BukinistkaOffersClient />;
}
