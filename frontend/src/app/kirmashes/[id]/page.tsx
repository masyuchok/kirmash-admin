import KirmashEditorClient from '@/components/kirmashes/KirmashEditorClient';

type Props = {
  params: Promise<{ id: string }>;
};

export default async function KirmashDetailPage({ params }: Props) {
  const { id } = await params;
  const numericId = Number(id);
  if (!Number.isFinite(numericId) || numericId <= 0) {
    return (
      <div className="p-6 text-sm text-red-600">Няправільны id кірмаша.</div>
    );
  }
  return <KirmashEditorClient kirmashId={numericId} />;
}
