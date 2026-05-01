import ReportDetailsClient from '@/components/documents/ReportDetailsClient';

export default async function ReportDetailsPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;
  const reportId = Number(id);
  if (!Number.isFinite(reportId) || reportId <= 0) {
    return (
      <div className="mx-auto w-full max-w-4xl rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
        Некарэктны ID справаздачы.
      </div>
    );
  }
  return <ReportDetailsClient reportId={reportId} />;
}
