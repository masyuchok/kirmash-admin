import BukinistkaOffersClient from '@/components/bukinistka/BukinistkaOffersClient';
import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'Прапановы | Kirma.sh | Bukinistka',
};

export default function BukinistkaKirmaOffersPage() {
  return <BukinistkaOffersClient />;
}
