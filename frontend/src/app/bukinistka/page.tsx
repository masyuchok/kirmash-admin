import BukinistkaHomeClient from '@/components/bukinistka/BukinistkaHomeClient';
import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'Bukinistka | Kirma.sh',
};

export default function BukinistkaPage() {
  return <BukinistkaHomeClient />;
}
