import BukinistkaKirmaTabs from '@/components/bukinistka/BukinistkaKirmaTabs';

export default function BukinistkaKirmaLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return <BukinistkaKirmaTabs>{children}</BukinistkaKirmaTabs>;
}
