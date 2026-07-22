import BukinistykaClient from '@/components/bukinistyka/BukinistykaClient';
import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'Букіністка | Kirma',
};

export default function BukinistykaPage() {
  return <BukinistykaClient />;
}
