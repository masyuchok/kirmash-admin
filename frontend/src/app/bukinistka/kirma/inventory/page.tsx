import BukinistkaInventoryClient from '@/components/bukinistka/BukinistkaInventoryClient';
import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'Інвентарызацыя | Kirma.sh | Bukinistka',
};

export default function BukinistkaKirmaInventoryPage() {
  return <BukinistkaInventoryClient />;
}
